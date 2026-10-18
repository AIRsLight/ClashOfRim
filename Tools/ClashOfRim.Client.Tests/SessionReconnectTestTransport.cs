using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;

// Shared controlled reconnect protocol for module and UI-workflow tests.
internal sealed class SessionReconnectTestTransport : IClientSessionReconnectTransport
{
    private readonly string scenario, user, snapshot;
    internal readonly List<string> Calls = new();
    internal string? PauseAt;
    internal ClashOfRimClientNetworkContext? MetadataContext;
    internal ModLoginResponseDto? LastLogin;
    internal ModDownloadLatestSnapshotResponseDto? LastMetadata;
    internal Action<string>? OnStage;
    internal Action<ModLoginResponseDto>? ConfigureLogin;
    internal readonly TaskCompletionSource<bool> Paused = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource<bool> Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal SessionReconnectTestTransport(string scenario = "success", string user = "user", string snapshot = "snapshot")
    { this.scenario = scenario; this.user = user; this.snapshot = snapshot; }
    private async Task Before(string stage)
    {
        Calls.Add(stage); OnStage?.Invoke(stage);
        if (PauseAt == stage) { Paused.SetResult(true); await Continue.Task; }
        if (scenario == stage + "-throw") throw new InvalidOperationException("transport failure");
    }
    public async Task<ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto>> PrepareAsync(ClashOfRimClientNetworkContext context, CancellationToken token)
    {
        await Before("prepare");
        if (scenario == "prepare-network") return ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto>.Failed("network", "offline");
        return ClashOfRimClientNetworkResult<ModPrepareWorldSessionResponseDto>.Ok(new()
        {
            Result = Accepted(scenario != "prepare-rejected"), HasExistingColony = scenario != "prepare-new-colony",
            AssignedColonyId = scenario == "prepare-colony" ? "COLONY" : "colony", LatestSnapshotId = scenario == "prepare-snapshot" ? "other" : snapshot
        });
    }
    public async Task<ClashOfRimClientNetworkResult<ModLoginResponseDto>> LoginAsync(ClashOfRimClientNetworkContext context,
        string purpose, CancellationToken token, Action<ModLoginResponseDto> captureReceipt)
    {
        await Before("login");
        if (scenario == "login-network") return ClashOfRimClientNetworkResult<ModLoginResponseDto>.Failed("network", "offline");
        var response = Login("candidate-session", "candidate-token");
        response.AuthenticatedUserId = scenario == "login-identity" ? " different-user " : " " + user + " ";
        if (scenario == "login-rejected") response.Result = Accepted(false);
        if (scenario == "login-no-token") response.AuthToken = null;
        if (scenario == "login-no-session") response.SessionId = null;
        ConfigureLogin?.Invoke(response);
        LastLogin = response;
        captureReceipt(response);
        if (scenario == "login-canceled-after-receipt") return ClashOfRimClientNetworkResult<ModLoginResponseDto>.Failed(nameof(OperationCanceledException), "canceled after receipt");
        return ClashOfRimClientNetworkResult<ModLoginResponseDto>.Ok(response);
    }
    public async Task<ClashOfRimClientNetworkResult<ModDownloadLatestSnapshotResponseDto>> ReadSnapshotAsync(ClashOfRimClientNetworkContext context, CancellationToken token)
    {
        MetadataContext = context;
        await Before("metadata");
        if (scenario == "metadata-network") return ClashOfRimClientNetworkResult<ModDownloadLatestSnapshotResponseDto>.Failed("network", "offline");
        LastMetadata = new()
        {
            Result = Accepted(scenario != "metadata-rejected"), SnapshotId = scenario == "metadata-snapshot" ? "other" : snapshot,
            ActiveRaidRecovery = new() { EventId = "active-raid" }
        };
        return ClashOfRimClientNetworkResult<ModDownloadLatestSnapshotResponseDto>.Ok(LastMetadata);
    }

    internal static ModProtocolResponseDto Accepted(bool value = true) => new() { Accepted = value, Message = value ? "ok" : "rejected" };
    internal static ModLoginResponseDto Login(string session, string token) => new() { Result = Accepted(), SessionId = session, AuthToken = token };
}
