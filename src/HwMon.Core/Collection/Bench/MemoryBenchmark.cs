using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HwMon.Core.Telemetry;

namespace HwMon.Core.Collection.Bench;

/// <summary>
/// Teste de memória no estilo do AIDA64: leitura, escrita e cópia em GB/s num bloco bem maior que o cache L3
/// (1 GB, ou 256 MB com pouca RAM livre), em várias threads; latência por "pointer chasing" (cada leitura depende
/// da anterior, em ordem aleatória, então o processador não consegue adiantar). GB = 10⁹ bytes, como no AIDA.
/// A latência sai um pouco acima da do AIDA, que usa páginas grandes (menos falhas de TLB).
/// </summary>
public static unsafe class MemoryBenchmark
{
    private const double Seconds = 2.5;

    public static Task<BenchResult> RunAsync(Action<string, double> progress, Action<BenchScore> partial, CancellationToken cancel)
        => Task.Run(() => Run(progress, partial, cancel), cancel);

    private static BenchResult Run(Action<string, double> progress, Action<BenchScore> partial, CancellationToken cancel)
    {
        var clock = Stopwatch.StartNew();
        var info = GC.GetGCMemoryInfo();
        var freeMb = (info.TotalAvailableMemoryBytes - info.MemoryLoadBytes) / (1 << 20);
        long size = freeMb > 4096 ? 1L << 30 : 256L << 20;
        // Uma thread por núcleo físico (aprox.): mais que isso não aumenta a banda e só esquenta.
        var threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 16);
        var result = new BenchResult { Kind = "memoria", Detail = $"bloco de {size >> 20} MB · {threads} threads" };

        var block = (byte*)NativeMemory.AlignedAlloc((nuint)size, 4096);
        try
        {
            progress("Preparando a memória", 0.02);
            new Span<byte>(block, (int)Math.Min(size, int.MaxValue)).Fill(1);
            if (size > int.MaxValue) new Span<byte>(block + int.MaxValue, (int)(size - int.MaxValue)).Fill(1);

            progress("Leitura", 0.05);
            var read = Bandwidth(block, size, threads, Read, cancel);
            Add(result, partial, "read", "Leitura", read, "GB/s");

            progress("Escrita", 0.25);
            var write = Bandwidth(block, size, threads, Write, cancel);
            Add(result, partial, "write", "Escrita", write, "GB/s");

            progress("Cópia", 0.45);
            var half = size / 2;
            var copy = Bandwidth(block, half, threads, (p, n) => Copy(p, p + half, n), cancel);
            Add(result, partial, "copy", "Cópia", copy, "GB/s");
        }
        finally
        {
            NativeMemory.AlignedFree(block);
        }

        progress("Latência", 0.65);
        var latency = Latency(256L << 20, cancel);
        Add(result, partial, "latency", "Latência", latency, "ns");
        result.Seconds = Math.Round(clock.Elapsed.TotalSeconds, 1);
        progress("Concluído", 1);
        return result;
    }

    private static void Add(BenchResult r, Action<BenchScore> partial, string key, string label, double value, string unit)
    {
        var s = new BenchScore { Key = key, Label = label, Value = Math.Round(value, key == "latency" ? 1 : 2), Unit = unit };
        r.Scores.Add(s);
        partial(s);
    }

    private delegate void Pass(byte* p, long n);

    /// <summary>GB/s: cada thread repete a operação na sua fatia do bloco até dar o tempo.</summary>
    private static double Bandwidth(byte* block, long size, int threads, Pass pass, CancellationToken cancel)
    {
        var slice = size / threads / 4096 * 4096;
        long bytes = 0;
        var stop = 0;
        var go = new ManualResetEventSlim(false);
        var workers = Enumerable.Range(0, threads).Select(i => new Thread(() =>
        {
            var p = block + i * slice;
            go.Wait();
            while (Volatile.Read(ref stop) == 0 && !cancel.IsCancellationRequested)
            {
                pass(p, slice);
                Interlocked.Add(ref bytes, slice);
            }
        }) { IsBackground = true }).ToList();
        foreach (var t in workers) t.Start();
        // Primeira passada de aquecimento, depois mede.
        go.Set();
        Thread.Sleep(300);
        var before = Interlocked.Read(ref bytes);
        var t0 = Stopwatch.GetTimestamp();
        Thread.Sleep(TimeSpan.FromSeconds(Seconds));
        var after = Interlocked.Read(ref bytes);
        var elapsed = Stopwatch.GetElapsedTime(t0).TotalSeconds;
        Volatile.Write(ref stop, 1);
        foreach (var t in workers) t.Join();
        cancel.ThrowIfCancellationRequested();
        return (after - before) / elapsed / 1e9;
    }

    private static ulong _sink;

    private static void Read(byte* p, long n)
    {
        var w = Vector<ulong>.Count * 8;
        Vector<ulong> a = default, b = default, c = default, d = default;
        var end = p + n - 4 * w;
        for (var q = p; q <= end; q += 4 * w)
        {
            a ^= Unsafe.ReadUnaligned<Vector<ulong>>(q);
            b ^= Unsafe.ReadUnaligned<Vector<ulong>>(q + w);
            c ^= Unsafe.ReadUnaligned<Vector<ulong>>(q + 2 * w);
            d ^= Unsafe.ReadUnaligned<Vector<ulong>>(q + 3 * w);
        }
        _sink ^= (a ^ b ^ c ^ d)[0]; // impede o compilador de descartar a leitura
    }

    /// <summary>
    /// Escrita "non-temporal" (direto para a RAM, sem passar pelo cache), como o AIDA: sem isso o processador
    /// primeiro lê cada linha antes de escrever e a banda medida cai pela metade.
    /// </summary>
    private static void Write(byte* p, long n)
    {
        if (System.Runtime.Intrinsics.X86.Avx.IsSupported && ((nint)p & 31) == 0)
        {
            var v = System.Runtime.Intrinsics.Vector256.Create((byte)0x5A);
            var end = p + n - 128;
            for (var q = p; q <= end; q += 128)
            {
                System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal(q, v);
                System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal(q + 32, v);
                System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal(q + 64, v);
                System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal(q + 96, v);
            }
            System.Runtime.Intrinsics.X86.Sse.StoreFence();
            return;
        }
        while (n > 0)
        {
            var chunk = (int)Math.Min(n, 1 << 30);
            new Span<byte>(p, chunk).Fill(0x5A);
            p += chunk;
            n -= chunk;
        }
    }

    /// <summary>Cópia com leitura normal e escrita non-temporal (bytes copiados por segundo, como o AIDA).</summary>
    private static void Copy(byte* src, byte* dst, long n)
    {
        if (!System.Runtime.Intrinsics.X86.Avx.IsSupported || ((nint)src & 31) != 0 || ((nint)dst & 31) != 0)
        {
            Buffer.MemoryCopy(src, dst, n, n);
            return;
        }
        var end = n - 128;
        for (long i = 0; i <= end; i += 128)
        {
            var a = System.Runtime.Intrinsics.X86.Avx.LoadAlignedVector256(src + i);
            var b = System.Runtime.Intrinsics.X86.Avx.LoadAlignedVector256(src + i + 32);
            var c = System.Runtime.Intrinsics.X86.Avx.LoadAlignedVector256(src + i + 64);
            var d = System.Runtime.Intrinsics.X86.Avx.LoadAlignedVector256(src + i + 96);
            System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal(dst + i, a);
            System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal(dst + i + 32, b);
            System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal(dst + i + 64, c);
            System.Runtime.Intrinsics.X86.Avx.StoreAlignedNonTemporal(dst + i + 96, d);
        }
        System.Runtime.Intrinsics.X86.Sse.StoreFence();
    }

    /// <summary>ns por acesso: lista ligada embaralhada (uma linha de cache por nó), percorrida em 1 thread.</summary>
    private static double Latency(long size, CancellationToken cancel)
    {
        const int line = 64;
        var nodes = (int)(size / line);
        var block = (byte*)NativeMemory.AlignedAlloc((nuint)size, 4096);
        try
        {
            // Ciclo aleatório único (algoritmo de Sattolo): visita todos os nós antes de repetir.
            var order = new int[nodes];
            for (var i = 0; i < nodes; i++) order[i] = i;
            var random = new Random(5);
            for (var i = nodes - 1; i > 0; i--)
            {
                var j = random.Next(i);
                (order[i], order[j]) = (order[j], order[i]);
            }
            for (var i = 0; i < nodes; i++)
                *(byte**)(block + (long)order[i] * line) = block + (long)order[(i + 1) % nodes] * line;
            cancel.ThrowIfCancellationRequested();

            var p = block;
            for (var i = 0; i < 1_000_000; i++) p = *(byte**)p; // aquecimento
            const int steps = 20_000_000;
            var t0 = Stopwatch.GetTimestamp();
            for (var i = 0; i < steps; i++) p = *(byte**)p;
            var ns = Stopwatch.GetElapsedTime(t0).TotalMilliseconds * 1e6 / steps;
            _sink ^= (ulong)p;
            return ns;
        }
        finally
        {
            NativeMemory.AlignedFree(block);
        }
    }
}
