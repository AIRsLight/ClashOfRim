using System;
using System.Collections.Generic;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Snapshots;

internal sealed class ClientSnapshotPublication
{
    private readonly IClientSnapshotStore store;
    private readonly IClientSnapshotGame game;
    internal ClientSnapshotPublication(IClientSnapshotStore store, IClientSnapshotGame game)
    { this.store = store; this.game = game; }

    // Domain confirmations call this from their existing owned main-thread callback.
    internal void PublishCurrentGame(string snapshotId, string? lineageToken) =>
        Publish(game.BindCurrentGame(), snapshotId, lineageToken, Array.Empty<ModSnapshotAchievementCandidateDto>());

    internal void Publish(IClientSnapshotGameSession session, string snapshotId, string? lineageToken,
        IReadOnlyList<ModSnapshotAchievementCandidateDto> submittedAchievements)
    {
        if (string.IsNullOrWhiteSpace(snapshotId)) return;
        using var publication = ClientSessionWork.EnterPublication();
        // An absent token must not clear settings while leaving the game on its previous lineage.
        string? token = string.IsNullOrWhiteSpace(lineageToken) ? null : lineageToken;
        store.Publish(snapshotId, token);
        session.Publish(snapshotId, token, submittedAchievements);
        store.Persist();
    }
}
