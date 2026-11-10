namespace HwMon.Core;

/// <summary>
/// Pastas de dados do Corewise. Até a 0.11 elas se chamavam "HwMon"; na primeira execução da 0.12 a pasta antiga é
/// movida (ou copiada, se algo estiver aberto nela) para a nova, levando máquinas pareadas, histórico e configurações.
/// </summary>
public static class AppPaths
{
    public const string Product = "Corewise";
    private const string Legacy = "HwMon";

    /// <summary>%AppData%\Corewise: máquinas pareadas, configurações do painel, histórico de rede, erro.log.</summary>
    public static string UserData => _user ??= Prepare(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));

    /// <summary>%ProgramData%\Corewise: dados do serviço (certificado, clientes, histórico, jogos, benchmarks).</summary>
    public static string MachineData => _machine ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Product);

    private static string? _user, _machine;

    /// <summary>Migra %ProgramData%\HwMon → %ProgramData%\Corewise (precisa de administrador; o serviço antigo parado).</summary>
    public static void MigrateMachineData() => Migrate(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

    private static string Prepare(string root)
    {
        Migrate(root);
        var dir = Path.Combine(root, Product);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Migrate(string root)
    {
        var dir = Path.Combine(root, Product);
        var old = Path.Combine(root, Legacy);
        if (!Directory.Exists(old)) return;
        if (Directory.Exists(dir))
        {
            // As duas existem (migração interrompida, ou algo criou a nova antes): completa com o que faltar,
            // sem sobrescrever nada da nova.
            try { Copy(old, dir); }
            catch (Exception) { /* sem acesso: segue com o que tem */ }
            return;
        }
        try
        {
            Directory.Move(old, dir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Algum arquivo aberto (ex.: painel antigo ainda rodando): copia e deixa a antiga como estava.
            try { Copy(old, dir); }
            catch (Exception) { /* sem acesso: começa vazio */ }
        }
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            var target = Path.Combine(to, Path.GetFileName(file));
            if (File.Exists(target)) continue;
            try { File.Copy(file, target); }
            catch (IOException) { /* arquivo preso: os outros continuam */ }
        }
        foreach (var sub in Directory.GetDirectories(from))
            Copy(sub, Path.Combine(to, Path.GetFileName(sub)));
    }
}
