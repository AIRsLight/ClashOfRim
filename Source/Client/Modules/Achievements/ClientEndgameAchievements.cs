using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Achievements;

internal enum ClientEndgameKind { Ship, Archonexus, VoidEmbrace, VoidDisrupt, CerebrexScavenged, CerebrexDestroyed }

internal interface IClientEndgameRuntime
{
    bool IsObserving { get; }
    ClientAchievementCandidates? Pending { get; }
}
internal interface IClientEndgamePresentation
{
    string Text(string key, params (string Name, object? Value)[] arguments);
    void Confirm(Action accept);
}

internal sealed class ClientEndgameAchievements
{
    private readonly ClientGameRequestTracker requests;
    private readonly ClientSessionLifecycle session;
    private readonly IClientEndgameRuntime runtime;
    private readonly IClientEndgamePresentation presentation;
    private readonly ClientLocalSnapshotConfirmation confirmation;
    private readonly HashSet<string> continuations = new(StringComparer.Ordinal);
    internal ClientEndgameAchievements(ClientSessionLifecycle session, IClientSnapshotGame game,
        IClientEndgameRuntime runtime, IClientEndgamePresentation presentation, ClientLocalSnapshotConfirmation confirmation)
    {
        this.session = session; this.runtime = runtime; this.presentation = presentation; this.confirmation = confirmation;
        requests = new(session, game, continuations.Clear);
    }
    internal event Action? Confirmed;
    private bool Active(ClientGameRequestTracker.Scope scope) => scope.Context.IsConfigured
        && !string.IsNullOrWhiteSpace(session.SessionId) && !string.IsNullOrWhiteSpace(session.ReadContext().CurrentSnapshotId)
        && scope.Boundary.HasGame && !runtime.IsObserving && !session.IsUnavailable;

    internal bool ConfirmOrDefer(string key, Action continuation)
    {
        using var publication = ClientSessionWork.EnterPublication();
        var scope = requests.Capture();
        if (!Active(scope)) return true;
        if (continuations.Remove(key)) return true;
        bool consumed = false;
        presentation.Confirm(() => requests.TryApply(scope, () =>
        {
            if (consumed || !Active(scope)) return;
            consumed = true;
            continuations.Add(key);
            try { continuation(); }
            finally { continuations.Remove(key); }
        }));
        return false;
    }

    internal Task SubmitAsync(IReadOnlyList<ModSnapshotAchievementCandidateDto> candidates)
    {
        using var publication = ClientSessionWork.EnterPublication();
        var scope = requests.Capture();
        if (!Active(scope) || candidates.Count == 0 || runtime.Pending is not { } pending) return Task.CompletedTask;
        pending.Enqueue(candidates);
        if (pending.Count == 0) return Task.CompletedTask;
        string uploading = presentation.Text("ClashOfRim.Endgame.UploadStarted");
        Task work = Task.CompletedTask;
        requests.TryApply(scope, () =>
        {
            if (!Active(scope)) return;
            work = confirmation.StartAsync(new()
            {
                Operation = LocalMutationKind.Endgame,
                UploadingStatus = uploading,
                ConfirmationOperation = "EndgameAchievement", SnapshotUploadKind = ModSnapshotUploadKinds.EndgameAchievement,
                BuildSuccessStatus = _ => presentation.Text("ClashOfRim.Endgame.UploadSucceeded"),
                BuildFailureStatus = result => Failure(result.Message ?? result.ErrorCode ?? ""),
                BuildExceptionStatus = exception => Failure(exception.Message),
                OnSuccessOnMainThread = _ => Confirmed?.Invoke()
            });
        });
        return work;
    }
    private string Failure(string reason) => presentation.Text("ClashOfRim.Endgame.UploadFailed", ("REASON", reason));

    internal static ModSnapshotAchievementCandidateDto Candidate(ClientEndgameKind kind, string eventKey)
    {
        var (id, label, icon) = kind switch
        {
            ClientEndgameKind.Ship => ("endgame_ship_launch", "EndgameShipLaunch", "endgame.ship.launch"),
            ClientEndgameKind.Archonexus => ("endgame_archonexus", "EndgameArchonexus", "endgame.archonexus"),
            ClientEndgameKind.VoidEmbrace => ("endgame_void_embrace", "EndgameVoid", "endgame.void"),
            ClientEndgameKind.VoidDisrupt => ("endgame_void_disrupt", "EndgameVoid", "endgame.void"),
            ClientEndgameKind.CerebrexScavenged => ("endgame_cerebrex_scavenged", "EndgameCerebrex", "endgame.cerebrex"),
            ClientEndgameKind.CerebrexDestroyed => ("endgame_cerebrex_destroyed", "EndgameCerebrex", "endgame.cerebrex"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        return new()
        {
            AchievementId = id, EventKey = kind == ClientEndgameKind.Ship ? eventKey + ":launch" : eventKey,
            Value = 1, Category = "Endgame", LabelKey = "ClashOfRim.Achievement." + label,
            IconId = icon, Color = kind == ClientEndgameKind.Ship ? "Purple" : "Red", AggregationKind = "Sum"
        };
    }
}
