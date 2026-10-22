using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Administration;
using AIRsLight.ClashOfRim.Modules.Multiplayer;
using AIRsLight.ClashOfRim.Modules.Shop;
using AIRsLight.ClashOfRim.Modules.Transport;

internal static class MultiplayerAdminShopTests
{
    internal static void Run() => RunAsync().GetAwaiter().GetResult();
    private static async Task RunAsync()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string name in new[] { "en-US", "zh-CN", "tr-TR", "fr-FR", "ar-SA" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
                await Drafts(); await SaveRow();
                foreach (string kind in new[] { "SellToPlayer", "BuyFromPlayer" }) await RowUnitPrice(kind);
            }
            foreach (string change in new[] { "close", "user", "colony", "auth", "server", "game", "login", "reset", "generation",
                "permission", "permission-return", "unavailable", "changed", "removed" }) await Stale(change);
            await ProductRevisions(); await EditorCopies(); await Busy(); await Remove(); await LargeCatalog();
            foreach (string fault in new[] { "network", "rejected", "exception" }) await Failure(fault);
        }
        finally { CultureInfo.CurrentCulture = culture; ClientSessionWork.Reset(); }
    }

    private static async Task Drafts()
    {
        using var f = new Fixture(); await f.Catalog.Load(Listing("I"), Listing("ı"));
        var rows = f.Model.Read(); var row = rows[0]; row.StockText = "-"; row.UnitPriceText = "";
        Check(ReferenceEquals(rows, f.Model.Read()) && row.Listing == f.Shop.Read().All[0], "Admin page rebuilt/copied the shared projection per frame.");
        Check(rows[1].StockText == "3", "Culture-sensitive ID matching mixed distinct drafts.");
        await f.Catalog.Load(Listing("ı"), Listing("I"));
        Check(ReferenceEquals(f.Model.Read()[1], row) && row.StockText == "-" && row.UnitPriceText == "", "Identical refresh discarded incomplete edits or server ordering.");
        row.StockText = "0"; row.UnitPriceText = "1";
        Check(f.Model.CanSave(row), "Valid zero-stock/base-price draft was rejected.");
        foreach (string invalid in new[] { "", "-", "2147483648", "1.5", "1,000", "-1" })
        {
            row.StockText = invalid; Check(!f.Model.CanSave(row), "Invalid stock passed: " + invalid);
            int calls = f.Catalog.Calls; await f.Model.SaveAsync(row); Check(calls == f.Catalog.Calls, "Invalid draft reached transport.");
            row.StockText = "3";
        }
        foreach (string invalid in new[] { "", "-", "2147483648", "-1", "NaN", "Infinity", "1e999" })
        { row.UnitPriceText = invalid; Check(!f.Model.CanSave(row), "Invalid price passed: " + invalid); row.UnitPriceText = "1"; }
        row.UnitPriceText = "0"; Check(!f.Model.CanSave(row), "Zero price passed.");
        var price = Listing("I"); price.BasePriceSilver = 17; f.Catalog.State.Upsert(price);
        var updated = f.Model.Read().Single(r => r.Listing.ListingId == "I");
        Check(!ReferenceEquals(updated, row) && updated.UnitPriceText == "8.5" && updated.LotPrice == 17, "Changed product retained an obsolete price draft.");
        price.Item = null; f.Catalog.State.Upsert(price);
        Check(!f.Model.CanSave(f.Model.Read().Single(r => r.Listing.ListingId == "I")), "Missing item allowed row save.");
    }

    private static async Task ProductRevisions()
    {
        using var f = new Fixture(); await f.Catalog.Load(Listing("a"));
        Action<ModServerShopListingDto>[] changes =
        {
            x => x.StockCount++, x => x.PriceSilver++, x => x.BasePriceSilver++, x => x.PriceIncreaseRatio += 0.5,
            x => x.ListingKind = "SellToPlayer", x => x.QualityRequirementMode = "AtLeast", x => x.HitPointsRequirementMode = "AtMost",
            x => x.Item!.StackCount++, x => x.Item!.Metadata["label"] = "changed",
            x => x.Item!.ThingPackage!.Scribe.XmlGzipBase64 = "changed", x => x.Item!.PawnPackageId = "changed"
        };
        foreach (var change in changes)
        {
            var row = f.Model.Read()[0]; row.StockText = "99"; var remove = f.Model.PrepareRemove(row); var editor = f.Model.PrepareEdit(row);
            var product = f.Catalog.State.CopyListings()[0]; change(product); f.Catalog.State.Upsert(product);
            Check(!ReferenceEquals(f.Model.Read()[0], row) && !f.Model.CanSave(row) && !editor.CanSubmit, "Product revision revived an old row/editor.");
            int calls = f.Catalog.Calls; await remove(); Check(calls == f.Catalog.Calls, "Delete ignored product revision.");
            f.Catalog.State.Replace(new[] { Listing("a") });
        }
        var original = f.Model.Read()[0]; original.UnitPriceText = "unfinished";
        var preview = f.Catalog.State.CopyListings()[0]; preview.Item!.PawnPackage!.PendingPreviewPng = new byte[] { 9 };
        f.Catalog.State.Upsert(preview);
        Check(ReferenceEquals(original, f.Model.Read()[0]) && original.UnitPriceText == "unfinished"
            && original.Listing.Item!.PawnPackage!.PendingPreviewPng![0] == 9, "Non-wire preview hydration discarded edits or kept an old preview.");
        var standalone = f.Model.PrepareExistingEdit(Listing("missing"));
        Check(standalone.Listing?.ListingId == "missing" && !standalone.CanSubmit && standalone.Submit(Command()) is null,
            "Missing standalone edit silently became a new listing.");
        var stale = Listing("a"); stale.StockCount = 999;
        Check(!f.Model.PrepareExistingEdit(stale).CanSubmit, "Standalone editor silently adopted a changed listing.");
    }

    private static async Task Stale(string change)
    {
        using var f = new Fixture(); await f.Catalog.Load(Listing("a"));
        var row = f.Model.Read()[0]; row.StockText = "99";
        var remove = f.Model.PrepareRemove(row); var editor = f.Model.PrepareEdit(row); var create = f.Model.PrepareEdit();
        int calls = f.Catalog.Calls;
        switch (change)
        {
            case "close": f.Model.Close(); break;
            case "permission": f.Access.SetAdministrator(false); break;
            case "permission-return": f.Access.SetAdministrator(false); f.Access.SetAdministrator(true); break;
            case "unavailable": f.Operations.Session.BeginUnavailable(f.Operations.Ports.Auth, null); break;
            case "changed": var changed = Listing("a"); changed.StockCount++; f.Catalog.State.Upsert(changed); break;
            case "removed": f.Catalog.State.Remove("a"); f.Model.Read(); f.Catalog.State.Upsert(Listing("a")); break;
            default: f.Catalog.Change(change); break;
        }
        Check(!f.Model.CanSave(row) && !editor.CanSubmit && editor.Submit(Command()) is null, "Old row/editor crossed " + change);
        if (change is not "changed" and not "removed")
            Check(!create.CanSubmit && create.Submit(Command()) is null, "Old create dialog crossed " + change);
        await f.Model.SaveAsync(row).WaitAsync(TimeSpan.FromSeconds(5)); await remove().WaitAsync(TimeSpan.FromSeconds(5));
        Check(f.Catalog.Calls == calls && !f.Catalog.State.Busy, "Stale action dispatched/occupied shop after " + change);
    }

    private static async Task SaveRow()
    {
        using var f = new Fixture(); await f.Catalog.Load(Listing("a"));
        var row = f.Model.Read()[0]; row.UnitPriceText = "23"; row.StockText = "0";
        f.Operations.Ports.Snapshot = "advanced"; f.Access.SetAdministrator(true);
        Task work = f.Model.SaveAsync(row); var call = await f.Catalog.Next();
        Check(call.Kind == "upsert" && call.Context.CurrentSnapshotId == "advanced" && call.Edit is
            { ListingId: "a", StockCount: 0, PriceSilver: 46, ListingKind: "BuyFromPlayer", PriceIncreaseRatio: 1.25,
                QualityRequirementMode: "AtMost", HitPointsRequirementMode: "AtLeast" }, "Row save lost current context or listing terms.");
        string frozen = ClientProtocolJson.Serialize(call.Edit!.Item);
        row.StockText = "999"; row.Listing.Item!.Metadata["label"] = "mutated"; row.Listing.Item.PawnPackage!.PendingPreviewPng![0] = 9;
        Check(ClientProtocolJson.Serialize(call.Edit.Item) == frozen && call.Edit.Item.PawnPackage!.PendingPreviewPng![0] == 1, "In-flight row command shares editable data.");
        f.Model.Close(); var accepted = Listing("a"); accepted.BasePriceSilver = 46; accepted.StockCount = 0;
        call.Complete(new ModUpsertServerShopListingResponseDto { Listing = accepted }); await work;
        Check(!f.Catalog.State.Busy && f.Model.Read()[0].UnitPriceText == "23" && f.Shop.Read().All[0].StockCount == 0,
            "Closing the page cancelled an admitted save or failed to update the shared shop.");
    }

    private static async Task EditorCopies()
    {
        using var f = new Fixture(); await f.Catalog.Load(Listing("a"));
        var row = f.Model.Read()[0]; var editor = f.Model.PrepareEdit(row); var copy = editor.Listing!;
        Check(ClientProtocolJson.Serialize(copy) == ClientProtocolJson.Serialize(row.Listing), "Editor copy omitted a wire field.");
        copy.ListingId = "other"; copy.Item!.ThingPackage!.Scribe.XmlGzipBase64 = "edited"; copy.Item.PawnPackage!.PendingPreviewPng![0] = 4;
        copy.Item.Metadata["label"] = "edited";
        Check(row.Listing.Item!.ThingPackage!.Scribe.XmlGzipBase64 == "payload" && row.Listing.Item.PawnPackage!.PendingPreviewPng![0] == 1
            && row.Listing.Item.Metadata["label"] == "中文 à", "Editor package/preview/metadata aliases the shared projection.");
        Task work = editor.Submit(Command(copy.Item))!; Check(work is not null && !editor.CanSubmit && editor.Submit(Command()) is null, "Editor did not consume exactly one submission.");
        var call = await f.Catalog.Next(); copy.Item.ThingPackage.Scribe.XmlGzipBase64 = "later"; copy.Item.PawnPackage.PendingPreviewPng[0] = 8;
        Check(call.Edit!.ListingId == "a" && call.Edit.Item.ThingPackage!.Scribe.XmlGzipBase64 == "edited"
            && call.Edit.Item.PawnPackage!.PendingPreviewPng![0] == 4, "Editor changed its target or in-flight item.");
        call.Complete(new ModUpsertServerShopListingResponseDto()); await work!;
        var closed = f.Model.PrepareEdit(); closed.Close(); Check(!closed.CanSubmit && closed.Submit(Command()) is null, "Closed dialog submitted.");
        var create = f.Model.PrepareEdit(); work = create.Submit(Command())!; call = await f.Catalog.Next();
        Check(call.Edit!.ListingId is null, "New editor adopted a supplied product ID."); call.Complete(new ModUpsertServerShopListingResponseDto()); await work;
    }
    private static async Task RowUnitPrice(string kind)
    {
        using var f = new Fixture(); var listing = Listing("priced"); listing.ListingKind = kind;
        listing.BasePriceSilver = 19; listing.Item!.StackCount = 3; await f.Catalog.Load(listing);
        var row = f.Model.Read()[0];
        Check(row.LotPrice == 19 && row.TryBuild(out var unchanged) && unchanged!.PriceSilver == 19,
            "Opening a quick-edit row changed the existing lot price.");
        row.UnitPriceText = 1.25m.ToString(CultureInfo.CurrentCulture);
        Task work = f.Model.SaveAsync(row); var call = await f.Catalog.Next();
        Check(row.LotPrice == 4 && call.Edit is { PriceSilver: 4, PriceIncreaseRatio: 1.25, Item.StackCount: 3 }
            && call.Edit.ListingKind == kind, "Quick edit failed to convert a localized fractional unit price to the lot amount.");
        call.Complete(new ModUpsertServerShopListingResponseDto()); await work;
    }

    private static async Task Busy()
    {
        using var f = new Fixture(); await f.Catalog.Load(Listing("a"));
        var row = f.Model.Read()[0]; row.UnitPriceText = "19"; var editor = f.Model.PrepareEdit(row); var remove = f.Model.PrepareRemove(row);
        Task refresh = f.Model.RefreshAsync(1); var call = await f.Catalog.Next(); int calls = f.Catalog.Calls;
        await f.Model.SaveAsync(row); await remove(); await f.Model.RefreshAsync(2);
        Check(!f.Model.CanSave(row) && !f.Model.CanRefresh && !editor.CanSubmit && editor.Submit(Command()) is null
            && f.Catalog.Calls == calls, "Busy catalog allowed another write or refresh.");
        call.Complete(new ModListServerShopResponseDto { Listings = new() { Listing("a") } }); await refresh;
        Check(f.Model.CanSave(row) && row.UnitPriceText == "19" && editor.CanSubmit, "Temporary busy state consumed drafts or delayed intents.");
        var ticket = f.Access.Begin(out _)!;
        Check(ticket is not null && f.Model.CanSave(row), "Shop writes incorrectly share administration/world-write admission.");
        Task save = editor.Submit(Command())!; call = await f.Catalog.Next(); call.Complete(new ModUpsertServerShopListingResponseDto()); await save;
        f.Access.End(ticket!);
    }

    private static async Task Remove()
    {
        using var f = new Fixture(); await f.Catalog.Load(Listing("a"), Listing("b"));
        var remove = f.Model.PrepareRemove(f.Model.Read()[0]); f.Operations.Ports.Snapshot = "advanced";
        Task work = remove(); var call = await f.Catalog.Next();
        Check(call.Kind == "remove" && call.Id == "a" && call.Context.CurrentSnapshotId == "advanced", "Removal lost target/current snapshot.");
        call.Complete(new ModRemoveServerShopListingResponseDto()); await work;
        Check(f.Model.Read().Single().Listing.ListingId == "b" && f.Shop.Read().All.Count == 1, "Removal left a stale draft or player view.");
        int calls = f.Catalog.Calls; await remove(); Check(calls == f.Catalog.Calls, "Completed delete submitted again after removal.");
    }

    private static async Task Failure(string fault)
    {
        using var f = new Fixture(); await f.Catalog.Load(Listing("a"));
        var row = f.Model.Read()[0]; row.StockText = "12";
        Task work = f.Model.SaveAsync(row); var call = await f.Catalog.Next();
        if (fault == "network") call.Complete(new ClientShopCatalogTests.Failure("network", "failed"));
        else if (fault == "exception") call.Completion.SetException(new InvalidOperationException("fault"));
        else call.Complete(new ModUpsertServerShopListingResponseDto { Result = new() { Accepted = false } });
        await work;
        Check(!f.Catalog.State.Busy && ReferenceEquals(f.Model.Read()[0], row) && row.StockText == "12", "Failed save discarded the retry draft: " + fault);
        work = f.Model.SaveAsync(row); call = await f.Catalog.Next();
        Check(call.Edit!.StockCount == 12, "Retry lost edited stock."); call.Complete(new ModUpsertServerShopListingResponseDto()); await work;
    }

    private static async Task LargeCatalog()
    {
        using var f = new Fixture(); await f.Catalog.Load(Enumerable.Range(0, 4000).Select(i => Listing(i.ToString(CultureInfo.InvariantCulture))).ToArray());
        var rows = f.Model.Read(); Check(rows.Count == 4000, "Large admin catalog lost entries."); rows[3999].UnitPriceText = "37";
        for (int i = 0; i < 20; i++) Check(ReferenceEquals(rows, f.Model.Read()), "Large catalog reconciled every frame.");
        var changed = Listing("0"); changed.StockCount = 10; f.Catalog.State.Upsert(changed);
        var next = f.Model.Read();
        Check(!ReferenceEquals(rows[0], next[0]) && ReferenceEquals(rows[3999], next[3999]) && next[3999].UnitPriceText == "37"
            && ReferenceEquals(next[3999].Listing, f.Shop.Read().All[3999]), "One listing update recreated unrelated drafts or duplicated the catalog.");
        f.Catalog.State.Replace(Array.Empty<ModServerShopListingDto>()); Check(f.Model.Read().Count == 0, "Empty refresh kept removed drafts.");
    }

    private static ModServerShopListingDto Listing(string id) => new()
    {
        ListingId = id, ListingKind = "BuyFromPlayer", StockCount = 3, PriceSilver = 10, PriceIncreaseRatio = 1.25,
        QualityRequirementMode = "AtMost", HitPointsRequirementMode = "AtLeast",
        Item = new() { DefName = "Steel", StackCount = 2, Metadata = new() { ["label"] = "中文 à" },
            PawnPackage = new() { PendingPreviewPng = new byte[] { 1, 2 } }, ThingPackage = new() { Scribe = new() { XmlGzipBase64 = "payload" } } }
    };
    private static ClientShopListingEdit Command(ModThingReferenceDto? item = null) => new("supplied-id", "SellToPlayer", item ?? Listing("a").Item!, 12, 3);
    internal sealed class Fixture : IDisposable
    {
        internal readonly ClientShopOperationsTests.Fixture Operations = new(false);
        internal readonly ClientShopCatalogTests.Fixture Catalog;
        internal readonly ClientAdminAccess Access;
        internal readonly ClientShopPanel Shop;
        internal readonly ClientAdminShopPanel Model;
        internal Fixture()
        {
            Catalog = new(Operations.Ports, Operations.Session, Operations.State, Operations.Mutations);
            Access = new(Operations.Session); Access.SetAdministrator(true);
            Shop = new(Operations.Session, Operations.Ports, Operations.State, Catalog.Catalog, Operations.Operations, Operations.Sync);
            Model = new(Operations.Session, Access, Operations.State, Catalog.Catalog, Shop);
        }
        public void Dispose() { Catalog.Dispose(); Operations.Dispose(); }
    }
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
