namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    internal bool AccountPasswordInProgress => passwordChange.Busy;
    internal string AccountPasswordStatus => passwordChange.Message;
    internal void StartChangeOfflinePassword(string currentPassword, string newPassword)
        => _ = passwordChange.StartAsync(currentPassword, newPassword);
}
