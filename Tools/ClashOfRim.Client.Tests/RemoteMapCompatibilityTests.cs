using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Compatibility;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility.RemoteMaps;
using RimWorld;
using RimWorld.Planet;
using Verse;

internal static class RemoteMapCompatibilityTests
{
    internal static void Run()
    {
        RemoteMapPlanTests.Run();
        RemoteMapDesignationTests.Run();
        RemoteMapLoadedItemTests.Run();
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "ja-JP" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                MapLifecycle();
                DefaultPawnAi();
                ProjectionReferences();
                RemoteFactionProjectionTests.Run();
                LoadIdRegistration();
                SnapshotAndColonyPolicies();
                PublicFacadeLifecycle();
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            ClashOfRimCompatibilityApi.BeginRegistrationCycle("client-tests");
        }
    }

    private static void DefaultPawnAi()
    {
        var f = new Fixture(); var pawn = New<Pawn>(); var other = New<Pawn>();
        Check(f.Lifecycle.CanUseDefaultRemotePawnAi(pawn), "Empty filters must preserve default AI.");
        var token = f.Scope.BeginRegistrationCycle("special-ai"); int calls = 0;
        Func<Pawn, bool> filter = actual => { calls++; return !ReferenceEquals(actual, pawn); };
        f.Lifecycle.RegisterRemotePawnDefaultAiFilter(_ => throw new Exception("filter"));
        f.Lifecycle.RegisterRemotePawnDefaultAiFilter(filter);
        f.Lifecycle.RegisterRemotePawnDefaultAiFilter(filter);
        Check(!f.Lifecycle.CanUseDefaultRemotePawnAi(pawn) && f.Lifecycle.CanUseDefaultRemotePawnAi(other)
            && calls == 2 && f.Scope.Diagnostics.Count == 2, "AI filtering lost pawn identity, deduplication or failure isolation.");
        f.Scope.RevokeRegistrationToken(token);
        Check(f.Lifecycle.CanUseDefaultRemotePawnAi(pawn) && calls == 2, "Revoked AI filter remained active.");
        var owner = ClashOfRimCompatibilityApi.BeginRegistrationCycle("ai-facade");
        ClashOfRimCompatibilityApi.RegisterRemotePawnDefaultAiFilter(_ => false);
        Check(!ClashOfRimCompatibilityApi.CanUseDefaultRemotePawnAi(pawn), "Facade did not delegate the AI filter.");
        ClashOfRimCompatibilityApi.RevokeRegistrationToken(owner);
        Check(ClashOfRimCompatibilityApi.CanUseDefaultRemotePawnAi(pawn), "Facade retained revoked AI filter.");
    }

    private static void MapLifecycle()
    {
        var f = new Fixture(); var map = New<Map>(); var caravan = New<Caravan>(); var pawn = New<Pawn>();
        var faction = New<Faction>(); var parent = New<RemoteSessionMapParent>(); var package = new ModSnapshotPackageMetadataDto();
        foreach (bool succeed in new[] { false, true })
        {
            f.Scope.BeginRegistrationCycle("entry"); int calls = 0;
            var expected = succeed ? RaidCaravanMapEntryResult.Success(new[] { "pawn-1" }) : RaidCaravanMapEntryResult.Failed("blocked");
            f.Lifecycle.RegisterRaidCaravanMapEntryHandler((_, _, _) => throw new Exception("entry"));
            f.Lifecycle.RegisterRaidCaravanMapEntryHandler((actualCaravan, actualMap, pawns) =>
            {
                Check(ReferenceEquals(actualCaravan, caravan) && ReferenceEquals(actualMap, map) && pawns.Count == 0,
                    "Raid entry lost inputs or null-pawn-list normalization.");
                calls++; return RaidCaravanMapEntryResult.NotHandled;
            });
            f.Lifecycle.RegisterRaidCaravanMapEntryHandler((_, _, _) => expected);
            f.Lifecycle.RegisterRaidCaravanMapEntryHandler((_, _, _) => { calls++; return RaidCaravanMapEntryResult.NotHandled; });
            Check(ReferenceEquals(f.Lifecycle.TryEnterRaidCaravanMap(caravan, map, null!), expected)
                && calls == 1 && f.Scope.Diagnostics.Count == 1, "Raid entry did not stop at the first handled success/failure.");
        }
        f.Scope.BeginRegistrationCycle("notifications"); var trace = new List<string>();
        RemoteDefenderMapPreparedHandler later = (_, _) => trace.Add("later");
        f.Lifecycle.RegisterRemoteDefenderMapPreparedHandler((actualMap, actualFaction) =>
        {
            Check(ReferenceEquals(actualMap, map) && ReferenceEquals(actualFaction, faction), "Defender preparation lost its inputs.");
            trace.Add("prepared"); f.Lifecycle.RegisterRemoteDefenderMapPreparedHandler(later);
            throw new Exception("prepared");
        });
        f.Lifecycle.NotifyRemoteDefenderMapPrepared(map, faction);
        Check(trace.SequenceEqual(new[] { "prepared" }), "Reentrant preparation registration altered the active snapshot.");
        trace.Clear(); f.Lifecycle.NotifyRemoteDefenderMapPrepared(map, faction);
        Check(trace.SequenceEqual(new[] { "prepared", "later" }), "Next preparation call did not see the added callback.");
        f.Lifecycle.RegisterRemoteMapLoadedHandler((_, _, _, _) => throw new Exception("loaded"));
        f.Lifecycle.RegisterRemoteMapLoadedHandler((actualMap, actualParent, scope, actualPackage) =>
        {
            Check(ReferenceEquals(actualMap, map) && ReferenceEquals(actualParent, parent) && scope == " raid " && ReferenceEquals(actualPackage, package),
                "Loaded-map callback lost its carrier, scope or package.");
            trace.Add("loaded");
        });
        f.Lifecycle.NotifyRemoteMapLoaded(map, parent, " raid ", package);
        f.Lifecycle.RegisterRemoteSessionPawnCleanupHandler((_, _) => throw new Exception("cleanup"));
        f.Lifecycle.RegisterRemoteSessionPawnCleanupHandler((_, _) => false);
        f.Lifecycle.RegisterRemoteSessionPawnCleanupHandler((actual, reason) => ReferenceEquals(actual, pawn) && reason == "close");
        f.Lifecycle.RegisterRemoteSessionPawnCleanupHandler((_, _) => { trace.Add("unexpected"); return true; });
        Check(f.Lifecycle.TryCleanupRemoteSessionPawn(pawn, "close") && !f.Lifecycle.TryCleanupRemoteSessionPawn(null!, "close")
            && trace.SequenceEqual(new[] { "prepared", "later", "loaded" }) && f.Scope.Diagnostics.Count == 4,
            "Map notification/cleanup failure isolation or short-circuiting changed.");
    }

    private static void ProjectionReferences()
    {
        var f = new Fixture(); var owner = f.Scope.BeginRegistrationCycle("low");
        var package = new ModSnapshotPackageMetadataDto(); var source = XDocument.Parse("<save />"); var map = new XElement("map");
        var low = new Reference("ID"); var high = new Reference("ID"); var distinct = new Reference("id");
        f.Projection.RegisterRemoteMapProjectionReferenceRewriter((_, _, _) => new ILoadReferenceable[] { low, distinct, null! });
        var priority = f.Scope.CreateRegistrationToken("high", 10);
        f.Scope.UseRegistrationToken(priority, () => f.Projection.RegisterRemoteMapProjectionReferenceRewriter((actual, document, element) =>
        {
            Check(ReferenceEquals(actual, package) && ReferenceEquals(document, source) && ReferenceEquals(element, map), "Projection rewriter lost inputs.");
            return new ILoadReferenceable[] { high };
        }));
        f.Projection.RegisterRemoteMapProjectionReferenceRewriter((_, _, _) => throw new Exception("rewrite"));
        f.Projection.RegisterRemoteMapProjectionReferenceRewriter((_, _, _) => null!);
        Check(f.Projection.RewriteRemoteMapProjectionReferences(package, source, map).SequenceEqual(new[] { high, distinct }),
            "Projection lost priority order, first-wins ID deduplication, ordinal identity or null filtering.");
        Check(f.Projection.RewriteRemoteMapProjectionReferences(null!, source, map).Count == 0, "Invalid projection input reached callbacks.");
        var pawns = new XElement("pawns");
        f.Projection.RegisterRemoteMapProjectionSanitizer((_, _, _) => throw new Exception("sanitize"));
        f.Projection.RegisterRemoteMapProjectionSanitizer((actual, actualMap, actualPawns) =>
        {
            Check(ReferenceEquals(actual, package) && ReferenceEquals(actualMap, map) && ReferenceEquals(actualPawns, pawns), "Sanitizer lost inputs.");
            actualMap.SetAttributeValue("sanitized", true);
        });
        f.Projection.SanitizeRemoteMapProjection(package, map, pawns);
        Check((bool?)map.Attribute("sanitized") == true && f.Scope.Diagnostics.Count == 2, "Projection sanitizer failure blocked later work.");
        f.Scope.RevokeRegistrationToken(owner);
        Check(f.Projection.RewriteRemoteMapProjectionReferences(package, source, map).Single() == high, "Owner revocation removed another owner's reference rewriter.");
    }

    private static void LoadIdRegistration()
    {
        var f = new Fixture(); var low = f.Scope.BeginRegistrationCycle("low");
        f.Projection.RegisterRemoteMapIntegerLoadIdRewriter(" ID_ ", Matches, NextId);
        f.Projection.RegisterRemoteMapIntegerLoadIdRewriter("ID_", new Func<XElement, bool>(Matches), new Func<int>(NextId));
        Check(f.Projection.GetRemoteMapIntegerLoadIdRewriters().Count == 1, "Equivalent integer ID callbacks were not deduplicated.");
        var high = f.Scope.CreateRegistrationToken("high", 10);
        f.Scope.UseRegistrationToken(high, () => f.Projection.RegisterRemoteMapIntegerLoadIdRewriter("ID_", _ => true, () => 9));
        f.Projection.RegisterRemoteMapIntegerLoadIdRewriter("id_", _ => false, () => 2);
        var snapshot = f.Projection.GetRemoteMapIntegerLoadIdRewriters();
        Check(snapshot.Select(x => x.NextId()).SequenceEqual(new[] { 9, 7, 2 }) && snapshot[1].LoadIdPrefix == "ID_"
            && snapshot[1].Predicate(new XElement("match")) && f.Scope.Diagnostics.Single().Code == "RemoteMapLoadIdPrefixShared",
            "Load ID prefix warnings, ordinal keys or priority order changed.");
        ((IList<RemoteMapIntegerLoadIdRewriterRegistration>)snapshot).Clear();
        Check(f.Projection.GetRemoteMapIntegerLoadIdRewriters().Count == 3, "A returned registration snapshot exposed mutable owner storage.");
        f.Scope.RevokeRegistrationToken(low);
        Check(f.Projection.GetRemoteMapIntegerLoadIdRewriters().Single().NextId() == 9, "Load ID owner revocation removed another owner's handler.");
        f.Projection.RegisterRemoteMapAreaLoadIdProvider((_, _) => throw new Exception("area"));
        f.Projection.RegisterRemoteMapAreaLoadIdProvider((_, _) => " ");
        f.Projection.RegisterRemoteMapAreaLoadIdProvider((area, id) => " " + area.Name + ":" + id.ToString(CultureInfo.InvariantCulture) + " ");
        Check(f.Projection.BuildRemoteMapAreaLoadId(new XElement("area"), 4) == " area:4 ", "Area ID fallback trimmed or changed the provided ID.");
        f.Scope.BeginRegistrationCycle("next");
        Check(f.Projection.GetRemoteMapIntegerLoadIdRewriters().Count == 0 && f.Projection.BuildRemoteMapAreaLoadId(new XElement("area"), 4) is null,
            "New cycle retained load ID capabilities.");
    }

    private static void SnapshotAndColonyPolicies()
    {
        var f = new Fixture(); var document = XDocument.Parse("<save />");
        Check(!f.Snapshots.HasSnapshotSaveSanitizers && !f.Colonies.IsPlayerColonySiteRegistrationSuppressedByCompatibility(), "Empty capabilities invented work.");
        f.Snapshots.RegisterSnapshotSaveSanitizer(_ => -10); // Untracked registration must also clear next cycle.
        f.Snapshots.RegisterSnapshotSaveSanitizer(_ => throw new Exception("save"));
        f.Snapshots.RegisterSnapshotSaveSanitizer(actual => { actual.Root!.SetAttributeValue("clean", true); return 3; });
        f.Snapshots.RegisterSnapshotSaveSanitizer(_ => 2);
        f.Colonies.RegisterPlayerColonySiteRegistrationSuppressor(() => throw new Exception("colony"));
        f.Colonies.RegisterPlayerColonySiteRegistrationSuppressor(() => false);
        f.Colonies.RegisterPlayerColonySiteRegistrationSuppressor(() => true);
        int extra = 0; f.Colonies.RegisterPlayerColonySiteRegistrationSuppressor(() => { extra++; return false; });
        Check(f.Snapshots.HasSnapshotSaveSanitizers && f.Snapshots.SanitizeSnapshotSave(document) == 5 && (bool?)document.Root!.Attribute("clean") == true
            && f.Colonies.IsPlayerColonySiteRegistrationSuppressedByCompatibility() && extra == 0 && f.Scope.Diagnostics.Count == 2,
            "Snapshot change counting or colony suppression failure/fallback policy changed.");
        var independent = new Fixture();
        Check(!independent.Snapshots.HasSnapshotSaveSanitizers && !independent.Colonies.IsPlayerColonySiteRegistrationSuppressedByCompatibility(), "Independent capabilities inherited registrations.");
        f.Scope.BeginRegistrationCycle("next");
        Check(!f.Snapshots.HasSnapshotSaveSanitizers && f.Snapshots.SanitizeSnapshotSave(document) == 0
            && !f.Colonies.IsPlayerColonySiteRegistrationSuppressedByCompatibility(), "New cycle retained untracked snapshot/colony registrations.");
    }

    private static void PublicFacadeLifecycle()
    {
        for (int cycle = 0; cycle < 2; cycle++)
        {
            var token = ClashOfRimCompatibilityApi.BeginRegistrationCycle("maps");
            var map = New<Map>(); var pawn = New<Pawn>(); var package = new ModSnapshotPackageMetadataDto();
            var source = XDocument.Parse("<save />"); var element = new XElement("map"); int calls = 0;
            ClashOfRimCompatibilityApi.RegisterRaidCaravanMapEntryHandler((_, _, _) => { calls++; return RaidCaravanMapEntryResult.Success(new[] { "pawn" }); });
            ClashOfRimCompatibilityApi.RegisterRemoteDefenderMapPreparedHandler((_, _) => calls++);
            ClashOfRimCompatibilityApi.RegisterRemoteMapLoadedHandler((_, _, _, _) => calls++);
            ClashOfRimCompatibilityApi.RegisterRemoteSessionPawnCleanupHandler((_, _) => { calls++; return true; });
            ClashOfRimCompatibilityApi.RegisterRemoteMapProjectionReferenceRewriter((_, _, _) => { calls++; return new[] { new Reference("id") }; });
            ClashOfRimCompatibilityApi.RegisterRemoteMapProjectionSanitizer((_, _, _) => calls++);
            ClashOfRimCompatibilityApi.RegisterRemoteMapIntegerLoadIdRewriter("ID_", Matches, NextId);
            ClashOfRimCompatibilityApi.RegisterRemoteMapAreaLoadIdProvider((_, _) => { calls++; return "area"; });
            ClashOfRimCompatibilityApi.RegisterSnapshotSaveSanitizer(_ => { calls++; return 1; });
            ClashOfRimCompatibilityApi.RegisterPlayerColonySiteRegistrationSuppressor(() => { calls++; return true; });
            void Notify()
            {
                ClashOfRimCompatibilityApi.NotifyRemoteDefenderMapPrepared(map, New<Faction>());
                ClashOfRimCompatibilityApi.NotifyRemoteMapLoaded(map, New<RemoteSessionMapParent>(), "raid", package);
                ClashOfRimCompatibilityApi.SanitizeRemoteMapProjection(package, element, new XElement("pawns"));
            }
            Notify();
            Check(ClashOfRimCompatibilityApi.TryEnterRaidCaravanMap(New<Caravan>(), map, new[] { pawn }).Kind == RaidCaravanMapEntryResultKind.Success
                && ClashOfRimCompatibilityApi.TryCleanupRemoteSessionPawn(pawn, "close")
                && ClashOfRimCompatibilityApi.RewriteRemoteMapProjectionReferences(package, source, element).Single().GetUniqueLoadID() == "id"
                && ClashOfRimCompatibilityApi.GetRemoteMapIntegerLoadIdRewriters().Single().NextId() == 7
                && ClashOfRimCompatibilityApi.BuildRemoteMapAreaLoadId(element, 1) == "area"
                && ClashOfRimCompatibilityApi.HasSnapshotSaveSanitizers && ClashOfRimCompatibilityApi.SanitizeSnapshotSave(source) == 1
                && ClashOfRimCompatibilityApi.IsPlayerColonySiteRegistrationSuppressedByCompatibility() && calls == 9,
                "Public remote-map API failed to reach all 10 capability collections.");
            if (cycle == 0) Check(ClashOfRimCompatibilityApi.RevokeRegistrationToken(token), "Map token was not revoked.");
            else ClashOfRimCompatibilityApi.BeginRegistrationCycle("next");
            Notify();
            Check(ClashOfRimCompatibilityApi.TryEnterRaidCaravanMap(New<Caravan>(), map, new[] { pawn }).Kind == RaidCaravanMapEntryResultKind.NotHandled
                && !ClashOfRimCompatibilityApi.TryCleanupRemoteSessionPawn(pawn, "close")
                && ClashOfRimCompatibilityApi.RewriteRemoteMapProjectionReferences(package, source, element).Count == 0
                && ClashOfRimCompatibilityApi.GetRemoteMapIntegerLoadIdRewriters().Count == 0
                && ClashOfRimCompatibilityApi.BuildRemoteMapAreaLoadId(element, 1) is null
                && !ClashOfRimCompatibilityApi.HasSnapshotSaveSanitizers && ClashOfRimCompatibilityApi.SanitizeSnapshotSave(source) == 0
                && !ClashOfRimCompatibilityApi.IsPlayerColonySiteRegistrationSuppressedByCompatibility() && calls == 9,
                "Revocation/new cycle retained map, projection, snapshot or colony callbacks.");
        }
    }

    private sealed class Fixture
    {
        internal readonly CompatibilityRegistrationScope Scope = new(_ => { });
        internal readonly RemoteMapLifecycleCompatibility Lifecycle;
        internal readonly RemoteMapProjectionCompatibility Projection;
        internal readonly SnapshotSanitizationCompatibility Snapshots;
        internal readonly ColonySiteCompatibility Colonies;
        internal Fixture() { Lifecycle = new(Scope); Projection = new(Scope); Snapshots = new(Scope); Colonies = new(Scope); }
    }
    private sealed class Reference : ILoadReferenceable
    {
        private readonly string id;
        internal Reference(string id) => this.id = id;
        public string GetUniqueLoadID() => id;
    }
    private static bool Matches(XElement element) => element.Name == "match";
    private static int NextId() => 7;
    private static T New<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
