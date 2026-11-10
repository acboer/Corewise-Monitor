using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using HwMon.Core.Model;

namespace HwMon.Core.Reporting;

public static class ReportJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // mantém acentos legíveis
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate,
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Serialize(InventoryReport report) => JsonSerializer.Serialize(report, Options);

    public static InventoryReport Load(string path)
        => JsonSerializer.Deserialize<InventoryReport>(File.ReadAllText(path), Options)
           ?? throw new InvalidDataException($"Arquivo vazio: {path}");
}
