using AIRsLight.ClashOfRim;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.CoreCompatibility;
using AIRsLight.ClashOfRim.Multiplayer;
using AIRsLight.ClashOfRim.MainMenu;
using AIRsLight.ClashOfRim.Multiplayer.Pages;
using AIRsLight.ClashOfRim.Modules.Multiplayer;
using AIRsLight.ClashOfRim.ServerShop;
using AIRsLight.ClashOfRim.Support;
using AIRsLight.ClashOfRim.Trades;
using AIRsLight.ClashOfRim.WorldObjects;
using AIRsLight.ClashOfRim.CompatibilityClient;
using AIRsLight.ClashOfRim.Modules.Compatibility;
using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.Traits;
using AIRsLight.ClashOfRim.DlcCompatibility;
using HarmonyLib;
using RimTest.Protocol;
using RimWorld;
using Verse;

namespace ClashOfRim.RimTestBridge;

internal static class UiLayoutWindowFixture
{
    internal static Window Create(string target)
    {
        var mod = LoadedModManager.GetMod<ClashOfRimMod>();
        const string longName = "RimTest Александра Северная колония – 超长殖民地名称";
        if (target == "shop-buy-bottom")
        {
            var window = Create("shop-buy");
            // Start at the end; native BeginScrollView clamps this after content measurement.
            AccessTools.Field(window.GetType(), "editorContentHeight").SetValue(window, 10000f);
            AccessTools.Field(window.GetType(), "editorScrollPosition").SetValue(window, new UnityEngine.Vector2(0f, 10000f));
            return window;
        }
        if (target.StartsWith("compatibility-"))
        {
            var response = new ModLoginResponseDto { CompatibilityIssues = new() { new() {
                Code = "ModMissing", Severity = "Error", Subject = longName, Message = longName } } };
            var model = (ClientCompatibilityMismatch)AccessTools.Method(typeof(ClashOfRimMod), "CreateCompatibilityMismatch")
                .Invoke(mod, [response, null, null, null, null])!;
            if (!model.Open()) throw new InvalidOperationException("Compatibility fixture did not open.");
            var window = new CompatibilityMismatchWindow(model);
            var field = AccessTools.Field(window.GetType(), "selectedTab");
            field.SetValue(window, Enum.Parse(field.FieldType, target[14..]));
            return window;
        }
        if (target.StartsWith("main-"))
        {
            var window = new ClashOfRimMultiplayerWindow { def = DefDatabase<MainButtonDef>.AllDefsListForReading.Single(d => d.tabWindowClass == typeof(ClashOfRimMultiplayerWindow)) };
            var field = AccessTools.Field(window.GetType(), "selectedTab"); field.SetValue(window, Enum.Parse(field.FieldType, target[5..]));
            return window;
        }
        if (target.StartsWith("admin-"))
        {
            var window = (ClashOfRimMultiplayerWindow)Create("main-Admin");
            var factory = (MultiplayerPageFactory)AccessTools.Field(typeof(ClashOfRimMultiplayerWindow), "factory").GetValue(null)!;
            var pages = factory.Create();
            AccessTools.Field(window.GetType(), "composition").SetValue(window, factory);
            AccessTools.Field(window.GetType(), "pages").SetValue(window, pages);
            var model = (ClientAdminPanel)AccessTools.Field(typeof(MultiplayerAdminPage), "model").GetValue(pages.Admin)!;
            model.Select(Enum.Parse<ClientAdminTab>(target[6..]))();
            return window;
        }
        return target switch
        {
            "trade-create" => new TradeOrderDialogWindow(mod, Find.CurrentMap),
            "trade-market" => new TradeMarketWindow(mod),
            "shop-edit" => new ServerShopListingDialogWindow(mod, null),
            "shop-sell" or "shop-buy" => new ServerShopListingDialogWindow(mod, new ModServerShopListingDto {
                ListingId = "ui-fixture", ListingKind = target == "shop-buy" ? "BuyFromPlayer" : "SellToPlayer",
                Item = new ModThingReferenceDto { DefName = "Apparel_Parka", StuffDefName = "Cloth", StackCount = 1, Quality = "Excellent", HitPoints = 90 },
                PriceSilver = 1234567, BasePriceSilver = 1234567, StockCount = 999999 }),
            "appearance" => new ColonyAppearanceDialogWindow(longName, ColonyAppearanceSelection.Empty, _ => { }),
            "baseline-progress" => new CompatibilityBaselineOverrideWindow((_, done) => { done(longName, true); return Task.CompletedTask; }),
            "select-trait" => new TraitTrainerSelectionDialogWindow(new ModThingReferenceDto()),
            "select-genes" => new BiotechGeneSelectionDialogWindow(new ModThingReferenceDto()),
            "edit-genes" => new BiotechThingReferenceDialogWindow(new ModThingReferenceDto()),
            "support-duration" => new SupportDurationDialogWindow(longName, longName, (_, _) => { }),
            "support-request" => new AllySupportRequestDialogWindow(longName, _ => Task.CompletedTask),
            "abandon" => new AbandonPlayerColonyConfirmationWindow(longName, () => Task.CompletedTask),
            "relocation" => new ColonyRelocationConfirmationWindow(longName, 12345, () => { }),
            "entry" => new ClashOfRimServerEntryDialog(mod),
            "registration" => new ClashOfRimAccountRegistrationDialog(mod, "http://127.0.0.1", longName),
            "progress" => new ServerEntryProgressWindow(ClashOfRimText.Key("ClashOfRim.Multiplayer.TabTrade"), longName, 0.5f, true),
            "session-expired" => new SessionExpiredWindow(ClashOfRimText.Key("ClashOfRim.Multiplayer.TabAccount"), longName, () => true, _ => { }, _ => { }),
            "snapshot-failure" => new SnapshotConfirmationFailureWindow(longName, longName, () => { }, () => { }, () => true),
            "select-item" => new AdminThingDefSelectionDialogWindow(_ => { }),
            "select-incident" => new AdminIncidentDefSelectionDialogWindow(_ => { }),
            "select-research" => new TradeResearchProjectSelectionDialogWindow(new ModThingReferenceDto(), false),
            _ => throw new CommandException("invalid_arguments", "Unknown layout target: " + target)
        };
    }
}
