using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Diplomacy;

// User actions compose selection and creation without exposing settings or the Mod.
internal sealed class ClientDiplomacyActions
{
    private readonly ClientPlayerDirectory players;
    private readonly ClientDiplomacyCreation creation;
    internal ClientDiplomacyActions(ClientPlayerDirectory players, ClientDiplomacyCreation creation)
    { this.players = players; this.creation = creation; }

    internal bool Select(ModPlayerSummaryDto? player) => player is not null && players.Select(ClientPlayerSelection.From(player));
    internal bool Create(string kind, ModPlayerSummaryDto? target, string? message, out Task work)
    {
        using var p = ClientSessionWork.EnterPublication();
        work = Task.CompletedTask;
        if (target is null) return false;
        var selected = ClientPlayerSelection.From(target);
        return players.Select(selected, showStatus: false)
            && creation.Start(kind, new(selected.UserId, selected.ColonyId, selected.DisplayName), message, out work);
    }
    internal bool Create(string kind, string? message, string? displayName, out Task work)
    {
        using var p = ClientSessionWork.EnterPublication();
        var selected = players.Selected;
        return creation.Start(kind, new(selected.UserId, selected.ColonyId, displayName), message, out work);
    }
}
