using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.Achievements;

internal sealed class ClientAchievementQueries
{
    private readonly ClientGameRequestTracker requests;
    private readonly IClientAchievementTransport transport;
    private readonly IClientAchievementPresentation presentation;
    private List<ModAchievementLeaderboardDto> boards = new();
    private List<ModAchievementSummaryDto> own = new();
    private ClientAchievementTarget target = new("", "");
    private int version;
    private string message = "";

    internal ClientAchievementQueries(ClientSessionLifecycle session, IClientSnapshotGame game,
        IClientAchievementTransport transport, IClientAchievementPresentation presentation)
    {
        this.transport = transport; this.presentation = presentation;
        requests = new(session, game, () => { boards = new(); own = new(); target = new("", ""); message = ""; version++; });
    }
    internal int Version => requests.Read(() => version);
    internal bool Busy => requests.Busy;
    internal string Message => requests.Read(() => message);
    internal ClientAchievementTarget Target => requests.Read(() => target);
    internal List<ModAchievementLeaderboardDto> CopyLeaderboards() => requests.Read(() => Copy(boards));
    internal List<ModAchievementSummaryDto> CopyAchievements() => requests.Read(() => Copy(own));
    internal void Reset() => requests.Reset();

    internal Task RefreshAsync(string? userId = null, string? colonyId = null)
    {
        using var publication = ClientSessionWork.EnterPublication();
        var scope = requests.Capture();
        if (!scope.Context.IsConfigured)
        {
            string text = presentation.Text("ClashOfRim.Achievement.StatusNotConfigured");
            requests.TryApply(scope, () => message = text);
            return Task.CompletedTask;
        }
        var request = requests.Begin(replace: true)!;
        var requested = ClientAchievementTarget.Resolve(request.Context, userId, colonyId);
        try
        {
            Status(request, "ClashOfRim.Achievement.StatusRefreshing");
            return ClientSessionWork.Run(() => ReadAsync(request, requested));
        }
        catch { requests.End(request); throw; }
    }

    private async Task ReadAsync(ClientGameRequestTracker.Request request, ClientAchievementTarget requested)
    {
        try
        {
            if (!requests.TryApply(request, static () => { })) return;
            var result = await transport.ListAsync(request.Context, requested, request.Scope.Owner.Token).ConfigureAwait(false);
            if (!requests.TryApply(request, static () => { })) return;
            if (!result.Success || result.Response is null)
                Status(request, "ClashOfRim.Achievement.StatusRefreshFailed", ("CODE", result.ErrorCode), ("MESSAGE", result.Message));
            else if (result.Response.Result is { Accepted: false } denied)
                Status(request, "ClashOfRim.Achievement.StatusRefreshRejected", ("CODE", denied.ErrorCode), ("MESSAGE", denied.Message));
            else if (!Complete(result.Response))
                Status(request, "ClashOfRim.Achievement.StatusRefreshFailed", ("CODE", "InvalidResponse"), ("MESSAGE", ""));
            else
            {
                var snapshot = Copy(result.Response);
                string text = "";
                if (!requests.TryApply(request, () => text = presentation.Text("ClashOfRim.Achievement.StatusRefreshed"))) return;
                requests.TryApply(request, () =>
                {
                    boards = snapshot.Leaderboards; own = snapshot.OwnAchievements; target = requested; version++;
                    message = text;
                });
            }
        }
        catch (Exception ex)
        {
            Status(request, "ClashOfRim.Achievement.StatusException", ("TYPE", ex.GetType().Name), ("MESSAGE", ex.Message));
            requests.TryApply(request, () => presentation.Diagnose(ex));
        }
        finally { requests.End(request); }
    }

    private void Status(ClientGameRequestTracker.Request request, string key, params (string Name, object? Value)[] arguments)
    {
        string text = "";
        if (requests.TryApply(request, () => text = presentation.Text(key, arguments)))
            requests.TryApply(request, () => message = text);
    }

    private static bool Complete(ModListAchievementsResponseDto response)
        => response.Result?.Accepted == true && response.Leaderboards is not null && response.OwnAchievements is not null
            && response.OwnAchievements.All(achievement => achievement is not null)
            && response.Leaderboards.All(board => board is not null && board.Entries is not null && board.Entries.All(entry => entry is not null));
    private static T Copy<T>(T value) => ClientProtocolJson.Deserialize<T>(ClientProtocolJson.Serialize(value), static type => type);
}
