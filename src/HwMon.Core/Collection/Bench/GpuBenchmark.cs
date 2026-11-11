using System.Diagnostics;
using HwMon.Core.Telemetry;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace HwMon.Core.Collection.Bench;

/// <summary>
/// Teste da placa de vídeo por DirectX 11 (compute shader), sem janela: funciona no serviço (sessão 0).
/// - TFLOPS FP32: cada thread faz uma cadeia longa de multiplica-e-soma (FMA) em 8 vetores, compilado com
///   IEEE estrito para o compilador não "resumir" as contas.
/// - Banda da VRAM: cópia de um buffer de 512 MB para outro na própria placa (leitura + escrita contadas, como o
///   "copy" do AIDA). Usa a placa com mais memória dedicada (a dedicada, não a integrada).
/// </summary>
public sealed class GpuBenchmark : IDisposable
{
    private const int Iters = 256;
    private const int GroupSize = 256;
    private const int Groups = 8192; // 2 M threads por dispatch
    private const double FlopsPerDispatch = (double)Iters * 8 * 8 * 4 * 2 * GroupSize * Groups;

    private const string Shader = """
        #define ITERS 256
        RWStructuredBuffer<float4> Out : register(u0);
        [numthreads(256, 1, 1)]
        void main(uint3 id : SV_DispatchThreadID)
        {
            float4 a = float4(id.x, id.x + 1, id.x + 2, id.x + 3) * 1e-7f;
            float4 b = a + 0.5f, c = a + 0.25f, d = a + 0.125f, e = a + 0.3f, f = a + 0.7f, g = a + 0.9f, h = a + 0.1f;
            const float4 m = float4(0.9999f, 0.9998f, 0.9997f, 0.9996f);
            const float4 k = float4(0.0001f, 0.0002f, 0.0003f, 0.0004f);
            [loop] for (uint i = 0; i < ITERS; i++)
            {
                [unroll] for (uint j = 0; j < 8; j++)
                {
                    a = mad(a, m, k); b = mad(b, m, k); c = mad(c, m, k); d = mad(d, m, k);
                    e = mad(e, m, k); f = mad(f, m, k); g = mad(g, m, k); h = mad(h, m, k);
                }
            }
            Out[id.x] = a + b + c + d + e + f + g + h;
        }
        """;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11ComputeShader _shader;
    private readonly ID3D11Buffer _output;
    private readonly ID3D11UnorderedAccessView _uav;
    private readonly ID3D11Query _done;

    public string Name { get; }
    public double VramGb { get; }

    public GpuBenchmark()
    {
        using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        // Placas de hardware, a de mais memória dedicada primeiro. A integrada pode informar 0 de memória dedicada e
        // ainda assim entra; se uma placa não aceitar DirectX 11, tenta a próxima.
        var candidates = new List<IDXGIAdapter1>();
        for (uint i = 0; factory.EnumAdapters1(i, out var adapter).Success; i++)
        {
            if ((adapter.Description1.Flags & AdapterFlags.Software) != 0) adapter.Dispose();
            else candidates.Add(adapter);
        }
        try
        {
            foreach (var candidate in candidates.OrderByDescending(a => (ulong)a.Description1.DedicatedVideoMemory))
            {
                if (D3D11.D3D11CreateDevice(candidate, DriverType.Unknown, DeviceCreationFlags.None, [FeatureLevel.Level_11_0], out ID3D11Device device, out ID3D11DeviceContext context).Failure)
                    continue;
                _device = device;
                _context = context;
                Name = candidate.Description1.Description.Trim();
                VramGb = (ulong)candidate.Description1.DedicatedVideoMemory / 1073741824.0;
                break;
            }
        }
        finally
        {
            foreach (var a in candidates) a.Dispose();
        }
        if (_device is null || _context is null || Name is null) throw new InvalidOperationException("Nenhuma placa de vídeo com DirectX 11 encontrada.");

        var bytecode = Compiler.Compile(Shader, "main", "bench.hlsl", "cs_5_0", ShaderFlags.IeeeStrictness | ShaderFlags.OptimizationLevel3);
        _shader = _device.CreateComputeShader(bytecode.Span);
        const int count = GroupSize * Groups;
        _output = _device.CreateBuffer(new BufferDescription((uint)(count * 16), BindFlags.UnorderedAccess, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferStructured, 16));
        _uav = _device.CreateUnorderedAccessView(_output, new UnorderedAccessViewDescription(_output, Format.Unknown, 0, count));
        _done = _device.CreateQuery(new QueryDescription(QueryType.Event));
    }

    /// <summary>Um lote leva ~0,2 s; muito acima disso a placa (ou o driver) parou de responder.</summary>
    private static readonly TimeSpan WaitLimit = TimeSpan.FromSeconds(20);

    private void Wait()
    {
        _context.End(_done);
        _context.Flush();
        var start = Stopwatch.GetTimestamp();
        while (!_context.IsDataAvailable(_done))
        {
            var waited = Stopwatch.GetElapsedTime(start);
            if (waited > WaitLimit) throw new TimeoutException("A placa de vídeo parou de responder durante o teste.");
            // Os primeiros milissegundos com Sleep(0) (precisão da medida); depois cede a CPU de verdade.
            Thread.Sleep(waited < TimeSpan.FromMilliseconds(5) ? 0 : 1);
        }
    }

    /// <summary>Preso enquanto um teste usa a placa: Dispose espera ele terminar antes de liberar os recursos.</summary>
    private readonly object _inUse = new();

    private void Dispatch(int n)
    {
        _context.CSSetShader(_shader);
        _context.CSSetUnorderedAccessView(0, _uav);
        for (var i = 0; i < n; i++) _context.Dispatch(Groups, 1, 1);
        Wait();
    }

    /// <summary>TFLOPS FP32 em ~<paramref name="seconds"/> segundos.</summary>
    public double Flops(double seconds, CancellationToken cancel)
    {
        Dispatch(1); // aquece e compila no driver
        var t = Stopwatch.StartNew();
        Dispatch(1);
        // Lotes de ~0,2 s (cada dispatch fica bem abaixo dos 2 s do TDR do Windows, mesmo em placa fraca).
        var batch = Math.Clamp((int)(0.2 / Math.Max(1e-4, t.Elapsed.TotalSeconds)), 1, 200);
        long dispatches = 0;
        t.Restart();
        while (t.Elapsed.TotalSeconds < seconds && !cancel.IsCancellationRequested)
        {
            Dispatch(batch);
            dispatches += batch;
        }
        cancel.ThrowIfCancellationRequested();
        return dispatches * FlopsPerDispatch / t.Elapsed.TotalSeconds / 1e12;
    }

    /// <summary>GB/s de cópia dentro da VRAM (bytes lidos + escritos).</summary>
    public double Bandwidth(double seconds, CancellationToken cancel)
    {
        // 512 MB em cada um dos dois buffers; em placas com pouca memória dedicada (ou integradas, que informam 0),
        // 256 MB. Acima de ~64 MB o tamanho não muda o resultado.
        var size = VramGb >= 2 ? 512u << 20 : 256u << 20;
        CopySizeMb = (int)(size >> 20);
        var desc = new BufferDescription(size, BindFlags.UnorderedAccess | BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.BufferAllowRawViews, 0);
        using var a = _device.CreateBuffer(desc);
        using var b = _device.CreateBuffer(desc);
        _context.CopyResource(b, a);
        Wait();
        var t = Stopwatch.StartNew();
        _context.CopyResource(b, a);
        Wait();
        var batch = Math.Clamp((int)(0.2 / Math.Max(1e-4, t.Elapsed.TotalSeconds)), 1, 100);
        long copies = 0;
        t.Restart();
        while (t.Elapsed.TotalSeconds < seconds && !cancel.IsCancellationRequested)
        {
            for (var i = 0; i < batch; i++) _context.CopyResource((i & 1) == 0 ? b : a, (i & 1) == 0 ? a : b);
            Wait();
            copies += batch;
        }
        cancel.ThrowIfCancellationRequested();
        return copies * 2.0 * size / t.Elapsed.TotalSeconds / 1e9;
    }

    /// <summary>Carga contínua para o teste de estabilidade (até cancelar).</summary>
    public void Stress(CancellationToken cancel)
    {
        lock (_inUse)
            while (!cancel.IsCancellationRequested) Dispatch(4);
    }

    public int CopySizeMb { get; private set; } = 512;

    public static Task<BenchResult> RunAsync(Action<string, double> progress, Action<BenchScore> partial, CancellationToken cancel)
        => Task.Run(() =>
        {
            var clock = Stopwatch.StartNew();
            progress("Abrindo a placa de vídeo", 0.02);
            using var gpu = new GpuBenchmark();
            var result = new BenchResult { Kind = "gpu", Device = gpu.Name, Detail = $"{gpu.VramGb:0} GB de VRAM · DirectX 11 compute" };

            progress("Cálculo (FP32)", 0.1);
            var tflops = gpu.Flops(8, cancel);
            var s1 = new BenchScore { Key = "tflops", Label = "Cálculo FP32", Value = Math.Round(tflops, 2), Unit = "TFLOPS", Detail = "multiplica-e-soma em massa" };
            result.Scores.Add(s1);
            partial(s1);

            progress("Memória da placa (VRAM)", 0.6);
            var gbs = gpu.Bandwidth(5, cancel);
            var s2 = new BenchScore { Key = "vram", Label = "Banda da VRAM", Value = Math.Round(gbs), Unit = "GB/s", Detail = $"cópia de {gpu.CopySizeMb} MB (leitura + escrita)" };
            result.Scores.Add(s2);
            partial(s2);

            result.Seconds = Math.Round(clock.Elapsed.TotalSeconds, 1);
            progress("Concluído", 1);
            return result;
        }, cancel);

    public void Dispose()
    {
        // A carga do teste de estabilidade roda em outra thread: só libera depois que ela parou. Se o driver travou
        // e ela não para, é melhor deixar os recursos presos do que liberá-los em uso.
        if (!Monitor.TryEnter(_inUse, TimeSpan.FromSeconds(30))) return;
        try
        {
            DisposeResources();
        }
        finally
        {
            Monitor.Exit(_inUse);
        }
    }

    private void DisposeResources()
    {
        _done.Dispose();
        _uav.Dispose();
        _output.Dispose();
        _shader.Dispose();
        _context.Dispose();
        _device.Dispose();
    }
}
