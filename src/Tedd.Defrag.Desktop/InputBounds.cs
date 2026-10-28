using System.Globalization;

namespace Tedd.Defrag.Desktop;

internal sealed record InputBounds(ulong Minimum, ulong Maximum, ulong DefaultValue = 0,
    bool AllowZero = false, bool Hexadecimal = false, bool Required = false, string? ZeroMeaning = null, ulong? AllowedBits = null)
{
    public const ulong MaximumMiB = long.MaxValue / (1024UL * 1024);
    public static readonly InputBounds Workers = new(0, 32, ZeroMeaning: "automatic");
    public static readonly InputBounds MoveQueue = new(1, 16, 16);
    public static readonly InputBounds Memory = new(256, int.MaxValue, AllowZero: true, ZeroMeaning: "unlimited");
    public static readonly InputBounds NonnegativeInt = new(0, int.MaxValue, ZeroMeaning: "unlimited");
    public static readonly InputBounds MiB = new(0, MaximumMiB, ZeroMeaning: "unlimited");
    public static readonly InputBounds Fragments = new(2, int.MaxValue, 20);
    public static readonly InputBounds ConcurrentVolumes = new(1, 32, 2);
    public static readonly InputBounds Affinity = new(0, ulong.MaxValue, Hexadecimal: true, ZeroMeaning: "automatic");

    public string RangeDescription => Hexadecimal ? AllowedBits is { } mask
        ? $"Use hexadecimal mask bits from 0x{mask:X}; 0 means automatic."
        : "Use up to 16 hexadecimal digits; 0 means automatic." :
        $"Use {(AllowZero && Minimum > 0 ? "0 or " : "")}{Minimum:N0}–{Maximum:N0}{(ZeroMeaning == null ? "." : $"; 0 means {ZeroMeaning}.")}";

    public bool TryRead(string? text, out ulong value)
    {
        if (string.IsNullOrWhiteSpace(text)) { value = DefaultValue; return !Required; }
        string normalized = text.Trim().Replace(",", "", StringComparison.Ordinal)
            .Replace(" ", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
        if (Hexadecimal && normalized.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) normalized = normalized[2..];
        return ulong.TryParse(normalized, Hexadecimal ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
            CultureInfo.InvariantCulture, out value);
    }

    public bool CanEdit(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || Hexadecimal && text.Trim().Equals("0x", StringComparison.OrdinalIgnoreCase)) return true;
        return TryRead(text, out ulong value) && value <= Maximum;
    }

    public bool IsValid(string? text) => TryRead(text, out ulong value) && IsInRange(value);
    private bool IsInRange(ulong value) => value <= Maximum && (value >= Minimum || AllowZero && value == 0) &&
        (AllowedBits == null || (value & ~AllowedBits.Value) == 0);

    public string Normalize(string? text)
    {
        if (!TryRead(text, out ulong value)) return Required ? "" : Format(DefaultValue);
        if (Required && value == 0) return "";
        if (AllowedBits is { } mask) value &= mask;
        if (!IsInRange(value)) value = AllowZero && value == 0 ? 0 : Math.Clamp(value, Minimum, Maximum);
        return Format(value);
    }
    private string Format(ulong value) => value.ToString(Hexadecimal ? "X" : "D", CultureInfo.InvariantCulture);

    public static bool SizeRangeIsValid(ulong minimum, ulong maximum) => maximum == 0 || maximum >= minimum;
}
