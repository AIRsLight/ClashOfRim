using System.Collections;
using System.Text.Json;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Trades;
using RimTest.Protocol;
using RimTest.Runtime;
using Verse;
using static ClashOfRim.RimTestBridge.BridgeReflection;

namespace ClashOfRim.RimTestBridge;

internal sealed class GiftCommands
{
    private readonly ObjectRegistry objects;
    internal GiftCommands(CommandRouter router, ObjectRegistry objects, BridgeActionPolicy policy)
    {
        this.objects = objects;
        router.Register("clash.gift.inventory", "Original gift dialog caravan inventory candidates.", "observe", a =>
            ObjectInspector.Paginate(((IEnumerable<Thing>)Invoke(Gift(a), "GetInventoryThings")!).Select(t => new
            { reference = objects.Add(t), defName = t.def.defName, count = t.stackCount }).ToArray(), a));
        router.Register("clash.gift.select", "Select a bounded quantity from currently available caravan cargo in the original gift dialog.", "semantic", a =>
        {
            policy.RequireLoopback(); var w = Gift(a); var thing = objects.Get<Thing>(a.String("thing")); int count = a.Int("count", 1);
            Set(w, "nextInventoryRefreshTick", 0);
            if (count < 1 || count > thing.stackCount || !((IEnumerable<Thing>)Invoke(w, "GetInventoryThings")!).Contains(thing))
                throw new CommandException("disabled", "Gift item/count is no longer available.");
            Invoke(w, "AddThing", thing); Field<List<TradeOfferSelection>>(w, "selectedThings").Single(s => s.Thing == thing).Count = count;
            Field<IDictionary>(w, "countBuffers")[thing.ThingID] = count.ToString(); return new { selected = count };
        });
        router.Register("clash.gift.submit", "Submit the original gift editor; retains its confirmation and server distance/permission validation.", "semantic", a =>
        {
            policy.RequireLoopback(); var w = Gift(a); string message = a.OptionalString("message") ?? "";
            bool forced = a.Bool("forced", Field<bool>(w, "forcedDelivery"));
            if (!Field<bool>(w, forced ? "forcedAvailable" : "normalAvailable") || Field<IList>(w, "selectedThings").Count == 0)
                throw new CommandException("disabled", "Gift mode unavailable or no selected items.");
            if (message.Length > 2000) throw new CommandException("invalid_arguments", "Message is too long.");
            Set(w, "forcedDelivery", forced); Set(w, "message", message); Invoke(w, "Submit"); return new { state = "confirmation_required" };
        });
    }
    private GiftDeliveryDialogWindow Gift(JsonElement a)
    {
        var w = objects.Get<GiftDeliveryDialogWindow>(a.String("window"));
        if (!Find.WindowStack.Windows.Contains(w) || Find.WindowStack.Windows.OfType<Dialog_MessageBox>().Any())
            throw new CommandException("invalid_context", "Gift editor is closed or waiting for confirmation.");
        return w;
    }
}
