using WebPos.Components.Account;

namespace WebPos.Tests;

public class RedirectTests
{
    [Theory]
    [InlineData("/", true)]
    [InlineData("/reports", true)]
    [InlineData("/transactions?from=2026-10-01", true)]
    [InlineData("https://evil.example", false)]
    [InlineData("//evil.example", false)]
    [InlineData("/\\evil.example", false)]
    [InlineData("reports", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_local_paths_are_allowed_as_return_urls(string? url, bool expected)
    {
        Assert.Equal(expected, IdentityRedirectManager.IsLocalUrl(url));
    }
}
