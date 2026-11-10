using System.Globalization;
using System.Text.Json;

namespace HwMon.Core;

/// <summary>
/// Idioma da interface. O texto em português, escrito no próprio código, é a chave; a tradução fica em
/// Idiomas\en.json ("texto em português" → "English text"). Sem tradução, aparece o português.
/// Textos com valores usam {0}, {1}… como em string.Format: <c>L.T("Testar o disco {0}?", drive)</c>.
/// O painel usa o idioma escolhido nas Configurações (padrão: o do Windows); o serviço, o do Windows da máquina
/// dele (ou "language" no config.json). Como o serviço de outra máquina pode falar a outra língua, T() também
/// traduz no sentido contrário (inglês → português) e <see cref="Is"/> compara aceitando as duas.
/// </summary>
public static class L
{
    private static readonly Lazy<Dictionary<string, string>> En = new(() => Load("en") ?? new());
    private static readonly Lazy<Dictionary<string, string>> Pt = new(() =>
    {
        var reverse = new Dictionary<string, string>();
        foreach (var (pt, en) in En.Value) reverse.TryAdd(en, pt);
        return reverse;
    });

    /// <summary>"pt" ou "en".</summary>
    public static string Language { get; private set; } = "pt";

    public static bool English => Language == "en";

    /// <summary>Números e datas no formato do idioma (vírgula decimal em português, ponto em inglês).</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("pt-BR");

    /// <summary>"pt", "en" ou "auto"/vazio (idioma do Windows: português se for português, senão inglês).</summary>
    public static void Use(string? setting)
    {
        Language = setting is "pt" or "en" ? setting : Detect();
        Culture = CultureInfo.GetCultureInfo(English ? "en-US" : "pt-BR");
    }

    /// <summary>Idioma do Windows (do usuário; no serviço, o do sistema).</summary>
    public static string Detect() => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pt" ? "pt" : "en";

    /// <summary>
    /// Traduz para o idioma atual. Aceita texto pronto vindo do serviço (que fala português com os painéis):
    /// além da frase exata, reconhece os modelos com valores ("média de 16 núcleos" ← "média de {0} núcleos").
    /// </summary>
    public static string T(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        if (!English) return Pt.Value.TryGetValue(text, out var pt) ? pt : text;
        if (En.Value.TryGetValue(text, out var en)) return en;
        if (Cache.TryGetValue(text, out var cached)) return cached;
        var result = text;
        if (text.Length < 400 && text.Any(char.IsLetter))
            foreach (var (regex, english) in Templates.Value)
                if (regex.Match(text) is { Success: true } m)
                {
                    result = PlaceholderRegex.Replace(english, p => int.TryParse(p.Groups[1].Value, out var i) && i + 1 < m.Groups.Count ? T(m.Groups[i + 1].Value) : p.Value);
                    break;
                }
        // "Não especificado · USB · 4001 GB": traduz cada parte.
        if (ReferenceEquals(result, text) && text.Contains(" · ", StringComparison.Ordinal))
            result = string.Join(" · ", text.Split(" · ").Select(T));
        if (Cache.Count > 5000) Cache.Clear();
        Cache[text] = result;
        return result;
    }

    private static readonly System.Text.RegularExpressions.Regex PlaceholderRegex = new(@"\{(\d+)(?::[^}]*)?\}");
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Cache = new();

    /// <summary>Chaves com {0}, {1}… viram expressões: o texto fixo tem de bater, cada valor vira um grupo.</summary>
    private static readonly Lazy<List<(System.Text.RegularExpressions.Regex Regex, string English)>> Templates = new(() =>
    {
        var list = new List<(System.Text.RegularExpressions.Regex, string)>();
        foreach (var (pt, en) in En.Value)
        {
            if (!pt.Contains('{')) continue;
            var pattern = new System.Text.StringBuilder("^");
            var last = 0;
            var groups = 0;
            foreach (System.Text.RegularExpressions.Match p in PlaceholderRegex.Matches(pt))
            {
                pattern.Append(System.Text.RegularExpressions.Regex.Escape(pt[last..p.Index]));
                pattern.Append("(.+?)");
                last = p.Index + p.Length;
                groups++;
            }
            if (groups == 0) continue;
            pattern.Append(System.Text.RegularExpressions.Regex.Escape(pt[last..])).Append('$');
            // Só modelos com algum texto fixo de verdade (não "{0} · {1}").
            if (PlaceholderRegex.Replace(pt, "").Count(char.IsLetter) < 3) continue;
            list.Add((new System.Text.RegularExpressions.Regex(pattern.ToString(), System.Text.RegularExpressions.RegexOptions.Singleline), en));
        }
        // Os mais específicos (mais texto fixo) primeiro.
        return list.OrderByDescending(t => t.Item1.ToString().Length).ToList();
    });

    public static string T(string pt, params object?[] args) => string.Format(Culture, T(pt), args);

    /// <summary>Tem tradução para o idioma atual.</summary>
    public static bool Has(string text) => English ? En.Value.ContainsKey(text) : Pt.Value.ContainsKey(text);

    /// <summary>O texto (que pode ter vindo de um serviço em outro idioma) é <paramref name="pt"/> em alguma língua.</summary>
    public static bool Is(string? text, string pt) => text is not null && (text == pt || (En.Value.TryGetValue(pt, out var en) && text == en));

    /// <summary>Começa com <paramref name="pt"/> (ou com a tradução dele).</summary>
    public static bool StartsWith(string? text, string pt)
        => text is not null && (text.StartsWith(pt, StringComparison.Ordinal) || (En.Value.TryGetValue(pt, out var en) && text.StartsWith(en, StringComparison.Ordinal)));

    private static Dictionary<string, string>? Load(string lang)
    {
        try
        {
            using var stream = typeof(L).Assembly.GetManifestResourceStream($"HwMon.Core.Idiomas.{lang}.json");
            if (stream is null) return null;
            return JsonSerializer.Deserialize<Dictionary<string, string>>(stream, new JsonSerializerOptions { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        }
        catch (Exception)
        {
            return null;
        }
    }
}
