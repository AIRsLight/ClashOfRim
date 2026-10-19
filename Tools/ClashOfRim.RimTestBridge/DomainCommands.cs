using System.Collections;
using System.Reflection;
using System.Text.Json;
using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.CompatibilityClient;
using AIRsLight.ClashOfRim.Multiplayer;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.Trades;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Support;
using AIRsLight.ClashOfRim.Mercenaries;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using HarmonyLib;
using RimTest.Protocol;
using RimTest.RimWorld;
using RimTest.Runtime;
using RimWorld;
using Verse;

namespace ClashOfRim.RimTestBridge;

/// <summary>Dialog-level semantic controls and business observations for the external test mod.</summary>
internal sealed partial class DomainCommands
{
    private readonly GameBridge bridge;
    private readonly EventDetailsObservation eventDetails = new(
        (int? knownVersion, out int version) => Mod.ReadEventDetails(knownVersion, out version));
    private static ClashOfRimMod Mod => LoadedModManager.GetMod<ClashOfRimMod>();
    private static readonly Type Component = typeof(ClashOfRimMod).Assembly.GetType("AIRsLight.ClashOfRim.ClashOfRimGameComponent", true)!;
    internal DomainCommands(GameBridge bridge)
    {
        this.bridge = bridge;
        RegisterStability();
        RegisterCatalog();
        RegisterBank();
        RegisterRelocation();
        RegisterGravship();
        RegisterDlcPersistence();
        RegisterAccountLifecycle();
        var r = bridge.Router;
        r.Register("clash.compatibility", "Observe the original compatibility decision; only accepted language warnings offer continuation.", "observe", _ =>
        {
            var window = Find.WindowStack.WindowOfType<CompatibilityMismatchWindow>();
            return new { open = window is not null, canContinue = window?.CanContinue == true };
        });
        r.Register("clash.compatibility.continue", "Choose the original Join Anyway button when the product permits a language-only warning.", "semantic", _ =>
        {
            Loopback();
            var window = Find.WindowStack.WindowOfType<CompatibilityMismatchWindow>();
            if (window?.TryContinue() != true) throw new CommandException("disabled", "Compatibility cannot be bypassed.");
            return new { invoked = true };
        });
        r.Register("clash.support.return", "Use the original quest early-return button, including completion tracking.", "semantic", a =>
        {
            Loopback(); string id = a.String("eventId");
            var part = Find.QuestManager.QuestsListForReading.SelectMany(q => q.PartsListForReading.OfType<QuestPart_ClashSupportPawn>())
                .SingleOrDefault(p => p.EventId == id && p.CanStartEarlyReturnNow);
            if (part is null) throw new CommandException("disabled", "No active quest accepts early return.");
            return new { accepted = part.TryStartEarlyReturnNow() };
        });
        r.Register("clash.mercenaries", "Observe original mercenary quest contracts and current pawn/recall state.", "observe", a =>
            ObjectInspector.Paginate(Find.QuestManager.QuestsListForReading.SelectMany(q => q.PartsListForReading.OfType<QuestPart_ClashMercenary>()).Select(p => new
            {
                reference = bridge.Objects.Add(p),
                p.ContractId,
                p.SkillDefName,
                p.SkillLevel,
                p.PriceSilver,
                p.ExpiresAtGameTicks,
                p.Completed,
                p.RecallStarted,
                p.RecallShuttleWaiting,
                p.RecallShuttleLoaded,
                pawn = p.Pawn is null ? null : bridge.Objects.Add(p.Pawn),
                spawned = p.Pawn?.Spawned == true,
                actualSkill = DefDatabase<SkillDef>.GetNamedSilentFail(p.SkillDefName) is SkillDef skill ? p.Pawn?.skills?.GetSkill(skill)?.Level : null,
                shuttle = p.RecallShuttle is null ? null : bridge.Objects.Add(p.RecallShuttle)
            }).ToArray(), a));
        r.Register("clash.mercenary.recall", "Use the original quest recall action on an active contract.", "semantic", a =>
        {
            Loopback(); var part = bridge.Objects.Get<QuestPart_ClashMercenary>(a.String("contract"));
            if (!Find.QuestManager.QuestsListForReading.Any(q => q.PartsListForReading.Contains(part)) || !part.CanStartRecallNow)
                throw new CommandException("disabled", "Mercenary cannot be recalled now.");
            return new { accepted = part.TryStartRecallNow() };
        });
        r.Register("clash.support.assignments", "Read current received support assignments and their stable event/pawn identities.", "observe", a =>
        {
            var component = Current.Game?.GetComponent<ClashOfRimGameComponent>();
            var assignments = component is null ? Array.Empty<ActiveSupportPawnAssignment>() : ((IEnumerable<ActiveSupportPawnAssignment>)Invoke(component, "CopyActiveSupportAssignments")!).ToArray();
            return ObjectInspector.Paginate(assignments.Select(s => new { s.EventId, s.PawnGlobalKey, s.PawnThingId, s.OwnerUserId, s.PermanentSupport, s.SupportDurationDays, s.FinishInProgress }).ToArray(), a);
        });
        r.Register("clash.world", "Read world identity and generated topology counts without credentials or payload contents.", "observe", _ => new
        {
            worldConfigurationId = Field<string>(Field<object>(Mod, "settings"), "CurrentWorldConfigurationId"),
            seed = Find.World?.info?.seedString,
            coverage = Find.World?.info?.planetCoverage,
            persistentRandomValue = Find.World?.info?.persistentRandomValue,
            layers = Find.World?.grid?.PlanetLayers.Values.Select(l => new { id = l.LayerID, defName = l.Def.defName, tiles = l.TilesCount }).ToArray()
        });
        r.Register("clash.gift.inventory", "Original gift dialog caravan inventory candidates.", "observe", a =>
            ObjectInspector.Paginate(((IEnumerable<Thing>)Invoke(Gift(a), "GetInventoryThings")!).Select(t => new
            { reference = bridge.Objects.Add(t), defName = t.def.defName, count = t.stackCount }).ToArray(), a));
        r.Register("clash.gift.select", "Select a bounded quantity from currently available caravan cargo in the original gift dialog.", "semantic", a =>
        {
            Loopback(); var w = Gift(a); var thing = bridge.Objects.Get<Thing>(a.String("thing")); int count = a.Int("count", 1);
            Set(w, "nextInventoryRefreshTick", 0);
            if (count < 1 || count > thing.stackCount || !((IEnumerable<Thing>)Invoke(w, "GetInventoryThings")!).Contains(thing))
                throw new CommandException("disabled", "Gift item/count is no longer available.");
            Invoke(w, "AddThing", thing); Field<List<TradeOfferSelection>>(w, "selectedThings").Single(s => s.Thing == thing).Count = count;
            Field<IDictionary>(w, "countBuffers")[thing.ThingID] = count.ToString(); return new { selected = count };
        });
        r.Register("clash.gift.submit", "Submit the original gift editor; retains its confirmation and server distance/permission validation.", "semantic", a =>
        {
            Loopback(); var w = Gift(a); string message = a.OptionalString("message") ?? "";
            bool forced = a.Bool("forced", Field<bool>(w, "forcedDelivery"));
            if (!Field<bool>(w, forced ? "forcedAvailable" : "normalAvailable") || Field<IList>(w, "selectedThings").Count == 0)
                throw new CommandException("disabled", "Gift mode unavailable or no selected items.");
            if (message.Length > 2000) throw new CommandException("invalid_arguments", "Message is too long.");
            Set(w, "forcedDelivery", forced); Set(w, "message", message); Invoke(w, "Submit"); return new { state = "confirmation_required" };
        });
        r.Register("clash.support.confirmDuration", "Confirm the original support-duration dialog using its offered 3..30 day range or permanent mode.", "semantic", a =>
        {
            Loopback(); var w = bridge.Objects.Get<Window>(a.String("window"));
            if (!Find.WindowStack.Windows.Contains(w) || w.GetType().FullName != "AIRsLight.ClashOfRim.Support.SupportDurationDialogWindow")
                throw new CommandException("invalid_context", "Expected current support duration dialog.");
            int days = a.Int("days", 7); bool permanent = a.Bool("permanent");
            if (days < 3 || days > 30) throw new CommandException("invalid_arguments", "days: 3..30.");
            var action = Field<Action<int?, bool>>(w, "confirmAction"); w.Close(); action(permanent ? null : days, permanent);
            return new { state = "invoked" };
        });
        r.Register("clash.events", "Read an isolated snapshot of the client event inbox.", "observe", a =>
        {
            return ObjectInspector.Paginate(eventDetails.Read().Select(Record).ToArray(), a);
        });
        r.Register("clash.inventory", "Count spawned home-map stock and colonists; excludes in-flight delivery containers.", "observe", _ => new
        {
            maps = Find.Maps.Where(m => m.IsPlayerHome).Select(m => new
            {
                reference = bridge.Objects.Add(m),
                mapId = "Map_" + m.uniqueID,
                tileId = m.Tile.tileId,
                layerId = m.Tile.Layer.LayerID,
                items = m.listerThings.AllThings.Where(t => t.def.category == ThingCategory.Item)
                    .GroupBy(t => t.def.defName).ToDictionary(g => g.Key, g => g.Sum(t => t.stackCount)),
                colonists = m.mapPawns.FreeColonistsSpawned.Select(p => new { reference = bridge.Objects.Add(p), id = p.ThingID }).ToArray()
            }).ToArray()
        });
        r.Register("clash.remote", "Read active observation/raid identity and object references.", "observe", _ =>
        {
            var session = Session();
            return new
            {
                active = session?.IsActive == true,
                session = session is null ? null : new
                {
                    reference = bridge.Objects.Add(session),
                    session.SessionId,
                    kind = session.Kind.ToString(),
                    session.Mode,
                    session.TargetUserId,
                    session.TargetSnapshotId,
                    session.TargetMapId,
                    session.ClientMapId,
                    session.RelatedEventId,
                    session.IsRaidBattle,
                    session.IsObservation
                },
                raid = AccessTools.Property(Component, "ActiveRaidBattleSession").GetValue(null) is { } raid ? bridge.Objects.Add(raid) : null
            };
        });
        r.Register("clash.remote.close", "Original remote-map primary action, with raid confirmation enabled.", "semantic", _ =>
        {
            Loopback();
            return new { invoked = RemoteMapSessionController.TryRunPrimaryAction(Mod, Session(), "RimTest player finish", true) };
        });
        r.Register("clash.trade.state", "Read the current trade editor and its original submit validation.", "observe", a =>
        {
            var w = Trade(a);
            return new
            {
                requireBeacon = Field<bool>(w, "requireTradeBeaconRange"),
                quoteInProgress = Field<bool>(w, "quoteInProgress"),
                canSubmit = Invoke(w, "CanSubmit"),
                quoteStatus = Field<string>(w, "quoteStatus"),
                offered = Field<List<TradeOfferSelection>>(w, "offeredThings").Select(s => new { thing = bridge.Objects.Add(s.Thing), defName = s.Thing.def.defName, count = s.Count }).ToArray(),
                requested = Field<List<ModThingReferenceDto>>(w, "requestedThings").Select(Record).ToArray()
            };
        });
        r.Register("clash.trade.beacon", "Toggle original trade editor beacon restriction and clear its selections as the UI does.", "semantic", a =>
        {
            Loopback(); var w = Trade(a); Editing(w); bool value = a.Bool("required");
            if (value != Field<bool>(w, "requireTradeBeaconRange"))
            {
                Set(w, "requireTradeBeaconRange", value);
                Field<IList>(w, "offeredThings").Clear(); Field<IDictionary>(w, "offeredThingIndex").Clear(); Field<IDictionary>(w, "offeredCountBuffers").Clear();
                Set(w, "offerSelectionVersion", Field<int>(w, "offerSelectionVersion") + 1); Invoke(w, "ClearOfferCandidateCache");
            }
            return new { required = value };
        });
        r.Register("clash.trade.candidates", "Original currently accessible offer candidates; filters compatibility and beacon range.", "observe", a =>
            ObjectInspector.Paginate(((IEnumerable<Thing>)Invoke(Trade(a), "GetOfferCandidates")!).Select(t => new
            { reference = bridge.Objects.Add(t), defName = t.def.defName, count = t.stackCount, label = t.LabelCap.ToString() }).ToArray(), a));
        r.Register("clash.trade.offer", "Select an original offer candidate and a bounded stack count in the current editor.", "semantic", a =>
        {
            Loopback(); var w = Trade(a); Editing(w); var thing = bridge.Objects.Get<Thing>(a.String("thing")); int count = a.Int("count", 1);
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
        r.Register("clash.trade.request", "Add a request using original definition eligibility and metadata builders. Complex requests remain editable through their UI.", "semantic", a =>
        {
            Loopback(); var w = Trade(a); Editing(w); var def = DefDatabase<ThingDef>.GetNamedSilentFail(a.String("defName")); int count = a.Int("count", 1);
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
            return Record(item);
        });
        r.Register("clash.trade.submit", "Original quote-and-confirm flow; never bypasses fee calculation or confirmation.", "semantic", a =>
        {
            Loopback(); var w = Trade(a); Editing(w);
            if (!(bool)Invoke(w, "CanSubmit")!) throw new CommandException("disabled", "Original editor validation rejected the draft.");
            Invoke(w, "StartQuoteAndConfirm"); return new { state = "invoked" };
        });
        r.Register("clash.shop.template", "Build a simple shop item with the original admin editor. Does not publish a listing.", "semantic", a =>
        {
            Loopback(); string name = a.String("defName"); int count = a.Int("count", 1);
            var def = ((IEnumerable<ThingDef>)InvokeStatic(typeof(ServerShopListingDialogWindow), "SearchThingDefs", name)!).SingleOrDefault(d => d.defName == name);
            if (def is null || count < 1 || count > 100000) throw new CommandException("invalid_arguments", "Invalid shop item/count.");
            var item = (ModThingReferenceDto)InvokeStatic(typeof(ServerShopListingDialogWindow), "BuildItemReference", def)!; item.StackCount = count;
            if (!(bool)InvokeStatic(typeof(ServerShopListingDialogWindow), "HasCompleteItem", item, ThingReferenceSurfaces.ServerShopListing)!)
                throw new CommandException("unsupported", "Item requires further metadata in the original editor.");
            return Record(item);
        });
    }
    private object Record(object o) => new { reference = bridge.Objects.Add(o), data = o };
    private static ActiveRemoteMapSession? Session() => (ActiveRemoteMapSession?)AccessTools.Property(Component, "ActiveRemoteMapSession").GetValue(null);
    private TradeOrderDialogWindow Trade(JsonElement a)
    {
        var w = bridge.Objects.Get<TradeOrderDialogWindow>(a.String("window"));
        if (!Find.WindowStack.Windows.Contains(w)) throw new CommandException("stale_window", "Trade editor is closed.");
        return w;
    }
    private GiftDeliveryDialogWindow Gift(JsonElement a)
    {
        var w = bridge.Objects.Get<GiftDeliveryDialogWindow>(a.String("window"));
        if (!Find.WindowStack.Windows.Contains(w) || Find.WindowStack.Windows.OfType<Dialog_MessageBox>().Any())
            throw new CommandException("invalid_context", "Gift editor is closed or waiting for confirmation.");
        return w;
    }
    private static void Editing(TradeOrderDialogWindow w)
    {
        if (Field<bool>(w, "quoteInProgress") || Find.WindowStack.Windows.OfType<Dialog_MessageBox>().Any())
            throw new CommandException("disabled", "Resolve the pending quote/confirmation first.");
    }
    private static void Loopback()
    {
        string? address = AccessTools.Property(typeof(ClashOfRimMod), "ServerBaseUrl").GetValue(Mod) as string;
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Scheme != "http")
            throw new CommandException("invalid_context", "Test actions require a loopback HTTP server.");
    }
    private static T Field<T>(object o, string name) => (T)AccessTools.Field(o.GetType(), name).GetValue(o)!;
    private static void Set(object o, string name, object value) => AccessTools.Field(o.GetType(), name).SetValue(o, value);
    private static object? Invoke(object o, string name, params object[] args) => AccessTools.Method(o.GetType(), name).Invoke(o, args);
    private static object? InvokeStatic(Type type, string name, params object[] args) => AccessTools.Method(type, name).Invoke(null, args);
}
