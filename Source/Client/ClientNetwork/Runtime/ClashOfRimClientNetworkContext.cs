namespace AIRsLight.ClashOfRim.ClientNetwork;

public sealed class ClashOfRimClientNetworkContext
{
    public ClashOfRimClientNetworkContext(
        string serverBaseUrl,
        string userId,
        string colonyId,
        string? currentSnapshotId,
        string? steamAuthTicket,
        string? offlinePassword,
        string? authToken)
    {
        ServerBaseUrl = ClashOfRimServerUrlUtility.NormalizeHttpBaseUrl(serverBaseUrl);
        UserId = userId;
        ColonyId = colonyId;
        CurrentSnapshotId = currentSnapshotId;
        SteamAuthTicket = steamAuthTicket;
        OfflinePassword = offlinePassword;
        AuthToken = authToken;
    }

    public string ServerBaseUrl { get; }

    public string UserId { get; }

    public string ColonyId { get; }

    public string? CurrentSnapshotId { get; }

    public string? SteamAuthTicket { get; }

    public string? OfflinePassword { get; }

    public string? AuthToken { get; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(ServerBaseUrl) &&
        !string.IsNullOrWhiteSpace(UserId) &&
        !string.IsNullOrWhiteSpace(ColonyId);

    public bool HasServerIdentity =>
        !string.IsNullOrWhiteSpace(ServerBaseUrl) &&
        !string.IsNullOrWhiteSpace(UserId);

    public static ClashOfRimClientNetworkContext FromSettings(ClashOfRimSettings settings)
    {
        return new ClashOfRimClientNetworkContext(
            settings.ServerBaseUrl,
            settings.UserId,
            settings.ColonyId,
            settings.CurrentSnapshotId,
            settings.SteamAuthTicket,
            settings.OfflinePassword,
            settings.AuthToken);
    }
}
