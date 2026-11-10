using System.IO.Compression;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace HwMon.Core.Collection.Bench;

/// <summary>
/// Cargas fixas de CPU (o mesmo código em todas as máquinas, então os números são comparáveis entre elas). Cada
/// chamada de <see cref="Run"/> faz uma "operação" de tamanho fixo e devolve um número de conferência: com a mesma
/// entrada, o resultado tem de ser sempre igual. No teste de estabilidade, resultado diferente = erro de cálculo
/// (CPU ou memória instável, típico de overclock/undervolt).
/// </summary>
public sealed class CpuWorkloads : IDisposable
{
    public sealed record Workload(string Key, string Label, string What);

    public static readonly Workload[] All =
    [
        new("compress", "Compressão", "Brotli em 256 KB de texto"),
        new("crypto", "Criptografia", "AES-256 e SHA-256 em 1 MB"),
        new("float", "Ponto flutuante", "contas vetoriais (AVX) em 64 mil números"),
        new("sort", "Inteiros", "ordenar 100 mil números"),
    ];

    private static readonly byte[] Text = MakeText(256 * 1024);
    private static readonly byte[] Bytes1M = MakeBytes(1 << 20, 7);
    private static readonly int[] SortSource = MakeInts(100_000, 11);
    private static readonly float[] FloatSource = MakeFloats(65_536, 13);

    private readonly byte[] _compressed = new byte[BrotliEncoder.GetMaxCompressedLength(Text.Length)];
    private readonly byte[] _cipher = new byte[(1 << 20) + 16];
    private readonly int[] _sort = new int[SortSource.Length];
    private readonly float[] _floats = new float[FloatSource.Length];
    private readonly Aes _aes;
    private readonly byte[] _iv = new byte[16];

    public CpuWorkloads()
    {
        _aes = Aes.Create();
        _aes.Key = SHA256.HashData("Hardware Watch"u8);
    }

    public ulong Run(string key) => key switch
    {
        "compress" => Compress(),
        "crypto" => Crypto(),
        "float" => Float(),
        _ => Sort(),
    };

    private ulong Compress()
    {
        BrotliEncoder.TryCompress(Text, _compressed, out var written, 5, 22);
        return (ulong)written ^ _compressed[written / 2];
    }

    private ulong Crypto()
    {
        var n = _aes.EncryptCbc(Bytes1M, _iv, _cipher, PaddingMode.PKCS7);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(_cipher.AsSpan(0, n), hash);
        return BitConverter.ToUInt64(hash);
    }

    private ulong Float()
    {
        FloatSource.CopyTo(_floats, 0);
        var span = _floats.AsSpan();
        var a = new Vector<float>(1.0001f);
        var b = new Vector<float>(0.37f);
        var c = new Vector<float>(0.999f);
        var w = Vector<float>.Count;
        for (var round = 0; round < 48; round++)
            for (var i = 0; i + w <= span.Length; i += w)
            {
                var x = new Vector<float>(span[i..]);
                x = Vector.FusedMultiplyAdd(x, a, b);
                x = Vector.SquareRoot(Vector.Abs(x)) * c + x * new Vector<float>(0.5f);
                x.CopyTo(span[i..]);
            }
        double sum = 0;
        for (var i = 0; i < span.Length; i += 97) sum += span[i];
        return BitConverter.DoubleToUInt64Bits(sum);
    }

    private ulong Sort()
    {
        SortSource.CopyTo(_sort, 0);
        Array.Sort(_sort);
        return (ulong)_sort[_sort.Length / 3] ^ ((ulong)_sort[^1] << 20);
    }

    public void Dispose() => _aes.Dispose();

    // ------------------------------------------------------------------ dados fixos (semente fixa)

    private static byte[] MakeText(int size)
    {
        string[] words = ["placa", "memória", "disco", "render", "frame", "textura", "sombra", "luz", "vidro", "concreto",
            "madeira", "cena", "câmera", "objeto", "material", "the", "and", "with", "render", "pass", "sample", "noise"];
        var random = new Random(3);
        var sb = new StringBuilder(size + 32);
        while (sb.Length < size)
        {
            sb.Append(words[random.Next(words.Length)]);
            sb.Append(random.Next(12) == 0 ? ".\n" : " ");
            if (random.Next(20) == 0) sb.Append(random.Next(100000));
        }
        return Encoding.UTF8.GetBytes(sb.ToString(0, size));
    }

    private static byte[] MakeBytes(int size, int seed)
    {
        var b = new byte[size];
        new Random(seed).NextBytes(b);
        return b;
    }

    private static int[] MakeInts(int n, int seed)
    {
        var r = new Random(seed);
        return Enumerable.Range(0, n).Select(_ => r.Next()).ToArray();
    }

    private static float[] MakeFloats(int n, int seed)
    {
        var r = new Random(seed);
        return Enumerable.Range(0, n).Select(_ => (float)(r.NextDouble() * 100)).ToArray();
    }
}
