using System.Text.Json;

namespace HwMon.Core;

/// <summary>
/// Gravação de arquivos de estado (clientes pareados, máquinas, resultados, configuração) sem risco de deixar JSON
/// pela metade: grava num temporário ao lado e troca de uma vez (File.Replace), guardando a versão anterior em .bak.
/// A leitura volta para o .bak se o principal estiver estragado, e guarda o estragado (.corrompido) em vez de
/// deixar a próxima gravação apagá-lo.
/// </summary>
public static class SafeFile
{
    public static void WriteAllText(string path, string text)
    {
        var tmp = $"{path}.{Guid.NewGuid().ToString("N")[..8]}.tmp";
        try
        {
            using (var stream = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(text);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(path)) File.Replace(tmp, path, path + ".bak", ignoreMetadataErrors: true);
            else File.Move(tmp, path);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch (Exception) { /* sobra um .tmp; não atrapalha */ }
        }
    }

    /// <summary>
    /// Lê o JSON (ou o .bak, se o principal estiver estragado). Nulo quando nenhum dos dois existe ou dá para ler.
    /// </summary>
    public static T? ReadJson<T>(string path, JsonSerializerOptions? options = null) where T : class
    {
        if (File.Exists(path))
        {
            try
            {
                return JsonSerializer.Deserialize<T>(File.ReadAllText(path), options);
            }
            catch (JsonException)
            {
                try { File.Copy(path, path + ".corrompido", overwrite: true); }
                catch (Exception) { /* sem espaço/acesso: segue para o backup */ }
            }
        }
        var backup = path + ".bak";
        if (!File.Exists(backup)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(backup), options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
