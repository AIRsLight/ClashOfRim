using System;
using System.Linq;
using AIRsLight.ClashOfRim.Modules.Raids;
using AIRsLight.ClashOfRim.RemoteMaps;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

internal sealed class ClientRaidFinishActions
{
    internal static ClientRaidFinishActions? Current { get; private set; }
    internal static void Attach(ClientRaidFinishActions? value) => Current = value;
    private readonly ClientRaidSettlements settlements;
    internal ClientRaidFinishActions(ClientRaidSettlements settlements) => this.settlements = settlements;
    internal bool Request(ActiveRaidBattleSession? battle, string reason, bool confirmation = false)
        => Prepare(battle, reason, confirmation)?.Invoke() == true;
    internal Func<bool>? Prepare(ActiveRaidBattleSession? battle, string reason, bool confirmation)
    {
        if (!ReferenceEquals(Current, this) || battle is null) return null;
        var command = settlements.PrepareFinish(new ClientRaidSettlementRuntime(battle), reason);
        if (command is null) return null;
        bool shown = false;
        bool Ready() => ReferenceEquals(Current, this) && command.IsCurrent;
        bool Start()
        {
            if (!Ready()) return false;
            _ = command.StartAsync(); return true;
        }
        return () =>
        {
            if (!Ready()) return false;
            if (!confirmation) return Start();
            if (shown) return false;
            int count = CountPlayerPawns(battle);
            string text = count > 0 ? ClashOfRimText.Key("ClashOfRim.Raid.ConfirmFinishWithPlayerPawns", count.Named("COUNT"))
                : ClashOfRimText.Key("ClashOfRim.Raid.ConfirmFinishNoPawns");
            if (!Ready()) return false;
            shown = true;
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(text, () => Start()));
            return true;
        };
    }
    private static int CountPlayerPawns(ActiveRaidBattleSession battle)
    {
        var map = RemoteSessionMapUtility.FindActiveSessionMap(ActiveRemoteMapSession.FromRaidBattle(battle))?.Map;
        if (map?.mapPawns is null || Faction.OfPlayer is null) return 0;
        return map.mapPawns.AllPawnsSpawned.Count(pawn => pawn is not null && !pawn.Dead && !pawn.Destroyed && pawn.Faction == Faction.OfPlayer);
    }
}
