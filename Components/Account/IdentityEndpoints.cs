using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using WebPos.Data;

namespace WebPos.Components.Account;

public static class IdentityEndpoints
{
    /// <summary>Endpoints the account UI needs besides its pages.</summary>
    public static IEndpointConventionBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var account = endpoints.MapGroup("/Account");

        // Posted by the logout form in the main layout.
        account.MapPost("/Logout", async (HttpContext context, IAntiforgery antiforgery, SignInManager<PosUser> signInManager) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context)) return Results.BadRequest();
            await signInManager.SignOutAsync();
            return Results.LocalRedirect("~/Account/Login");
        });

        return account;
    }
}
