using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HwMon.Collector;

/// <summary>
/// Código de pareamento de 6 dígitos, criado pelo comando "parear" (exige administrador no PC do coletor).
/// Vale 10 minutos, serve uma única vez e é descartado após 5 tentativas erradas. O arquivo guarda só o hash.
/// </summary>
public sealed class PairingCodes
{
    public static readonly TimeSpan Validity = TimeSpan.FromMinutes(10);
    private const int MaxAttempts = 5;
    private readonly string _path;
    private readonly object _lock = new();

    public PairingCodes(string path) => _path = path;

    public string Create()
    {
        var code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        var salt = RandomNumberGenerator.GetBytes(16);
        var state = new State
        {
            Salt = Convert.ToBase64String(salt),
            Hash = Hash(code, salt),
            ExpiresUtc = DateTime.UtcNow + Validity,
            AttemptsLeft = MaxAttempts,
        };
        lock (_lock) File.WriteAllText(_path, JsonSerializer.Serialize(state));
        return code;
    }

    public enum Result { Ok, NoCode, Expired, Wrong, Locked }

    public Result Verify(string code)
    {
        lock (_lock)
        {
            if (!File.Exists(_path)) return Result.NoCode;
            var state = JsonSerializer.Deserialize<State>(File.ReadAllText(_path));
            if (state is null) return Result.NoCode;
            if (DateTime.UtcNow > state.ExpiresUtc)
            {
                File.Delete(_path);
                return Result.Expired;
            }

            var ok = CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(Hash(code.Trim(), Convert.FromBase64String(state.Salt))),
                Encoding.ASCII.GetBytes(state.Hash));
            if (ok)
            {
                File.Delete(_path); // uso único
                return Result.Ok;
            }

            state.AttemptsLeft--;
            if (state.AttemptsLeft <= 0)
            {
                File.Delete(_path);
                return Result.Locked;
            }
            File.WriteAllText(_path, JsonSerializer.Serialize(state));
            return Result.Wrong;
        }
    }

    private static string Hash(string code, byte[] salt)
        => Convert.ToHexString(SHA256.HashData([.. salt, .. Encoding.UTF8.GetBytes(code)]));

    private sealed class State
    {
        public string Salt { get; set; } = "";
        public string Hash { get; set; } = "";
        public DateTime ExpiresUtc { get; set; }
        public int AttemptsLeft { get; set; }
    }
}

public sealed class PairedClient
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string TokenHash { get; set; } = "";
    /// <summary>
    /// Token anterior deste mesmo painel, ainda aceito até o painel usar o novo. Um pareamento que o painel não
    /// chegou a guardar (pedido aprovado depois de a janela fechar, resposta perdida) não derruba o que funciona.
    /// </summary>
    public string? PreviousTokenHash { get; set; }
    public DateTimeOffset PairedAt { get; set; }
    public DateTimeOffset? LastSeen { get; set; }
}

/// <summary>Clientes pareados. O token só existe no cliente; aqui fica o hash SHA-256.</summary>
public sealed class ClientRegistry
{
    private readonly string _path;
    private readonly object _lock = new();
    private List<PairedClient> _clients;
    private DateTimeOffset _lastSave = DateTimeOffset.MinValue;

    public ClientRegistry(string path)
    {
        _path = path;
        _clients = Load();
    }

    private List<PairedClient> Load()
        => SafeFile.ReadJson<List<PairedClient>>(_path) ?? new();

    /// <summary>Recarrega do disco: o comando "revogar" altera o arquivo com o serviço rodando.</summary>
    public void Reload()
    {
        lock (_lock) _clients = Load();
    }

    public IReadOnlyList<PairedClient> List()
    {
        lock (_lock) return _clients.ToList();
    }

    public string Add(string clientId, string name)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        lock (_lock)
        {
            // Pareou de novo: o token novo vale já, e o anterior continua valendo até o painel aparecer com o novo.
            var previous = _clients.FirstOrDefault(c => c.Id == clientId)?.TokenHash;
            _clients.RemoveAll(c => c.Id == clientId);
            _clients.Add(new PairedClient { Id = clientId, Name = name, TokenHash = HashToken(token), PreviousTokenHash = previous, PairedAt = DateTimeOffset.Now });
            Save();
        }
        return token;
    }

    private DateTime _loadedWrite;

    public PairedClient? Validate(string token)
    {
        var hash = HashToken(token);
        lock (_lock)
        {
            // O comando "revogar" edita o arquivo com o serviço rodando: recarrega se mudou por fora.
            // Arquivo sumido ou ilegível neste instante (outro processo gravando): segue com a lista em memória, em vez
            // de recusar todo mundo com 401 (o painel antes desistia da máquina até ser reaberto).
            var write = File.Exists(_path) ? File.GetLastWriteTimeUtc(_path) : _loadedWrite;
            if (write != _loadedWrite)
            {
                try
                {
                    if (SafeFile.ReadJson<List<PairedClient>>(_path) is { } fresh)
                    {
                        _clients = fresh;
                        _loadedWrite = write;
                    }
                }
                catch (IOException)
                {
                    // tenta na próxima requisição
                }
            }
            var client = _clients.FirstOrDefault(c => c.TokenHash == hash);
            if (client is not null && client.PreviousTokenHash is not null)
            {
                client.PreviousTokenHash = null; // o painel já usa o token novo: o antigo deixa de valer
                Save();
            }
            client ??= _clients.FirstOrDefault(c => c.PreviousTokenHash is not null && c.PreviousTokenHash == hash);
            if (client is null) return null;
            client.LastSeen = DateTimeOffset.Now;
            if (DateTimeOffset.Now - _lastSave > TimeSpan.FromMinutes(5)) Save();
            return client;
        }
    }

    public int Revoke(string idOrName)
    {
        lock (_lock)
        {
            var removed = _clients.RemoveAll(c => c.Id.Equals(idOrName, StringComparison.OrdinalIgnoreCase)
                                                  || c.Name.Equals(idOrName, StringComparison.OrdinalIgnoreCase));
            if (removed > 0) Save();
            return removed;
        }
    }

    private void Save()
    {
        SafeFile.WriteAllText(_path, JsonSerializer.Serialize(_clients, DataStore.Json));
        _lastSave = DateTimeOffset.Now;
        _loadedWrite = File.GetLastWriteTimeUtc(_path);
    }

    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
