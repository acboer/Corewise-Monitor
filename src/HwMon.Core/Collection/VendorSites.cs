using System.Text.RegularExpressions;

namespace HwMon.Core.Collection;

/// <summary>
/// Página de suporte (ou busca pelo modelo) dos fabricantes conhecidos. O hardware não informa endereço nenhum;
/// esta tabela cobre as marcas mais comuns. Fabricante fora dela: sem link.
/// </summary>
public static class VendorSites
{
    private static string E(string s) => Uri.EscapeDataString(s.Trim());

    /// <summary>Endereço para o fabricante (texto livre do SMBIOS/WMI) e, quando dá, já buscando o modelo.</summary>
    public static string? For(string? vendor, string? model = null)
    {
        var v = (vendor ?? "") + " " + (model ?? "");
        var m = model ?? "";
        return v switch
        {
            _ when Has(v, "Micro-Star|\\bMSI\\b") => m.Length > 0 ? $"https://www.msi.com/search/{E(Regex.Replace(m, @"\s*\(MS-\w+\)", ""))}" : "https://www.msi.com/support",
            _ when Has(v, "ASUSTeK|\\bASUS\\b") => m.Length > 0 ? $"https://www.asus.com/searchresult?searchType=support&searchKey={E(m)}" : "https://www.asus.com/support/",
            _ when Has(v, "Gigabyte|\\bGIGA") => m.Length > 0 ? $"https://www.gigabyte.com/Search?kw={E(m)}" : "https://www.gigabyte.com/Support",
            _ when Has(v, "ASRock") => "https://www.asrock.com/support/index.asp",
            _ when Has(v, "\\bAMD\\b|Advanced Micro|Radeon|Ryzen") => "https://www.amd.com/pt/support/download/drivers.html",
            _ when Has(v, "\\bIntel\\b|GenuineIntel") => m.Length > 0 ? $"https://www.intel.com/content/www/us/en/search.html#q={E(m)}" : "https://www.intel.com/content/www/us/en/support.html",
            _ when Has(v, "NVIDIA|GeForce") => "https://www.nvidia.com/pt-br/drivers/",
            _ when Has(v, "Corsair|^\\s*CM[A-Z]") => m.Length > 0 ? $"https://www.corsair.com/br/pt/search?query={E(m)}" : "https://www.corsair.com/br/pt/s/",
            _ when Has(v, "G\\.Skill|^\\s*F[3-5]-") => "https://www.gskill.com/support",
            _ when Has(v, "Kingston|\\bKF\\d|\\bKVR") => "https://www.kingston.com/br/support",
            _ when Has(v, "Crucial|Micron|^\\s*CT\\d") => "https://www.crucial.com/support",
            _ when Has(v, "Seagate|^\\s*ST\\d") => "https://www.seagate.com/br/pt/support/",
            _ when Has(v, "Western Digital|^\\s*WDC|^\\s*WD\\b") => "https://support-en.wd.com/",
            _ when Has(v, "Samsung") => "https://semiconductor.samsung.com/consumer-storage/support/tools/",
            _ when Has(v, "XPG|ADATA") => "https://www.xpg.com/pt-br/support",
            _ when Has(v, "Netac") => "https://www.netac.com/",
            _ when Has(v, "Toshiba|KIOXIA") => "https://www.kioxia.com/",
            _ when Has(v, "\\bDell\\b") => m.Length > 0 ? $"https://www.dell.com/support/search/pt-br#q={E(m)}" : "https://www.dell.com/support",
            _ when Has(v, "\\bLG\\b") => "https://www.lg.com/br/suporte",
            _ when Has(v, "BenQ") => "https://www.benq.com/pt-br/support.html",
            _ when Has(v, "\\bAcer\\b") => "https://www.acer.com/br-pt/support",
            _ when Has(v, "\\bAOC\\b") => "https://aoc.com/br/support",
            _ when Has(v, "Realtek") => "https://www.realtek.com/Download/Index",
            _ when Has(v, "Qualcomm") => "https://www.qualcomm.com/support",
            _ when Has(v, "Logitech") => "https://support.logi.com/",
            _ when Has(v, "\\bHP\\b|Hewlett") => "https://support.hp.com/br-pt",
            _ when Has(v, "Lenovo") => "https://pcsupport.lenovo.com/br/pt",
            _ => null,
        };
    }

    private static bool Has(string text, string pattern) => Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase);
}
