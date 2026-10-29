using Bunit;
using WebPos.Components.Layout;
using WebPos.Data;
using WebPos.Services;

namespace WebPos.Tests;

public class LayoutTests(PostgresFixture db) : PageTestBase(db)
{
    [Fact]
    public async Task Sidebar_toggle_collapses_the_menu_to_icons()
    {
        var admin = await Db.AddUserAsync(Roles.SuperAdmin);
        SignIn(admin, Roles.SuperAdmin);
        var layout = Render<MainLayout>();
        string[] Labels() => layout.FindAll(".rz-navigation-item-text").Select(e => e.TextContent.Trim()).ToArray();
        int Icons() => layout.FindAll(".rz-sidebar .rz-navigation-item-icon").Count;

        layout.WaitForAssertion(() => Assert.Equal(["Checkout", "Transactions", "Reports", "Items", "Users"], Labels()));
        Assert.Equal(5, Icons());

        layout.Find(".rz-sidebar-toggle").Click();
        Assert.Empty(Labels());
        Assert.Equal(5, Icons());                                               // the icons stay
        Assert.Contains("width: max-content", layout.Find(".rz-sidebar").GetAttribute("style"));
        Assert.DoesNotContain("rz-sidebar-collapsed", layout.Find(".rz-sidebar").ClassName);
        Assert.Equal("Reports", layout.FindAll(".rz-sidebar [title]").Single(e => e.GetAttribute("title") == "Reports").GetAttribute("title"));

        layout.Find(".rz-sidebar-toggle").Click();
        Assert.Equal(5, Labels().Length);
    }
}
