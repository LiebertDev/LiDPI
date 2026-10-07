namespace LiDPI.Localization;

/// <summary>
/// A text that is resolved when shown: a key (+ format arguments) or a raw string. View-model state keeps these
/// instead of finished strings, so a language switch re-renders status lines in the new language.
/// Arguments may be <see cref="LocText"/> themselves (they are resolved through ToString()).
/// </summary>
public readonly struct LocText
{
    private readonly string? _key;
    private readonly object?[]? _args;
    private readonly string? _raw;

    private LocText(string? key, object?[]? args, string? raw)
    {
        _key = key;
        _args = args;
        _raw = raw;
    }

    public static LocText Empty => default;

    public static LocText Of(string key, params object?[] args) => new(key, args, null);

    public static LocText Raw(string? text) => new(null, null, text);

    public bool IsEmpty => _key is null && string.IsNullOrEmpty(_raw);

    public override string ToString() =>
        _key is null ? _raw ?? ""
        : _args is { Length: > 0 } a ? Loc.Instance.Format(_key, a)
        : Loc.Instance.Get(_key);
}
