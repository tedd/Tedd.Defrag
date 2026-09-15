using System.Text.RegularExpressions;
using Tedd;

namespace Tedd.Defrag.Core;

public sealed class PathRules
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);
    private readonly CompiledRule[] _selected;
    private readonly CompiledRule[] _excluded;

    public PathRules(IEnumerable<PathRule> selected, IEnumerable<PathRule> excluded)
    {
        _selected = Compile(selected);
        _excluded = Compile(excluded);
    }

    public static string Normalize(string path) => path.Replace('/', '\\').TrimEnd('\\');
    public static void Validate(PathRule? rule)
    {
        ValidateShape(rule);
        try { _ = CompileRule(rule!); }
        catch (ArgumentException exception) { throw new ArgumentException("Invalid file rule.", exception); }
    }

    public bool IsExcluded(string path) => MatchesAny(_excluded, path, timeoutMatches: true);
    public bool IsSelected(string path) => _selected.Length == 0 || MatchesAny(_selected, path, timeoutMatches: false);

    private static CompiledRule[] Compile(IEnumerable<PathRule> rules) => rules.Select(CompileRule).ToArray();

    private static CompiledRule CompileRule(PathRule rule)
    {
        ValidateShape(rule);
        return rule.Kind switch
        {
            PathRuleKind.Path => new(rule, Normalize(rule.Pattern), null, null),
            PathRuleKind.Wildcard => new(rule, null, new WildcardMatch(rule.Pattern.Replace('/', '\\'),
                WildcardOptions.Compiled | WildcardOptions.IgnoreCase | WildcardOptions.CultureInvariant, MatchTimeout), null),
            PathRuleKind.Regex => new(rule, null, null, new Regex(rule.Pattern,
                RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout)),
            _ => throw new ArgumentException("Unknown file-rule kind.")
        };
    }

    private static void ValidateShape(PathRule? rule)
    {
        if (rule is null || string.IsNullOrWhiteSpace(rule.Pattern) || rule.Pattern.Length > 32760 || rule.Pattern.Contains('\0') || !Enum.IsDefined(rule.Kind))
            throw new ArgumentException("Invalid file rule.");
    }

    private static bool MatchesAny(CompiledRule[] rules, string path, bool timeoutMatches)
    {
        string normalized = Normalize(path);
        foreach (var rule in rules)
        {
            try
            {
                bool match = rule.Source.Kind switch
                {
                    PathRuleKind.Path => normalized.Equals(rule.NormalizedPath, StringComparison.OrdinalIgnoreCase) ||
                        (normalized.Length > rule.NormalizedPath!.Length && normalized.StartsWith(rule.NormalizedPath, StringComparison.OrdinalIgnoreCase) && normalized[rule.NormalizedPath.Length] == '\\'),
                    PathRuleKind.Wildcard => rule.Wildcard!.IsMatch(normalized),
                    PathRuleKind.Regex => rule.Regex!.IsMatch(path),
                    _ => false
                };
                if (match) return true;
            }
            // An exclusion must fail closed. A selected-file match fails open so it
            // cannot enlarge user scope after a pathological expression times out.
            catch (RegexMatchTimeoutException) when (timeoutMatches) { return true; }
        }
        return false;
    }

    private sealed record CompiledRule(PathRule Source, string? NormalizedPath, WildcardMatch? Wildcard, Regex? Regex);
}
