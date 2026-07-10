using Dispatch.Core.Routing;

namespace Dispatch.Core.Tests;

public class DomainMatcherTests
{
    [Theory]
    [InlineData("acme.com", "acme.com", true)]            // exact
    [InlineData("ACME.com", "acme.com", true)]            // case-insensitive
    [InlineData("mail.acme.com", "acme.com", false)]      // subdomain ≠ exact
    [InlineData("mail.acme.com", "*.acme.com", true)]     // single-level wildcard
    [InlineData("a.b.acme.com", "*.acme.com", true)]      // deep subdomain still ends with .acme.com
    [InlineData("acme.com", "*.acme.com", false)]         // apex not matched by *.acme.com
    [InlineData("beta.com", "acme.com,beta.com", true)]   // comma list
    [InlineData("gamma.com", "acme.com,beta.com", false)]
    [InlineData("anything.org", "*", true)]               // catch-all
    public void Matches_pattern(string domain, string pattern, bool expected) =>
        Assert.Equal(expected, DomainMatcher.Matches(domain, pattern));

    [Theory]
    [InlineData("acme.com", 2)]
    [InlineData("*.acme.com", 1)]
    [InlineData("*", 0)]
    [InlineData(null, 0)]
    [InlineData("a.com,*.b.com", 2)]   // most-specific member wins
    public void Specificity_ranks_patterns(string? pattern, int expected) =>
        Assert.Equal(expected, DomainMatcher.Specificity(pattern));

    [Theory]
    [InlineData("user@Acme.com", "acme.com")]
    [InlineData("no-at-sign", "")]
    [InlineData("trailing@", "")]
    public void ExtractDomain_lowercases(string address, string expected) =>
        Assert.Equal(expected, DomainMatcher.ExtractDomain(address));
}
