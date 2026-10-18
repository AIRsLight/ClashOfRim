using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Diplomacy;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using Verse;

namespace AIRsLight.ClashOfRim.Diplomacy;

internal sealed class ClientPlayerDirectoryPresentation : IClientPlayerDirectoryPresentation
{
    public string Text(string key, params (string Name, object? Value)[] arguments) =>
        ClashOfRimText.Key(key, arguments.Select(argument => (argument.Value ?? "").Named(argument.Name)).ToArray());
    public string Admission(LocalMutationAdmission admission, LocalMutationState? pending) => ClientLocalMutationPresentation.AdmissionFailure(admission, pending);
    public void Diagnose(Exception exception) => Log.Warning("[ClashOfRim] List players failed: " + exception);
    public string Summary(IReadOnlyCollection<ModPlayerSummaryDto> players)
    {
        if (players.Count == 0) return ClashOfRimText.Key("ClashOfRim.Players.NoneRegistered");
        return ClashOfRimText.Key("ClashOfRim.Players.SummaryPrefix") + string.Join(ClashOfRimText.Key("ClashOfRim.ListSeparator"), players
            .OrderByDescending(player => player.Online).ThenBy(player => player.UserId, StringComparer.Ordinal).Take(8)
            .Select(player => ClashOfRimText.Key("ClashOfRim.Players.SummaryItem",
                ClashOfRimText.SafeArgument(string.IsNullOrWhiteSpace(player.DisplayName) ? player.UserId : player.DisplayName!).Named("PLAYER"),
                ClashOfRimText.Key(player.Online ? "ClashOfRim.Multiplayer.Online" : "ClashOfRim.Multiplayer.Offline").Named("STATUS"))));
    }
}

internal sealed class ClientPlayerDirectoryRuntime : IClientPlayerDirectoryRuntime
{
    public void EnsureProxy(string userId) => PlayerFactionProxyUtility.EnsureProxyForUser(userId);
    public void ApplyRelation(string userId, string relation) => ClientDiplomacyRelationEffects.Apply(userId, relation, "player-list-sync");
}
