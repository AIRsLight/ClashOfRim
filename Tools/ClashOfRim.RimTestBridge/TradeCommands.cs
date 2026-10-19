using System.Collections;
using System.Text.Json;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Trades;
using RimTest.Protocol;
using RimTest.Runtime;
using Verse;
using static ClashOfRim.RimTestBridge.BridgeReflection;

namespace ClashOfRim.RimTestBridge;

internal sealed class TradeCommands
{
    private readonly ObjectRegistry objects;
    internal TradeCommands(CommandRouter router, ObjectRegistry objects, BridgeActionPolicy policy)
    {
        this.objects = objects;
        router.Register("clash.trade.state", "Read the current trade editor and its original submit validation.", "observe", a =>
        {
            var w = Trade(a);
            return new
            {
                requireBeacon = Field<bool>(w, "requireTradeBeaconRange"),
                quoteInProgress = Field<bool>(w, "quoteInProgress"),
                canSubmit = Invoke(w, "CanSubmit"),
                quoteStatus = Field<string>(w, "quoteStatus"),
                offered = Field<List<TradeOfferSelection>>(w, "offeredThings").Select(s => new { thing = objects.Add(s.Thing), defName = s.Thing.def.defName, count = s.Count }).ToArray(),
                requested = Field<List<ModThingReferenceDto>>(w, "requestedThings").Select(objects.Record).ToArray()
            };
        });
        router.Register("clash.trade.beacon", "Toggle original trade editor beacon restriction and clear its selections as the UI does.", "semantic", a =>
        {
            policy.RequireLoopback(); var w = Trade(a); Editing(w); bool value = a.Bool("required");
            if (value != Field<bool>(w, "requireTradeBeaconRange"))
            {
                Set(w, "requireTradeBeaconRange", value);
                Field<IList>(w, "offeredThings").Clear(); Field<IDictionary>(w, "offeredThingIndex").Clear(); Field<IDictionary>(w, "offeredCountBuffers").Clear();
                Set(w, "offerSelectionVersion", Field<int>(w, "offerSelectionVersion") + 1); Invoke(w, "ClearOfferCandidateCache");
            }
            return new { required = value };
        });
        router.Register("clash.trade.candidates", "Original currently accessible offer candidates; filters compatibility and beacon range.", "observe", a =>
            ObjectInspector.Paginate(((IEnumerable<Thing>)Invoke(Trade(a), "GetOfferCandidates")!).Select(t => new
            { reference = objects.Add(t), defName = t.def.defName, count = t.stackCount, label = t.LabelCap.ToString() }).ToArray(), a));
        router.Register("clash.trade.offer", "Select an original offer candidate and a bounded stack count in the current editor.", "semantic", a =>
        {
            policy.RequireLoopback(); var w = Trade(a); Editing(w); var thing = objects.Get<Thing>(a.String("thing")); int count = a.Int("count", 1);
            if (count < 1 || count > (thing is Pawn ? 1 : thing.stackCount) || thing.Destroyed)
                throw new CommandException("invalid_arguments", "Invalid offer count.");
            var selection = (TradeOfferSelection?)Invoke(w, "FindOfferSelection", thing);
            if (selection is null)
            {
                // Invalidate cached availability before every write; a previous observation is not authorization.
                Invoke(w, "ClearOfferCandidateCache");
                if (!((IEnumerable<Thing>)Invoke(w, "GetOfferCandidates")!).Contains(thing)) throw new CommandException("disabled", "Thing is no longer an offer candidate.");
                Invoke(w, "AddOfferedThing", thing); selection = (TradeOfferSelection)Invoke(w, "FindOfferSelection", thing)!;
            }
            selection.Count = count; Field<IDictionary>(w, "offeredCountBuffers")[thing.ThingID] = count.ToString();
            return new { state = "selected", count };
        });
        router.Register("clash.trade.request", "Add a request using original definition eligibility and metadata builders. Complex requests remain editable through their UI.", "semantic", a =>
        {
            policy.RequireLoopback(); var w = Trade(a); Editing(w); var def = DefDatabase<ThingDef>.GetNamedSilentFail(a.String("defName")); int count = a.Int("count", 1);
            if (def is null || count < 1 || count > 100000 || !((IEnumerable<ThingDef>)InvokeStatic(typeof(TradeOrderDialogWindow), "RequestableThingDefs")!).Contains(def))
                throw new CommandException("invalid_arguments", "Invalid request definition/count.");
            var utility = typeof(TradeOrderDialogWindow).Assembly.GetType("AIRsLight.ClashOfRim.Trades.TradeUiUtility", true)!;
            int? hitPoints = null;
            if (a.TryGetProperty("minimumHitPointsPercent", out var percentValue))
            {
                int percent = percentValue.GetInt32();
                if (percent < 1 || percent > 100 || !(bool)InvokeStatic(utility, "DefSupportsHitPoints", def)!)
                    throw new CommandException("invalid_arguments", "Supported durability slider range: 1..100.");
                hitPoints = (int)InvokeStatic(utility, "HitPointsFromPercent", def, percent)!;
            }
            Invoke(w, "AddRequestedThing", def);
            var item = Field<List<ModThingReferenceDto>>(w, "requestedThings").Last(t => t.DefName == def.defName || t.MinifiedInnerDefName == def.defName);
            item.StackCount = count;
            if (hitPoints is int hp) InvokeStatic(typeof(TradeOrderDialogWindow), "SetRequestedHitPoints", item, hp);
            return objects.Record(item);
        });
        router.Register("clash.trade.submit", "Original quote-and-confirm flow; never bypasses fee calculation or confirmation.", "semantic", a =>
        {
            policy.RequireLoopback(); var w = Trade(a); Editing(w);
            if (!(bool)Invoke(w, "CanSubmit")!) throw new CommandException("disabled", "Original editor validation rejected the draft.");
            Invoke(w, "StartQuoteAndConfirm"); return new { state = "invoked" };
        });
    }
    private TradeOrderDialogWindow Trade(JsonElement a)
    {
        var w = objects.Get<TradeOrderDialogWindow>(a.String("window"));
        if (!Find.WindowStack.Windows.Contains(w)) throw new CommandException("stale_window", "Trade editor is closed.");
        return w;
    }
    private static void Editing(TradeOrderDialogWindow w)
    {
        if (Field<bool>(w, "quoteInProgress") || Find.WindowStack.Windows.OfType<Dialog_MessageBox>().Any())
            throw new CommandException("disabled", "Resolve the pending quote/confirmation first.");
    }
}
