using Microsoft.AspNetCore.Components;

namespace WebPos.Components.Account;

/// <summary>Redirects from the static server-rendered account pages, refusing anything that is not a local URL.</summary>
public sealed class IdentityRedirectManager(NavigationManager navigationManager)
{
    public static bool IsLocalUrl(string? url) =>
        !string.IsNullOrEmpty(url)
        && url.StartsWith('/')
        && !url.StartsWith("//", StringComparison.Ordinal)
        && !url.StartsWith("/\\", StringComparison.Ordinal)
        && Uri.IsWellFormedUriString(url, UriKind.Relative);

    /// <summary>Redirects to <paramref name="returnUrl"/> if it is local, otherwise to the home page.</summary>
    public void RedirectTo(string? returnUrl) => navigationManager.NavigateTo(IsLocalUrl(returnUrl) ? returnUrl! : "");

    public void RedirectTo(string relativePath, Dictionary<string, object?> queryParameters)
    {
        var target = navigationManager.GetUriWithQueryParameters(navigationManager.ToAbsoluteUri(relativePath).ToString(), queryParameters);
        navigationManager.NavigateTo(navigationManager.ToBaseRelativePath(target));
    }
}
