using System;
using System.Linq;
using AIRsLight.ClashOfRim.Bank;
using AIRsLight.ClashOfRim.Modules.Mercenaries;
using AIRsLight.ClashOfRim.Quests;
using Verse;

namespace AIRsLight.ClashOfRim.Mercenaries;

internal sealed class ClientMercenaryRuntime : IClientMercenaryRuntime
{
    public IClientMercenarySource Capture() => new ClientMercenarySource(
        Find.Maps?.FirstOrDefault(map => map.IsPlayerHome) ?? Find.CurrentMap, Find.TickManager?.TicksGame ?? 0);
}
internal sealed class ClientMercenarySource : IClientMercenarySource
{
    private readonly Map? map;
    private Pawn? pawn;
    private bool awaitingArrival;
    public long GameTicks { get; }
    public int TicksPerDay => ClashManagedQuestTimingUtility.TicksPerDay;
    internal ClientMercenarySource(Map? map, long ticks) { this.map = map; GameTicks = ticks; }
    private bool MapAvailable => map is not null && Find.Maps?.Contains(map) == true;
    public string? Validate(int price)
    {
        if (!MapAvailable) return Text("StatusNoMap");
        if (price <= 0) return null;
        int available = ClientBankRuntime.CountPlayerHomeSilver();
        return available < price ? ClashOfRimText.Key("ClashOfRim.Mercenary.StatusInsufficientSilver",
            price.Named("NEEDED"), available.Named("AVAILABLE")) : null;
    }
    public ClientMercenaryLocalResult Prepare(ClientMercenaryCommand command, Action mutationStarting)
    {
        // Silver removal may fail after consuming some stacks. Its failure must
        // retain the pending mutation and must never offer another debit.
        mutationStarting();
        if (!ClientBankRuntime.TryConsumePlayerHomeSilver(command.Terms.Price, out var message))
            return new(ClientMercenaryPreparation.Failed, message);
        if (command.Selection.IsGuard) return new(ClientMercenaryPreparation.Prepared, "");
        pawn = MercenarySkillUtility.GenerateMercenaryPawn(command.Selection.Skill, command.Selection.Level, command.ContractId);
        ClashMercenaryQuestUtility.CreateMercenaryQuest(command.Contract(), pawn);
        awaitingArrival = true;
        return ResumeArrival();
    }
    public ClientMercenaryLocalResult ResumeArrival()
    {
        if (!awaitingArrival || pawn is null || pawn.Destroyed || pawn.Dead)
            return new(ClientMercenaryPreparation.Failed, Text("StatusArrivalPawnMissing"));
        if (!MapAvailable) return new(ClientMercenaryPreparation.AwaitingArrival, Text("StatusNoMap"));
        // A thrown engine call may already have created/loaded the ship. Only a
        // definite false (no arrival side effect) can be continued.
        awaitingArrival = false;
        if (!ClashMercenaryQuestUtility.TrySendArrivalShuttleToMap(pawn, map, out var message))
        { awaitingArrival = true; return new(ClientMercenaryPreparation.AwaitingArrival, message); }
        return new(ClientMercenaryPreparation.Prepared, "");
    }
    private static string Text(string key) => ClashOfRimText.Key("ClashOfRim.Mercenary." + key);
}
