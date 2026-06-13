namespace Dispatch.Core.Routing;

/// <summary>
/// Domain-based routing pattern matching (spec §10.4): exact (<c>acme.com</c>), single-level wildcard
/// (<c>*.acme.com</c>), comma-separated list, and catch-all (<c>*</c>). Patterns match the domain part only.
/// </summary>
public static class DomainMatcher
{
    public static bool Matches(string domain, string pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        if (pattern == "*") return true;

        foreach (var p in pattern.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (MatchSingle(domain, p))
                return true;
        return false;
    }

    private static bool MatchSingle(string domain, string pattern)
    {
        if (pattern == "*") return true;
        if (pattern.StartsWith("*.", StringComparison.Ordinal))
            return domain.EndsWith("." + pattern[2..], StringComparison.OrdinalIgnoreCase);
        return domain.Equals(pattern, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Specificity of a single pattern: exact = 2, single-level wildcard = 1, catch-all = 0 (spec §10.4).</summary>
    public static int Specificity(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return 0;
        // For a comma list, use the most specific member.
        var best = 0;
        foreach (var p in pattern.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            best = Math.Max(best, p == "*" ? 0 : p.StartsWith("*.", StringComparison.Ordinal) ? 1 : 2);
        return best;
    }

    public static string ExtractDomain(string address)
    {
        var at = address.LastIndexOf('@');
        return at >= 0 && at < address.Length - 1 ? address[(at + 1)..].ToLowerInvariant() : "";
    }
}
