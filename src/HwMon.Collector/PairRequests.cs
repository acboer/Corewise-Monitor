using System.Collections.Concurrent;
using System.Security.Cryptography;
using HwMon.Core.Telemetry;

namespace HwMon.Collector;

/// <summary>
/// Pareamento por aceite: o painel de outro PC pede acesso (POST /v1/pair/ask) e fica perguntando o resultado; o
/// painel aberto NESTE PC (só ele, pela conexão local) mostra "Permitir?" e responde. Sem código para digitar.
/// Pedidos valem 2 minutos; no máximo 5 abertos ao mesmo tempo, e um por cliente.
/// </summary>
public sealed class PairRequests(ClientRegistry clients)
{
    private static readonly TimeSpan Validity = TimeSpan.FromMinutes(2);

    private sealed class Entry
    {
        public required string Id { get; init; }
        public required string ClientId { get; init; }
        public required string ClientName { get; init; }
        public string? Address { get; init; }
        public DateTimeOffset Created { get; } = DateTimeOffset.UtcNow;
        public string State = "pending"; // pending, approved, denied
        public string? Token;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new();

    private void Prune()
    {
        foreach (var e in _entries.Values.Where(e => DateTimeOffset.UtcNow - e.Created > Validity + TimeSpan.FromMinutes(1)))
            _entries.TryRemove(e.Id, out _);
    }

    /// <summary>Novo pedido; nulo se já houver pedidos demais.</summary>
    public string? Ask(string clientId, string clientName, string? address)
    {
        Prune();
        foreach (var old in _entries.Values.Where(e => e.ClientId == clientId)) _entries.TryRemove(old.Id, out _);
        if (_entries.Values.Count(e => e.State == "pending") >= 5) return null;
        var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _entries[id] = new Entry { Id = id, ClientId = clientId, ClientName = clientName, Address = address };
        return id;
    }

    /// <summary>Estado para quem pediu: pending, approved (com o token, entregue uma vez só), denied ou expired.</summary>
    public PairAskStatus Status(string id)
    {
        if (!_entries.TryGetValue(id, out var e)) return new PairAskStatus { State = "expired" };
        if (e.State == "pending" && DateTimeOffset.UtcNow - e.Created > Validity)
        {
            _entries.TryRemove(id, out _);
            return new PairAskStatus { State = "expired" };
        }
        if (e.State != "pending") _entries.TryRemove(id, out _);
        return new PairAskStatus { State = e.State, Token = e.Token };
    }

    /// <summary>Pedidos esperando resposta (para o painel deste PC).</summary>
    public List<PairPending> Pending()
    {
        Prune();
        return _entries.Values.Where(e => e.State == "pending" && DateTimeOffset.UtcNow - e.Created <= Validity)
            .OrderBy(e => e.Created)
            .Select(e => new PairPending { Id = e.Id, ClientName = e.ClientName, Address = e.Address, Time = e.Created })
            .ToList();
    }

    public bool Answer(string id, bool approve)
    {
        if (!_entries.TryGetValue(id, out var e) || e.State != "pending") return false;
        if (approve) e.Token = clients.Add(e.ClientId, e.ClientName);
        e.State = approve ? "approved" : "denied";
        return true;
    }
}
