using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using HwMon.Core.Telemetry;

namespace HwMon.Core.Collection;

/// <summary>
/// Teste de disco no estilo do CrystalDiskMark, com os mesmos quatro testes (SEQ1M Q8T1, SEQ1M Q1T1, RND4K Q32T1,
/// RND4K Q1T1), leitura e escrita. O arquivo de teste é escrito sem o cache do Windows (FILE_FLAG_NO_BUFFERING) com
/// dados aleatórios, e as operações vão para o disco em paralelo (fila de N pedidos assíncronos, 1 thread), como o
/// "Q8T1" do CDM. MB = 1.000.000 bytes, igual ao CDM. O arquivo é apagado no fim (também se cancelar ou der erro).
/// </summary>
public static class DiskBenchmark
{
    public sealed record Test(string Name, int Block, int Queue, bool Random);

    public static readonly Test[] Tests =
    [
        new("SEQ1M Q8T1", 1 << 20, 8, false),
        new("SEQ1M Q1T1", 1 << 20, 1, false),
        new("RND4K Q32T1", 4096, 32, true),
        new("RND4K Q1T1", 4096, 1, true),
    ];

    private const FileOptions NoBuffering = (FileOptions)0x20000000;
    /// <summary>Prefixo do arquivo de teste; cada execução usa um nome novo (nunca o de um arquivo que já existe).</summary>
    public const string FilePrefix = "Corewise-benchmark-";
    private const FileOptions DeleteOnClose = (FileOptions)0x04000000;

    /// <summary>
    /// <paramref name="live"/>: linhas já medidas (cópia) e a medição em andamento (teste, escrita?, MB/s até agora),
    /// para o painel mostrar os números aparecendo durante o teste.
    /// </summary>
    public static async Task<DiskBenchResult> RunAsync(DiskBenchRequest request, Action<string, double> progress, CancellationToken cancel,
        Action<List<DiskBenchLine>, string?, bool, double?>? live = null)
    {
        var letter = request.Drive.Trim().TrimEnd(':', '\\').ToUpperInvariant();
        if (letter.Length != 1 || !char.IsAsciiLetter(letter[0])) throw new ArgumentException("Volume inválido.");
        var root = letter + @":\";
        var drive = new DriveInfo(root);
        if (!drive.IsReady) throw new InvalidOperationException($"O volume {letter}: não está pronto.");
        var size = Math.Clamp(request.SizeMb, 64, 32768) * (1L << 20);
        if (drive.AvailableFreeSpace < size + (1L << 30))
            throw new InvalidOperationException($"Espaço insuficiente em {letter}: (precisa de {size >> 20} MB + 1 GB de folga).");
        var seconds = Math.Clamp(request.Seconds, 2, 30);

        var result = new DiskBenchResult
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Time = DateTimeOffset.Now,
            Drive = letter + ":",
            VolumeLabel = string.IsNullOrWhiteSpace(drive.VolumeLabel) ? null : drive.VolumeLabel,
            SizeMb = (int)(size >> 20),
            Seconds = seconds,
        };

        // Nome novo a cada teste e CreateNew: nunca abre (nem trunca, nem apaga) um arquivo que já existia.
        // DeleteOnClose: o Windows apaga o arquivo ao fechar o handle, mesmo se o teste falhar no meio.
        var path = Path.Combine(root, FilePrefix + Guid.NewGuid().ToString("N")[..12] + ".tmp");
        var created = false;
        try
        {
            using var handle = File.OpenHandle(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                FileOptions.Asynchronous | FileOptions.WriteThrough | NoBuffering | DeleteOnClose, preallocationSize: size);
            created = true;

            // 1) Arquivo cheio de dados aleatórios (não medido): lê de verdade do disco, sem "zeros" comprimíveis.
            progress("Preparando o arquivo de teste", 0);
            await FillAsync(handle, size, p => progress("Preparando o arquivo de teste", p * 0.15), cancel);

            // 2) Os quatro testes: leitura e depois escrita, com 1 s de pausa entre medições.
            var steps = Tests.Length * 2;
            var step = 0;
            List<DiskBenchLine> Copy() => result.Lines.Select(l => new DiskBenchLine
            {
                Test = l.Test, ReadMBs = l.ReadMBs, WriteMBs = l.WriteMBs, ReadIops = l.ReadIops, WriteIops = l.WriteIops,
                ReadLatencyUs = l.ReadLatencyUs, WriteLatencyUs = l.WriteLatencyUs,
            }).ToList();
            var lastLive = 0L;
            foreach (var test in Tests)
            {
                var line = new DiskBenchLine { Test = test.Name };
                result.Lines.Add(line);
                foreach (var write in new[] { false, true })
                {
                    var label = $"{test.Name} · {(write ? "escrita" : "leitura")}";
                    var start = 0.15 + 0.85 * step / steps;
                    var m = await MeasureAsync(handle, size, test, write, TimeSpan.FromSeconds(seconds),
                        (p, mbs) =>
                        {
                            // A cada ~250 ms (no 4K Q1 são milhares de operações por segundo).
                            if (Stopwatch.GetElapsedTime(lastLive) < TimeSpan.FromMilliseconds(250)) return;
                            lastLive = Stopwatch.GetTimestamp();
                            progress(label, start + 0.85 / steps * p);
                            live?.Invoke(Copy(), test.Name, write, mbs);
                        }, cancel);
                    if (write) (line.WriteMBs, line.WriteIops, line.WriteLatencyUs) = m;
                    else (line.ReadMBs, line.ReadIops, line.ReadLatencyUs) = m;
                    live?.Invoke(Copy(), null, false, null);
                    step++;
                    await Task.Delay(1000, cancel);
                }
            }
            progress("Concluído", 1);
            return result;
        }
        finally
        {
            // Só o arquivo que este teste criou (o DeleteOnClose já apagou; isto é a garantia).
            if (created)
                try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task FillAsync(SafeFileHandle handle, long size, Action<double> progress, CancellationToken cancel)
    {
        const int block = 1 << 20;
        using var buffer = new AlignedBuffer(block);
        Random.Shared.NextBytes(buffer.Memory.Span);
        long next = 0;
        var tasks = Enumerable.Range(0, 4).Select(async _ =>
        {
            using var own = new AlignedBuffer(block);
            buffer.Memory.Span.CopyTo(own.Memory.Span);
            while (true)
            {
                var offset = Interlocked.Add(ref next, block) - block;
                if (offset >= size) return;
                await RandomAccess.WriteAsync(handle, own.Memory, offset, cancel);
                progress(Math.Min(1, (double)offset / size));
            }
        });
        await Task.WhenAll(tasks);
    }

    /// <summary>MB/s, IOPS e latência média (µs) de uma medição.</summary>
    private static async Task<(double MBs, double Iops, double LatencyUs)> MeasureAsync(SafeFileHandle handle, long size, Test test,
        bool write, TimeSpan duration, Action<double, double> progress, CancellationToken cancel)
    {
        long bytes = 0, ops = 0, latencyTicks = 0, sequential = 0;
        var blocks = size / test.Block;
        var clock = Stopwatch.StartNew();
        var workers = Enumerable.Range(0, test.Queue).Select(async w =>
        {
            using var buffer = new AlignedBuffer(test.Block);
            Random.Shared.NextBytes(buffer.Memory.Span);
            var random = new Random(Environment.TickCount + w * 7919);
            while (clock.Elapsed < duration && !cancel.IsCancellationRequested)
            {
                var offset = test.Random
                    ? random.NextInt64(blocks) * test.Block
                    : (Interlocked.Increment(ref sequential) - 1) % blocks * test.Block;
                var started = Stopwatch.GetTimestamp();
                if (write) await RandomAccess.WriteAsync(handle, buffer.Memory, offset, cancel);
                else await RandomAccess.ReadAsync(handle, buffer.Memory, offset, cancel);
                Interlocked.Add(ref latencyTicks, Stopwatch.GetTimestamp() - started);
                Interlocked.Add(ref bytes, test.Block);
                Interlocked.Increment(ref ops);
                if (w == 0) progress(Math.Min(1, clock.Elapsed / duration), Interlocked.Read(ref bytes) / Math.Max(0.001, clock.Elapsed.TotalSeconds) / 1e6);
            }
        }).ToList();
        await Task.WhenAll(workers);
        cancel.ThrowIfCancellationRequested();
        var s = clock.Elapsed.TotalSeconds;
        var latencyUs = ops == 0 ? 0 : latencyTicks / (double)ops / Stopwatch.Frequency * 1e6;
        return (Math.Round(bytes / s / 1e6, 2), Math.Round(ops / s, 0), Math.Round(latencyUs, 1));
    }

    /// <summary>Memória alinhada a 4 KB (exigência do FILE_FLAG_NO_BUFFERING).</summary>
    private sealed unsafe class AlignedBuffer : MemoryManager<byte>
    {
        private readonly void* _pointer;
        private readonly int _length;

        public AlignedBuffer(int length)
        {
            _length = length;
            _pointer = NativeMemory.AlignedAlloc((nuint)length, 4096);
        }

        public override Span<byte> GetSpan() => new(_pointer, _length);
        public override MemoryHandle Pin(int elementIndex = 0) => new((byte*)_pointer + elementIndex);
        public override void Unpin() { }
        protected override void Dispose(bool disposing) => NativeMemory.AlignedFree(_pointer);
    }
}
