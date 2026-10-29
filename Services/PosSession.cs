using WebPos.Data;

namespace WebPos.Services;

/// <summary>
/// Per-browser-tab state (scoped to the Blazor circuit), so the cart survives navigating to other pages and back.
/// Logging in or out reloads the page and starts a new circuit, so a session always belongs to one user.
/// </summary>
public class PosSession
{
    public Cart Cart { get; } = new();
    public CustomerGame Game { get; } = new();
    public Transaction? LastSale { get; set; }
}
