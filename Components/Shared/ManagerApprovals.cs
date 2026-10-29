using Microsoft.AspNetCore.Components.Authorization;
using Radzen;
using WebPos.Services;

namespace WebPos.Components.Shared;

/// <summary>
/// Gets a manager to approve an action at the register. A signed-in manager approves their own actions;
/// a cashier stays signed in while a manager scans their ID card or types their password in a dialog.
/// </summary>
public class ManagerApprovals(AuthenticationStateProvider auth, DialogService dialogs)
{
    /// <summary>Returns the approving manager, or null if the dialog was cancelled.</summary>
    public async Task<Approver?> RequestAsync(string action)
    {
        var user = (await auth.GetAuthenticationStateAsync()).User;
        if (user.CanManage()) return new Approver(user.UserId(), user.DisplayName());

        return await dialogs.OpenAsync<ManagerApprovalDialog>("Manager approval",
            new Dictionary<string, object?> { [nameof(ManagerApprovalDialog.Action)] = action },
            new DialogOptions { Width = "28rem", CloseDialogOnOverlayClick = false }) as Approver;
    }
}
