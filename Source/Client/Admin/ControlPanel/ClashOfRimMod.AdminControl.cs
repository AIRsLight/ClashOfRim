using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    internal void ApplyAdministratorFlag(bool value) => administration.ObservePermission(value);
    internal void StartRefreshAdminStatus() => _ = administration.RefreshAsync();
    internal void StartUpdateAdminConfiguration(ModAdminConfigurationDto configuration) => _ = administration.SaveAsync(configuration);
    internal void StartAdminAction(string actionKind, string? targetUserId = null, string? targetColonyId = null,
        string? message = null, string? notificationSeverity = null, bool persistentNotification = true)
        => _ = administration.ActAsync(new(actionKind, targetUserId, targetColonyId, message, notificationSeverity, persistentNotification));
}
