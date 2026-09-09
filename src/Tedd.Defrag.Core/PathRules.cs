using System.IO.Enumeration;

namespace Tedd.Defrag.Core;

public sealed class PathRules
{
    private readonly string[] _selected;
    private readonly string[] _excluded;
    public PathRules(string[] selected, string[] excluded)
    {
        _selected = selected.Select(Normalize).ToArray();
        _excluded = excluded.Select(Normalize).ToArray();
    }
    public static string Normalize(string path) => path.Replace('/', '\\').TrimEnd('\\');
    public bool IsExcluded(string path) => MatchesAny(_excluded, Normalize(path));
    public bool IsSelected(string path) => _selected.Length == 0 || MatchesAny(_selected, Normalize(path));
    private static bool MatchesAny(string[] rules, string path)
    {
        foreach (var rule in rules)
        {
            if (rule.IndexOfAny(['*', '?']) >= 0)
            {
                if (Glob(rule, path)) return true;
            }
            else if (path.Equals(rule, StringComparison.OrdinalIgnoreCase) ||
                     (path.Length > rule.Length && path.StartsWith(rule, StringComparison.OrdinalIgnoreCase) && path[rule.Length] == '\\')) return true;
        }
        return false;
    }
    private static bool Glob(ReadOnlySpan<char> pattern, ReadOnlySpan<char> path)
    {
        int p = 0, s = 0, star = -1, retry = 0;
        while (s < path.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(path[s]))) { p++; s++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; retry = s; }
            else if (star >= 0) { p = star + 1; s = ++retry; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }
}
