using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiDPI.Localization;

/// <summary>One selectable UI language. <see cref="NativeName"/> is shown in the picker ("Türkçe", "English").</summary>
public sealed record LanguageInfo(string Code, string NativeName, bool IsExternal)
{
    public override string ToString() => NativeName;
}

/// <summary>
/// UI strings from flat JSON files (key → text). Built-in languages are embedded resources
/// (<c>Localization/*.json</c>); any <c>*.json</c> in <c>%AppData%\LiDPI\lang\</c> or in a <c>lang</c> folder next to the exe
/// adds a language, or overrides keys of a built-in one with the same code, so translations can be tested without building.
/// Lookup: selected language → English → the key itself. Thread-safe for reads; switch languages on the UI thread.
/// XAML binds through the indexer (<c>{l:T Key}</c>), so a switch updates every bound text without a restart.
/// </summary>
public sealed partial class Loc : INotifyPropertyChanged
{
    public const string FallbackCode = "en";
    /// <summary>Key holding the language's own name ("Türkçe").</summary>
    public const string NameKey = "LanguageName";
    /// <summary>Optional key: culture used for numbers and dates when the file name is not a culture code.</summary>
    public const string CultureKey = "LanguageCulture";

    private const long MaxFileBytes = 1024 * 1024;
    private const string ResourcePrefix = "LiDPI.Localization.";

    /// <summary>Embedded languages: lower-case code → manifest resource name (every Localization/*.json is embedded).</summary>
    private static readonly IReadOnlyDictionary<string, string> Embedded = typeof(Loc).Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        .Select(n => (Code: Normalize(n[ResourcePrefix.Length..^".json".Length]), Name: n))
        .Where(x => x.Code is not null)
        .GroupBy(x => x.Code!)
        .ToDictionary(g => g.Key, g => g.First().Name);

    /// <summary>Turkish and English first, then any other embedded language alphabetically.</summary>
    private static readonly string[] BuiltIn =
        [.. new[] { "tr", FallbackCode }.Where(Embedded.ContainsKey), .. Embedded.Keys.Where(k => k is not ("tr" or FallbackCode)).Order()];

    public static Loc Instance { get; } = new();

    /// <summary>Raised on the UI thread after the language changed (for code that formats text itself).</summary>
    public static event EventHandler? LanguageChanged;

    public event PropertyChangedEventHandler? PropertyChanged;

    private volatile IReadOnlyDictionary<string, string> _current;
    private volatile IReadOnlyDictionary<string, string> _fallback;
    private volatile CultureInfo _culture;
    private string _code;
    private IReadOnlyList<LanguageInfo>? _languages;

    private Loc()
    {
        _fallback = Load(FallbackCode);
        _code = DefaultCode();
        _current = _code == FallbackCode ? _fallback : Load(_code);
        _culture = CultureFor(_code, _current);
    }

    /// <summary>Text for <paramref name="key"/> (the binding target of <c>{l:T Key}</c>).</summary>
    public string this[string key] => Get(key);

    /// <summary>Code of the active language ("tr", "en", or the file name of an external one).</summary>
    public string Code => _code;

    /// <summary>Culture for numbers and dates of the active language.</summary>
    public CultureInfo Culture => _culture;

    /// <summary>Bumped on every switch; multi-bindings include it to re-run their converter.</summary>
    public int Version { get; private set; }

    public static string T(string key) => Instance.Get(key);

    public static string F(string key, params object?[] args) => Instance.Format(key, args);

    public string Get(string key) =>
        _current.TryGetValue(key, out var s) || _fallback.TryGetValue(key, out s) ? s : key;

    /// <summary>Like <see cref="Get"/> but tells a missing key apart (no key echo).</summary>
    public bool TryGet(string key, out string text)
    {
        if (_current.TryGetValue(key, out text!) || _fallback.TryGetValue(key, out text!)) return true;
        text = "";
        return false;
    }

    /// <summary>
    /// string.Format with the active culture. A broken translation (bad placeholder) falls back to English
    /// instead of throwing; translation files come from users.
    /// </summary>
    public string Format(string key, params object?[] args)
    {
        var culture = _culture;
        if (_current.TryGetValue(key, out var fmt) && TryFormat(culture, fmt, args, out var s)) return s;
        if (_fallback.TryGetValue(key, out fmt) && TryFormat(culture, fmt, args, out s)) return s;
        return args.Length == 0 ? key : key + ": " + string.Join(", ", args);
    }

    private static bool TryFormat(CultureInfo c, string fmt, object?[] args, out string result)
    {
        try
        {
            result = string.Format(c, fmt, args);
            return true;
        }
        catch (FormatException)
        {
            result = "";
            return false;
        }
    }

    /// <summary>Windows display language Turkish → "tr", anything else → "en".</summary>
    public static string DefaultCode() =>
        string.Equals(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName, "tr", StringComparison.OrdinalIgnoreCase) ? "tr" : FallbackCode;

    /// <summary>
    /// Switches the language (null/unknown → <see cref="DefaultCode"/>). Files are re-read from disk, so picking a
    /// language again picks up edits to an external file. Call on the UI thread.
    /// </summary>
    public void SetLanguage(string? code)
    {
        code = Normalize(code);
        if (code is null || !IsAvailable(code)) code = DefaultCode();

        var fallback = Load(FallbackCode);
        var current = code == FallbackCode ? fallback : Load(code);
        _fallback = fallback;
        _current = current;
        _culture = CultureFor(code, current);
        _code = code;
        Version++;

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Code)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Culture)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Version)));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Built-in languages plus the external files found (scanned once per process).</summary>
    public IReadOnlyList<LanguageInfo> Languages => _languages ??= Discover();

    private bool IsAvailable(string code) =>
        BuiltIn.Contains(code) || Languages.Any(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<LanguageInfo> Discover()
    {
        var list = new List<LanguageInfo>();
        foreach (var code in BuiltIn)
        {
            var d = Load(code);
            list.Add(new LanguageInfo(code, d.TryGetValue(NameKey, out var n) ? n : code, false));
        }
        foreach (var dir in ExternalDirs())
        {
            string[] files;
            try { files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json") : []; }
            catch { continue; }
            foreach (var f in files.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                var code = Normalize(Path.GetFileNameWithoutExtension(f));
                if (code is null || list.Any(l => string.Equals(l.Code, code, StringComparison.OrdinalIgnoreCase))) continue;
                var d = ReadFile(f);
                if (d.Count == 0) continue;
                list.Add(new LanguageInfo(code, d.TryGetValue(NameKey, out var n) ? n : code, true));
            }
        }
        return list;
    }

    /// <summary>Folders scanned for extra / override language files (later ones win).</summary>
    public static IEnumerable<string> ExternalDirs()
    {
        yield return Path.Combine(AppContext.BaseDirectory, "lang");
        string appData;
        try { appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData); }
        catch { yield break; }
        yield return Path.Combine(appData, "LiDPI", "lang");
    }

    /// <summary>Embedded strings of <paramref name="code"/> with any external file of the same code laid on top.</summary>
    private static Dictionary<string, string> Load(string code)
    {
        var d = ReadEmbedded(code);
        foreach (var dir in ExternalDirs())
        {
            var path = Path.Combine(dir, code + ".json");
            foreach (var kv in ReadFile(path)) d[kv.Key] = kv.Value;
        }
        return d;
    }

    private static Dictionary<string, string> ReadEmbedded(string code)
    {
        try
        {
            if (!Embedded.TryGetValue(code, out var name)) return new(StringComparer.Ordinal);
            using var s = typeof(Loc).Assembly.GetManifestResourceStream(name);
            return s is null ? new(StringComparer.Ordinal) : Parse(s);
        }
        catch
        {
            return new(StringComparer.Ordinal);
        }
    }

    private static Dictionary<string, string> ReadFile(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length > MaxFileBytes) return new(StringComparer.Ordinal);
            using var s = fi.OpenRead();
            return Parse(s);
        }
        catch
        {
            return new(StringComparer.Ordinal);   // unreadable or not JSON: ignore the file
        }
    }

    /// <summary>Flat object of string values; comments and trailing commas allowed, other value types ignored.</summary>
    private static Dictionary<string, string> Parse(Stream s)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(s, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return d;
        foreach (var p in doc.RootElement.EnumerateObject())
            if (p.Value.ValueKind == JsonValueKind.String && p.Name.Length is > 0 and <= 200)
                d[p.Name] = p.Value.GetString() ?? "";
        return d;
    }

    private static CultureInfo CultureFor(string code, IReadOnlyDictionary<string, string> strings)
    {
        foreach (var name in new[] { strings.TryGetValue(CultureKey, out var c) ? c : null, code })
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            try { return CultureInfo.GetCultureInfo(name); }
            catch (CultureNotFoundException) { /* try the next one */ }
        }
        return CultureInfo.InvariantCulture;
    }

    /// <summary>"tr", "pt-BR", "zh-Hant" style codes only (file names come from users).</summary>
    private static string? Normalize(string? code)
    {
        var c = code?.Trim();
        return c is not null && CodeRegex().IsMatch(c) ? c.ToLowerInvariant() : null;
    }

    [GeneratedRegex(@"^[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8}){0,2}$")]
    private static partial Regex CodeRegex();
}
