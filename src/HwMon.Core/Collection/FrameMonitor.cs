using System.Diagnostics;
using System.Runtime.InteropServices;
using HwMon.Core.Telemetry;

namespace HwMon.Core.Collection;

/// <summary>
/// Quadros por segundo de cada programa que desenha com DirectX (DXGI: DX10/11/12, e Vulkan/OpenGL quando o driver
/// apresenta por DXGI, o padrão da NVIDIA) ou Direct3D 9, pelo mesmo método do PresentMon da Intel: os eventos
/// "Present" que o próprio Windows emite (ETW). Nada é injetado no jogo, então não há risco com anticheat.
/// Precisa de administrador/SISTEMA (o coletor roda como SISTEMA). A sessão ETW fica ligada enquanto alguém
/// pergunta: o gravador de jogos pergunta a cada segundo, então no serviço ela fica sempre ligada; sem o
/// gravador (ex.: comando "quadros"), 60 s sem perguntas e ela é encerrada.
/// Frametime aqui = intervalo entre dois Present da aplicação (o "MsBetweenPresents" do PresentMon).
/// </summary>
public sealed class FrameMonitor : IDisposable
{
    private const string SessionName = "HardwareWatch-Frames";
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private const ushort DxgiPresentStart = 42;
    private const ushort D3D9PresentStart = 1;
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan IdleStop = TimeSpan.FromSeconds(60);

    private readonly object _lock = new();
    private readonly Dictionary<(int Pid, ulong SwapChain), Queue<long>> _presents = new();
    private readonly Action<string>? _log;
    private Thread? _thread;
    private ulong _sessionHandle;
    private ulong _traceHandle;
    private DateTime _lastQuery;
    private DateTime _startedAt;
    private string? _error;
    private EventRecordCallback? _callback; // mantido vivo enquanto o Windows chama
    private Timer? _idleTimer;
    private DateTime _lastAttempt;
    private readonly Dictionary<int, Dictionary<ulong, FrameCapture>> _captures = new();

    public FrameMonitor(Action<string>? log = null) => _log = log;

    // ------------------------------------------------------------------ captura de uma sessão inteira

    /// <summary>
    /// Todos os quadros de um processo desde <see cref="BeginCapture"/> (não só os últimos 10 s): contagem, soma e
    /// histograma dos frametimes em passos de 0,1 ms, para FPS médio e 1 % low da sessão toda sem guardar cada quadro.
    /// </summary>
    public sealed class FrameCapture
    {
        public const int Bins = 10000; // 0–1000 ms
        public readonly uint[] Histogram = new uint[Bins];
        public long Frames;
        public double SumMs;
        public double MaxMs;
        internal long LastStamp;

        public FrameCapture Clone()
        {
            var c = new FrameCapture { Frames = Frames, SumMs = SumMs, MaxMs = MaxMs };
            Array.Copy(Histogram, c.Histogram, Bins);
            return c;
        }

        /// <summary>Frametime (ms) no percentil p (0–1).</summary>
        public double Percentile(double p)
        {
            if (Frames == 0) return 0;
            var target = (long)Math.Ceiling(p * Frames);
            long seen = 0;
            for (var i = 0; i < Bins; i++)
            {
                seen += Histogram[i];
                if (seen >= target) return (i + 0.5) / 10;
            }
            return MaxMs;
        }

        /// <summary>Quadros acima de <paramref name="ms"/>.</summary>
        public long CountAbove(double ms)
        {
            long n = 0;
            for (var i = Math.Clamp((int)(ms * 10), 0, Bins); i < Bins; i++) n += Histogram[i];
            return n;
        }
    }

    /// <summary>Começa a acumular os quadros do processo (o coletor de sessões de jogo chama ao ver um candidato).</summary>
    public void BeginCapture(int pid)
    {
        lock (_lock) _captures.TryAdd(pid, new Dictionary<ulong, FrameCapture>());
    }

    /// <summary>Cópia do acumulado (a cadeia de troca com mais quadros, se o processo tiver várias).</summary>
    public FrameCapture? Capture(int pid)
    {
        lock (_lock)
            return _captures.TryGetValue(pid, out var chains) && chains.Count > 0
                ? chains.Values.MaxBy(c => c.Frames)!.Clone()
                : null;
    }

    public void EndCapture(int pid)
    {
        lock (_lock) _captures.Remove(pid);
    }

    /// <summary>Programas que apresentaram quadros nos últimos 2 s (ou só <paramref name="pid"/>).</summary>
    public FramesResponse Query(int? pid)
    {
        _lastQuery = DateTime.UtcNow;
        EnsureRunning();
        var response = new FramesResponse
        {
            Running = _thread is { IsAlive: true },
            Error = _error,
            WarmingUp = DateTime.UtcNow - _startedAt < TimeSpan.FromSeconds(2),
        };
        var now = Stopwatch.GetTimestamp();
        var freq = (double)Stopwatch.Frequency;
        lock (_lock)
        {
            foreach (var group in _presents.Where(kv => pid is null || kv.Key.Pid == pid).GroupBy(kv => kv.Key.Pid))
            {
                // Mais de uma cadeia de troca no processo (ex.: launcher + jogo): fica a que mais apresenta.
                var best = group.Select(kv => kv.Value).Where(q => q.Count > 1).OrderByDescending(q => q.Count(t => (now - t) / freq <= 1)).FirstOrDefault();
                if (best is null) continue;
                var times = best.ToArray();
                if ((now - times[^1]) / freq > 2) continue; // parou de desenhar
                var frametimes = times.Zip(times.Skip(1), (a, b) => (b - a) / freq * 1000).Where(ms => ms > 0 && ms < 1000).ToArray();
                if (frametimes.Length == 0) continue;
                var lastSecond = times.Where(t => (now - t) / freq <= 1).ToArray();
                var recent = lastSecond.Zip(lastSecond.Skip(1), (a, b) => (b - a) / freq * 1000).ToArray();
                var avg = recent.Length > 0 ? recent.Average() : frametimes[^Math.Min(frametimes.Length, 30)..].Average();
                // Rajadas soltas (jogo minimizado ou em segundo plano soltando 2–3 quadros juntos de vez em quando):
                // a média dos intervalos daria centenas de FPS; vale o que de fato saiu no último segundo.
                if (lastSecond.Length < 1000 / avg * 0.5) avg = 1000.0 / Math.Max(1, lastSecond.Length);
                var sorted = frametimes.OrderBy(x => x).ToArray();
                double Percentile(double p) => sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
                response.Processes.Add(new FrameStats
                {
                    Pid = group.Key,
                    Name = ProcessName(group.Key),
                    Fps = Math.Round(1000 / avg, 1),
                    FrametimeMs = Math.Round(avg, 2),
                    Low1Fps = Math.Round(1000 / Percentile(0.99), 1),
                    Low01Fps = Math.Round(1000 / Percentile(0.999), 1),
                    MaxFrametimeMs = Math.Round(sorted[^1], 2),
                    Frametimes = frametimes[^Math.Min(frametimes.Length, 300)..].Select(x => Math.Round(x, 2)).ToList(),
                    WindowSeconds = Math.Round((times[^1] - times[0]) / freq, 1),
                });
            }
        }
        response.Processes = response.Processes.OrderByDescending(p => p.Fps).ToList();
        return response;
    }

    private static readonly Dictionary<int, string> Names = new();

    private static string ProcessName(int pid)
    {
        lock (Names)
        {
            if (Names.TryGetValue(pid, out var n)) return n;
            try
            {
                using var p = Process.GetProcessById(pid);
                n = p.ProcessName;
            }
            catch (ArgumentException)
            {
                n = $"processo {pid}";
            }
            Names[pid] = n;
            return n;
        }
    }

    // ------------------------------------------------------------------ sessão ETW

    private void EnsureRunning()
    {
        lock (_lock)
        {
            if (_thread is { IsAlive: true }) return;
            // Falhou (ex.: sem permissão): tenta de novo só a cada minuto, sem inundar o log.
            if (_error is not null && DateTime.UtcNow - _lastAttempt < TimeSpan.FromMinutes(1)) return;
            _lastAttempt = DateTime.UtcNow;
            _error = null;
            _presents.Clear();
            try
            {
                Start();
                _startedAt = DateTime.UtcNow;
                _idleTimer ??= new Timer(_ =>
                {
                    if (_thread is { IsAlive: true } && DateTime.UtcNow - _lastQuery > IdleStop) Stop();
                }, null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(15));
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                _log?.Invoke($"Quadros (ETW): {ex.Message}");
            }
        }
    }

    private void Start()
    {
        var props = Properties();
        try
        {
            var status = StartTraceW(out _sessionHandle, SessionName, props);
            if (status == 183) // já existe (sobrou de uma execução anterior): encerra e cria de novo
            {
                ControlTraceW(0, SessionName, props, 1);
                Marshal.FreeHGlobal(props);
                props = Properties();
                status = StartTraceW(out _sessionHandle, SessionName, props);
            }
            if (status != 0) throw new InvalidOperationException(status == 5
                ? "Sem permissão para ler os quadros (precisa do coletor rodando como serviço/administrador)."
                : $"Não foi possível iniciar a sessão de eventos (erro {status}).");
        }
        finally
        {
            Marshal.FreeHGlobal(props);
        }

        foreach (var provider in new[] { DxgiProvider, D3D9Provider })
        {
            var guid = provider;
            var status = EnableTraceEx2(_sessionHandle, ref guid, 1 /* ENABLE */, 5 /* VERBOSE */, ulong.MaxValue, 0, 0, IntPtr.Zero);
            if (status != 0) _log?.Invoke($"Quadros (ETW): provedor {provider} não ligou (erro {status}).");
        }

        _callback = OnEvent;
        var logfile = new EventTraceLogfile
        {
            LoggerName = SessionName,
            ProcessTraceMode = 0x100 | 0x10000000, // REAL_TIME | EVENT_RECORD
            EventRecordCallback = _callback,
        };
        _traceHandle = OpenTraceW(ref logfile);
        if (_traceHandle == ulong.MaxValue) throw new InvalidOperationException($"Não foi possível abrir a sessão de eventos (erro {Marshal.GetLastWin32Error()}).");

        var handle = _traceHandle;
        _thread = new Thread(() =>
        {
            var handles = new[] { handle };
            ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero); // bloqueia até a sessão parar
        }) { IsBackground = true, Name = "frames-etw" };
        _thread.Start();
        _log?.Invoke("Quadros (ETW): sessão iniciada.");
    }

    private void Stop()
    {
        lock (_lock)
        {
            var props = Properties();
            try
            {
                ControlTraceW(_sessionHandle, null, props, 1 /* STOP */);
            }
            finally
            {
                Marshal.FreeHGlobal(props);
            }
            if (_traceHandle != 0) CloseTrace(_traceHandle);
            _traceHandle = 0;
            _sessionHandle = 0;
            _presents.Clear();
        }
        _log?.Invoke("Quadros (ETW): sessão encerrada (ninguém consultando).");
    }

    private void OnEvent(ref EventRecord record)
    {
        var provider = record.EventHeader.ProviderId;
        var id = record.EventHeader.EventDescriptor.Id;
        if (!((provider == DxgiProvider && id == DxgiPresentStart) || (provider == D3D9Provider && id == D3D9PresentStart))) return;
        var pid = (int)record.EventHeader.ProcessId;
        if (pid == Environment.ProcessId) return;
        // 1º campo do evento: ponteiro da cadeia de troca (32 ou 64 bits, conforme a flag do cabeçalho).
        ulong swapChain = 0;
        if (record.UserDataLength >= 4 && record.UserData != IntPtr.Zero)
            swapChain = (record.EventHeader.Flags & 0x0020) == 0 /* sem EVENT_HEADER_FLAG_32_BIT_HEADER */ && record.UserDataLength >= 8
                ? (ulong)Marshal.ReadInt64(record.UserData)
                : (uint)Marshal.ReadInt32(record.UserData);
        var stamp = record.EventHeader.TimeStamp; // QPC (ClientContext = 1)
        lock (_lock)
        {
            if (!_presents.TryGetValue((pid, swapChain), out var q)) _presents[(pid, swapChain)] = q = new Queue<long>(256);
            q.Enqueue(stamp);
            if (_captures.TryGetValue(pid, out var chains))
            {
                if (!chains.TryGetValue(swapChain, out var cap)) chains[swapChain] = cap = new FrameCapture();
                if (cap.LastStamp != 0)
                {
                    var ms = (stamp - cap.LastStamp) * 1000.0 / Stopwatch.Frequency;
                    if (ms > 0 && ms < 1000) // pausas longas (minimizado, carregando) não são quadros
                    {
                        cap.Frames++;
                        cap.SumMs += ms;
                        if (ms > cap.MaxMs) cap.MaxMs = ms;
                        cap.Histogram[Math.Min(FrameCapture.Bins - 1, (int)(ms * 10))]++;
                    }
                }
                cap.LastStamp = stamp;
            }
            var limit = stamp - (long)(Window.TotalSeconds * Stopwatch.Frequency);
            while (q.Count > 0 && q.Peek() < limit) q.Dequeue();
            // Processos que pararam: limpa de vez em quando.
            if (_presents.Count > 64)
                foreach (var key in _presents.Where(kv => kv.Value.Count == 0 || kv.Value.Peek() < limit).Select(kv => kv.Key).ToList())
                    _presents.Remove(key);
        }
    }

    /// <summary>Diagnóstico: tamanho da estrutura passada ao OpenTraceW (x64 = 448 bytes).</summary>
    public static int LogfileStructSize => Marshal.SizeOf<EventTraceLogfile>();

    public void Dispose()
    {
        _idleTimer?.Dispose();
        if (_thread is { IsAlive: true }) Stop();
    }

    // ------------------------------------------------------------------ P/Invoke (advapi32)

    /// <summary>EVENT_TRACE_PROPERTIES + espaço para o nome da sessão.</summary>
    private static IntPtr Properties()
    {
        const int propsSize = 120;
        const int total = propsSize + 1024;
        var p = Marshal.AllocHGlobal(total);
        for (var i = 0; i < total; i++) Marshal.WriteByte(p, i, 0);
        Marshal.WriteInt32(p, 0, total);                 // Wnode.BufferSize
        Marshal.WriteInt32(p, 40, 1);                    // Wnode.ClientContext = QPC
        Marshal.WriteInt32(p, 44, 0x00020000);           // Wnode.Flags = WNODE_FLAG_TRACED_GUID
        Marshal.WriteInt32(p, 48, 64);                   // BufferSize (KB)
        Marshal.WriteInt32(p, 52, 4);                    // MinimumBuffers
        Marshal.WriteInt32(p, 56, 32);                   // MaximumBuffers
        Marshal.WriteInt32(p, 64, 0x00000100);           // LogFileMode = REAL_TIME
        Marshal.WriteInt32(p, 68, 1);                    // FlushTimer (s)
        Marshal.WriteInt32(p, 116, propsSize);           // LoggerNameOffset
        var guid = Guid.NewGuid().ToByteArray();
        Marshal.Copy(guid, 0, p + 24, 16);               // Wnode.Guid
        return p;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventDescriptor
    {
        public ushort Id;
        public byte Version, Channel, Level, Opcode;
        public ushort Task;
        public ulong Keyword;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventHeader
    {
        public ushort Size, HeaderType, Flags, EventProperty;
        public uint ThreadId, ProcessId;
        public long TimeStamp;
        public Guid ProviderId;
        public EventDescriptor EventDescriptor;
        public ulong ProcessorTime;
        public Guid ActivityId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventRecord
    {
        public EventHeader EventHeader;
        public uint BufferContext; // ProcessorNumber, Alignment, LoggerId
        public ushort ExtendedDataCount, UserDataLength;
        public IntPtr ExtendedData, UserData, UserContext;
    }

    private delegate void EventRecordCallback(ref EventRecord record);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SystemTime { public short Year, Month, DayOfWeek, Day, Hour, Minute, Second, Milliseconds; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TimeZoneInformation
    {
        public int Bias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string StandardName;
        public SystemTime StandardDate;
        public int StandardBias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DaylightName;
        public SystemTime DaylightDate;
        public int DaylightBias;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TraceLogfileHeader
    {
        public uint BufferSize, Version, ProviderVersion, NumberOfProcessors;
        public long EndTime;
        public uint TimerResolution, MaximumFileSize, LogFileMode, BuffersWritten;
        public Guid LogInstanceGuid;
        public IntPtr LoggerName, LogFileName;
        public TimeZoneInformation TimeZone;
        public long BootTime, PerfFreq, StartTime;
        public uint ReservedFlags, BuffersLost;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTraceHeader
    {
        public ushort Size, FieldTypeFlags;
        public uint Version;
        public uint ThreadId, ProcessId;
        public long TimeStamp;
        public Guid Guid;
        public ulong KernelUserTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTrace
    {
        public EventTraceHeader Header;
        public uint InstanceId, ParentInstanceId;
        public Guid ParentGuid;
        public IntPtr MofData;
        public uint MofLength, ClientContext;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct EventTraceLogfile
    {
        public string? LogFileName;
        public string LoggerName;
        public long CurrentTime;
        public uint BuffersRead;
        public uint ProcessTraceMode;
        public EventTrace CurrentEvent;
        public TraceLogfileHeader LogfileHeader;
        public IntPtr BufferCallback;
        public uint BufferSize, Filled, EventsLost;
        public EventRecordCallback EventRecordCallback;
        public uint IsKernelTrace;
        public IntPtr Context;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int StartTraceW(out ulong handle, string name, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int ControlTraceW(ulong handle, string? name, IntPtr properties, uint code);

    [DllImport("advapi32.dll")]
    private static extern int EnableTraceEx2(ulong handle, ref Guid provider, uint control, byte level, ulong matchAny, ulong matchAll, uint timeout, IntPtr parameters);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ulong OpenTraceW(ref EventTraceLogfile logfile);

    [DllImport("advapi32.dll")]
    private static extern int ProcessTrace(ulong[] handles, uint count, IntPtr start, IntPtr end);

    [DllImport("advapi32.dll")]
    private static extern int CloseTrace(ulong handle);
}
