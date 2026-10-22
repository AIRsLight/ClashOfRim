using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Multiplayer;
using AIRsLight.ClashOfRim.Modules.Shop;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;

internal static class ClientShopListingEditorTests
{
    internal static void Run() => RunAsync().WaitAsync(TimeSpan.FromSeconds(60)).GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                Search(); Inputs(); Selection(); Values(); DefaultRatios(); PriceBounds();
                foreach (string kind in new[] { "SellToPlayer", "BuyFromPlayer" })
                {
                    await NewListing(kind); await RoundTrip(kind); await ResizeExisting(kind);
                }
            }
            foreach (string change in new[] { "dialog", "page", "permission-return", "user", "colony", "auth", "game", "reset", "generation", "unavailable" }) StaleMenus(change);
            await LocalizedRatio(); await Busy();
        }
        finally { CultureInfo.CurrentCulture = previous; ClientSessionWork.Reset(); }
    }
    private static void Search()
    {
        using var f = new MultiplayerAdminShopTests.Fixture(); var runtime = new Runtime(); var editor = Create(f, runtime);
        runtime.Catalog = Enumerable.Range(0, 130).Select(i => new ClientShopDefinition("Item" + i.ToString(CultureInfo.InvariantCulture), "材料 " + i)).ToArray();
        Check(editor.Search("").Count == 100 && ClientShopListingEditor.SearchCatalog(runtime.Catalog, "").Count() == 130, "UI cap truncated the authoritative catalog.");
        Check(ReferenceEquals(editor.Search(""), editor.Search(null)), "Null/empty query compatibility changed.");
        var result = editor.Search("  ITEM129  ");
        Check(result.Single().DefName == "Item129" && ReferenceEquals(result, editor.Search("ITEM129")), "Search lost trimming, ordinal case or cache reuse.");
        Check(editor.Search("材料 129").Count == 1 && editor.Search("missing").Count == 0, "Localized label search failed.");
        runtime.Catalog = new[] { new ClientShopDefinition("Replacement", "ITEM129") };
        Check(editor.Search("ITEM129").Single().DefName == "Replacement", "Definition/language refresh reused the old search result.");
        editor.Select("Item129"); Check(editor.Item is null, "Removed catalog definition remained selectable.");
    }
    private static void Inputs()
    {
        using var f = new MultiplayerAdminShopTests.Fixture(); var runtime = new Runtime(); var editor = Create(f, runtime);
        Check(!editor.CanSave && editor.Save() is null, "Empty editor submitted."); editor.Select("plain");
        foreach (string text in new[] { "", "-", "2147483648", "1.5", "1,000", "-1" })
        {
            editor.StockText = text; Check(!editor.CanSave && editor.Save() is null, "Invalid stock submitted: " + text); editor.StockText = "0";
            editor.CountText = text; Check(!editor.CanSave, "Invalid count accepted: " + text); editor.CountText = "1";
        }
        foreach (string text in new[] { "", "-", "2147483648", "-1", "NaN", "Infinity", "1e999" })
        { editor.SetUnitPrice(text); Check(!editor.CanSave, "Invalid price accepted: " + text); editor.SetUnitPrice("2"); }
        editor.CountText = "0"; Check(!editor.CanSave, "Zero count accepted."); editor.CountText = "1";
        editor.SetUnitPrice("0"); Check(!editor.CanSave, "Zero price accepted."); editor.SetUnitPrice("2");
        foreach (string text in new[] { "", "NaN", "Infinity", "-Infinity", "1e999", "garbage" })
        { editor.RatioText = text; Check(!editor.CanSave && editor.Save() is null, "Invalid ratio submitted: " + text); }
        editor.RatioText = "1.25"; runtime.Complete = false; Check(!editor.CanSave, "Incomplete extension item submitted.");
        runtime.Complete = true; Check(editor.CanSave && f.Catalog.Calls == 0, "Valid zero-stock input rejected or validation dispatched work.");
    }
    private static void Selection()
    {
        using var f = new MultiplayerAdminShopTests.Fixture(); var runtime = new Runtime(); var editor = Create(f, runtime);
        editor.ChooseKind()("BuyFromPlayer"); editor.Select("packed");
        Check(runtime.Surfaces.SequenceEqual(new[] { ThingReferenceSurfaces.TradeRequest }), "Template initialized using the wrong or repeated surface.");
        var item = editor.Item!; var stuff = editor.ChooseStuff(); var quality = editor.ChooseQuality();
        stuff("Steel"); quality("Excellent"); editor.SetHitPointsPercent(73);
        Check(item.StuffDefName is null && item.MinifiedInnerStuffDefName == "Steel" && item.Quality == "Excellent"
            && item.MinifiedInnerQuality == "Excellent" && item.HitPoints == 146 && item.MinifiedInnerHitPoints == 146, "Minified editing lost inner/top-level semantics.");
        stuff("invalid"); Check(item.MinifiedInnerStuffDefName == "Steel", "Invalid material entered the template.");
        editor.ChooseRequirement(true)("AtMost"); editor.ChooseRequirement(false)("AtMost");
        Check(editor.QualityMode == "AtMost" && editor.HitPointsMode == "AtMost", "Requirement controls did not update the draft.");
        var oldMode = editor.ChooseRequirement(true); editor.Select("packed");
        stuff(null); quality(null); oldMode("AtLeast");
        Check(item.MinifiedInnerStuffDefName == "Steel" && item.Quality == "Excellent" && editor.Item!.Quality == "Normal"
            && editor.QualityMode == "AtMost", "Menu from a replaced selection changed its old or new item.");
        editor.ChooseKind()("SellToPlayer"); editor.ChooseKind()("SellToPlayer");
        Check(runtime.Surfaces.Count == 3 && runtime.Surfaces.Last() == ThingReferenceSurfaces.ServerShopListing, "Mode defaults repeated or used an old surface.");
        editor.ChooseStuff()(null); editor.ChooseQuality()(null); editor.SetHitPointsPercent(0);
        Check(editor.Item.HitPoints == 2 && editor.Item.MinifiedInnerQuality is null, "Any quality/lower HP boundary changed.");
        editor.Select("plain"); editor.ChooseStuff()("Steel"); editor.SetHitPointsPercent(150);
        Check(editor.Item!.StuffDefName == "Steel" && editor.Item.HitPoints == 200, "Ordinary item editing changed wrapper fields or failed upper HP clamp.");
        editor.Item.HitPoints = 67; editor.SetHitPointsPercent(editor.Features.HitPointsPercent);
        Check(editor.Item.HitPoints == 67, "Drawing an unchanged percentage rewrote exact durability.");
        editor.Item.HitPoints = null; editor.SetHitPointsPercent(100);
        Check(editor.Item.HitPoints is null, "Drawing the default percentage added a concrete durability requirement.");
    }
    private static void Values()
    {
        using var f = new MultiplayerAdminShopTests.Fixture(); var runtime = new Runtime(); var editor = Create(f, runtime); editor.Select("plain");
        Check(editor.UnitPriceText == "10" && runtime.Estimates == 1, "Default suggestion did not share one evaluation.");
        editor.SetUnitPrice(editor.UnitPriceText); editor.CountText = "3"; editor.Refresh();
        Check(editor.UnitPriceText == "10" && editor.LotPrice == 30 && runtime.Estimates == 2, "Quantity changed the unit suggestion or failed to update the lot price.");
        editor.Refresh(); Check(runtime.Estimates == 2, "Unchanged item repeated market estimation.");
        editor.Item!.Metadata["a"] = "b\u001fc=d"; editor.Refresh(); int estimates = runtime.Estimates;
        editor.Item.Metadata["a"] = "b"; editor.Item.Metadata["c"] = "d"; editor.Refresh();
        Check(runtime.Estimates == estimates + 1, "Delimiter collision kept a stale estimate.");
        editor.Item.ThingPackage = new() { Scribe = new() { XmlGzipBase64 = "first" } }; editor.Refresh(); estimates = runtime.Estimates;
        editor.Item.ThingPackage.Scribe.XmlGzipBase64 = "second"; editor.Refresh();
        Check(runtime.Estimates == estimates + 1, "Nested package change was missing from the value key.");
        editor.SetUnitPrice("unfinished"); runtime.PresentationOwner = new(); editor.Refresh();
        Check(editor.UnitPriceText == "unfinished" && editor.MarketValue == "value-" + runtime.Estimates, "Language change discarded manual input or kept localized value text.");
        runtime.ExtensionKeys = new[] { "a\u001fb", "c" }; editor.Refresh(); estimates = runtime.Estimates;
        runtime.ExtensionKeys = new[] { "a", "b\u001fc" }; editor.Refresh();
        Check(runtime.Estimates == estimates + 1, "Extension cache keys collided across field boundaries.");
        editor.Select("packed"); Check(editor.UnitPriceText == "10", "Selecting a new item retained the previous manual price.");
    }
    private static async Task RoundTrip(string kind)
    {
        using var f = new MultiplayerAdminShopTests.Fixture();
        const double ratio = 1.23456789012345;
        await f.Catalog.Load(new ModServerShopListingDto { ListingId = "existing", ListingKind = kind, StockCount = 0,
            BasePriceSilver = 19, PriceSilver = 21, PriceIncreaseRatio = ratio, QualityRequirementMode = "AtMost", HitPointsRequirementMode = "AtMost",
            Item = new() { DefName = "plain", StackCount = 3, Metadata = new() { ["label"] = "保留" } } });
        var runtime = new Runtime(); var editor = new ClientShopListingEditor(f.Model.PrepareEdit(f.Model.Read()[0]), runtime);
        Check(decimal.Parse(editor.UnitPriceText, CultureInfo.InvariantCulture) == 19m / 3 && editor.LotPrice == 19,
            "Existing lot price was treated as a unit price or overwritten by a suggestion.");
        editor.ChooseKind()(kind == "BuyFromPlayer" ? "SellToPlayer" : "BuyFromPlayer"); editor.ChooseKind()(kind);
        Check(editor.RatioText == ratio.ToString("R", CultureInfo.InvariantCulture), "Existing multiplier was replaced by a new-listing default.");
        Task? work = editor.Save(); Check(work is not null, "Valid existing draft did not submit."); var call = await f.Catalog.Next();
        Check(call.Edit is { ListingId: "existing", PriceSilver: 19, StockCount: 0, QualityRequirementMode: "AtMost", HitPointsRequirementMode: "AtMost" }
            && call.Edit.ListingKind == kind && call.Edit.PriceIncreaseRatio == ratio && call.Edit.Item.StackCount == 3
            && call.Edit.Item.Metadata["label"] == "保留", "Opening/saving changed price, ratio precision or listing fields.");
        Check(!editor.IsCurrent && editor.Save() is null, "Submitted editor dispatched twice."); call.Complete(new ModUpsertServerShopListingResponseDto()); await work!;
    }
    private static void DefaultRatios()
    {
        using var f = new MultiplayerAdminShopTests.Fixture(); var editor = Create(f, new Runtime());
        Check(editor.RatioText == "1.05", "New sale did not default to a 1.05 repeat multiplier.");
        // An unchanged text field draw must not turn the suggestion into a manual override.
        editor.RatioText = editor.RatioText;
        editor.ChooseKind()("BuyFromPlayer");
        Check(editor.RatioText == "0.95", "New buy order did not default to a 0.95 repeat multiplier.");
        editor.Select("plain"); editor.CountText = "4"; editor.Refresh();
        Check(editor.UnitPriceText == "10" && editor.LotPrice == 40 && editor.RatioText == "0.95", "Quantity/selection lost the buy order defaults.");
        editor.ChooseKind()("SellToPlayer");
        Check(editor.RatioText == "1.05", "Switching back to sale did not restore its default multiplier.");
        editor.RatioText = "1.23456789"; editor.ChooseKind()("BuyFromPlayer"); editor.Select("packed");
        Check(editor.RatioText == "1.23456789", "Mode/selection overwrote the administrator's multiplier.");
        editor.RatioText = "unfinished"; editor.ChooseKind()("SellToPlayer");
        Check(editor.RatioText == "unfinished" && !editor.CanSave, "Mode change discarded unfinished multiplier input.");
    }
    private static async Task NewListing(string kind)
    {
        using var f = new MultiplayerAdminShopTests.Fixture(); var runtime = new Runtime { UnitValue = 2.5f }; var editor = Create(f, runtime);
        editor.ChooseKind()(kind); editor.Select("plain"); editor.CountText = "3"; editor.Refresh();
        Check(editor.UnitPriceText == "2.5" && editor.LotPrice == 8, "Fractional market value was rounded before multiplying the lot size.");
        editor.SetUnitPrice("1.25"); editor.CountText = "4"; editor.Refresh();
        Check(editor.UnitPriceText == "1.25" && editor.LotPrice == 5, "Manual unit price did not calculate the lot total.");
        editor.CountText = "8"; editor.Refresh(); editor.ChooseQuality()("Excellent");
        Check(editor.UnitPriceText == "1.25" && editor.LotPrice == 10, "Count/quality edit lost the manual unit price.");
        Task? work = editor.Save(); Check(work is not null, "Valid unit price did not submit."); var call = await f.Catalog.Next();
        Check(call.Edit is { PriceSilver: 10, Item.StackCount: 8 } && call.Edit.ListingKind == kind
            && call.Edit.PriceIncreaseRatio == (kind == "BuyFromPlayer" ? 0.95 : 1.05), "Unit price or wrong default multiplier reached the per-lot wire contract.");
        call.Complete(new ModUpsertServerShopListingResponseDto()); await work!;
    }
    private static async Task ResizeExisting(string kind)
    {
        using var f = new MultiplayerAdminShopTests.Fixture();
        await f.Catalog.Load(new ModServerShopListingDto { ListingId = "existing", ListingKind = kind, BasePriceSilver = 19, PriceIncreaseRatio = 1,
            Item = new() { DefName = "plain", StackCount = 3 } });
        var editor = new ClientShopListingEditor(f.Model.PrepareEdit(f.Model.Read()[0]), new Runtime());
        string unit = editor.UnitPriceText; editor.CountText = "6"; editor.Refresh();
        Check(editor.UnitPriceText == unit && editor.LotPrice == 38, "Existing price did not scale with lot size.");
        editor.SetUnitPrice("2.5"); editor.CountText = "4"; editor.Refresh();
        Task? work = editor.Save(); Check(work is not null, "Edited existing unit price did not submit."); var call = await f.Catalog.Next();
        Check(call.Edit is { PriceSilver: 10, PriceIncreaseRatio: 1, Item.StackCount: 4 }, "Existing edit submitted a unit price as a lot price.");
        call.Complete(new ModUpsertServerShopListingResponseDto()); await work!;
    }
    private static void PriceBounds()
    {
        var culture = CultureInfo.CurrentCulture;
        foreach (var test in new[] { ("0.1", 1, 1), ("0.1", 75, 8), ("1.25", 2, 2), ("1.75", 2, 4),
            ("1", int.MaxValue, int.MaxValue), ("2147483647", 1, int.MaxValue) })
            Check(ClientShopRules.TryLotPrice(test.Item1, test.Item2, culture, out int price) && price == test.Item3, "Lot rounding/minimum/boundary changed: " + test);
        foreach (var test in new[] { ("0", 1), ("-1", 1), ("1", 0), ("1", -1), ("2147483647", 2), ("2147483647.5", 1),
            ("79228162514264337593543950335", int.MaxValue), ("Infinity", 1), ("NaN", 1), ("unfinished", 1) })
            Check(!ClientShopRules.TryLotPrice(test.Item1, test.Item2, culture, out _), "Invalid/overflowing lot accepted: " + test);
        foreach (int count in new[] { 1, 3, 7, 75, 999999, int.MaxValue })
            foreach (int total in new[] { 1, 19, int.MaxValue })
            {
                var listing = new ModServerShopListingDto { BasePriceSilver = total, Item = new() { StackCount = count } };
                Check(ClientShopRules.TryLotPrice(ClientShopRules.UnitPriceText(listing), count, culture, out int actual) && actual == total,
                    "Legacy lot amount failed exact read/save round trip.");
            }
    }
    private static async Task LocalizedRatio()
    {
        using var f = new MultiplayerAdminShopTests.Fixture(); CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        var editor = Create(f, new Runtime()); editor.Select("plain"); editor.RatioText = "1,23456789";
        editor.SetUnitPrice("1,25"); editor.CountText = "4";
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
        Task? work = editor.Save(); Check(work is not null, "Culture switch reinterpreted unfinished localized input.");
        var call = await f.Catalog.Next(); Check(call.Edit!.PriceIncreaseRatio == 1.23456789 && call.Edit.PriceSilver == 5,
            "Localized decimal price/ratio parsed with grouping or lost precision.");
        call.Complete(new ModUpsertServerShopListingResponseDto()); await work!;
    }
    private static void StaleMenus(string change)
    {
        using var f = new MultiplayerAdminShopTests.Fixture(); var runtime = new Runtime(); var editor = Create(f, runtime); editor.Select("plain");
        var item = editor.Item!; var stuff = editor.ChooseStuff(); var quality = editor.ChooseQuality(); var kind = editor.ChooseKind(); var mode = editor.ChooseRequirement(true);
        switch (change)
        {
            case "dialog": editor.Close(); break; case "page": f.Model.Close(); break;
            case "permission-return": f.Access.SetAdministrator(false); f.Access.SetAdministrator(true); break;
            case "unavailable": f.Operations.Session.BeginUnavailable(f.Operations.Ports.Auth, null); break;
            default: f.Catalog.Change(change); break;
        }
        int estimates = runtime.Estimates;
        stuff("Steel"); quality("Legendary"); kind("BuyFromPlayer"); mode("AtMost"); editor.SetHitPointsPercent(10); editor.Select("packed");
        Check(!editor.IsCurrent && !editor.CanSave && editor.Save() is null && editor.Item == item && item.StuffDefName is null
            && item.Quality == "Normal" && item.HitPoints == 200 && !editor.IsBuyOrder && editor.QualityMode == "AtLeast"
            && runtime.Estimates == estimates && f.Catalog.Calls == 0, "Old editor/menu crossed " + change);
    }
    private static async Task Busy()
    {
        using var f = new MultiplayerAdminShopTests.Fixture(); var editor = Create(f, new Runtime()); editor.Select("plain");
        Task refresh = f.Catalog.Catalog.RefreshAsync(); var call = await f.Catalog.Next();
        editor.ChooseQuality()("Good"); editor.CountText = "2"; editor.Refresh();
        Check(editor.IsCurrent && !editor.CanSave && editor.Save() is null && editor.Item!.Quality == "Good", "Busy request disabled local editing or allowed submission.");
        call.Complete(new ModListServerShopResponseDto()); await refresh;
        Task save = editor.Save()!; call = await f.Catalog.Next();
        Check(call.Edit!.Item.StackCount == 2 && call.Edit.Item.Quality == "Good", "Explicit retry lost local edits.");
        call.Complete(new ModUpsertServerShopListingResponseDto()); await save;
    }
    private static ClientShopListingEditor Create(MultiplayerAdminShopTests.Fixture f, Runtime runtime) => new(f.Model.PrepareEdit(), runtime);
    private sealed class Runtime : IClientShopListingRuntime
    {
        public IReadOnlyList<ClientShopDefinition> Catalog { get; set; } = new[] { new ClientShopDefinition("plain", "普通"), new ClientShopDefinition("packed", "打包") };
        public object? PresentationOwner { get; set; } = new();
        internal readonly List<string> Surfaces = new();
        internal bool Complete = true;
        internal int Estimates;
        internal float UnitValue = 10f;
        internal IReadOnlyList<string> ExtensionKeys = Array.Empty<string>();
        public ModThingReferenceDto Create(string defName, string surface)
        {
            var item = new ModThingReferenceDto { DefName = defName == "packed" ? "MinifiedThing" : defName, StackCount = 1,
                MinifiedInnerDefName = defName == "packed" ? "building" : null, HitPoints = 200, Quality = "Normal" };
            ApplyDefaults(item, surface); return item;
        }
        public void ApplyDefaults(ModThingReferenceDto item, string surface) => Surfaces.Add(surface);
        public void Normalize(ModThingReferenceDto item, string surface) { }
        public bool IsComplete(ModThingReferenceDto item, string surface) => Complete;
        public ClientShopItemFeatures Features(ModThingReferenceDto item) => new(true, true, true, (item.HitPoints ?? 200) / 2);
        public IReadOnlyList<ClientShopDefinition> Materials(ModThingReferenceDto item) => new[] { new ClientShopDefinition("Steel", "钢铁") };
        public int HitPointsFromPercent(ModThingReferenceDto item, int percent) => percent * 2;
        public IReadOnlyList<string> ValueKeyParts(ModThingReferenceDto item) => ExtensionKeys;
        public ClientShopItemValue Estimate(ModThingReferenceDto item) { Estimates++; return new(UnitValue, "value-" + Estimates); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
