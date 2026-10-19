using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Compatibility;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility.Pawns;
using RimWorld;
using Verse;

internal static class PawnCompatibilityTests
{
    internal static void Run()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "ja-JP" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                OwnershipAndLifecycle();
                ReferenceKeysAndReplacement();
                FactionNotificationOrder();
                ExchangeSnapshotsAndFailures();
                PackageRegistrationFailures();
                TradeFallbackAndEffects();
                PublicFacade();
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            ClashOfRimCompatibilityApi.BeginRegistrationCycle("client-tests");
        }
    }

    private static void OwnershipAndLifecycle()
    {
        var f = new Fixture(); Pawn pawn = New<Pawn>(); Faction faction = New<Faction>();
        var metadata = new Dictionary<string, string?>(); var package = new ModPawnExchangePackageDto();
        int calls = 0;
        void Invoke(bool populated)
        {
            f.Factions.NotifyPlayerProxyFactionPrepared(faction, "owner");
            f.References.CollectPawnReferenceMetadata(pawn, metadata, "user", "colony");
            Check(f.References.ResolvePawnReferenceMetadata(pawn, "key", "user", "colony") == (populated ? "value" : null), "Resolver lifecycle changed.");
            Check(f.References.RestorePawnReferenceMetadata(pawn, "key", "value", "label") == (populated ? "restored" : ""), "Restorer lifecycle changed.");
            f.Exchange.AppendPawnExchangeExtensions(pawn, package); f.Exchange.NormalizePawnExchangePackage(package);
            Check(f.Exchange.TryRegisterPawnExchangePackageExtensions(package, out string message) && message == (populated ? "accepted" : ""), "Registrar lifecycle changed.");
            Check(f.Exchange.TryRestorePawnPreview(pawn, package, out message) && message == (populated ? "preview" : ""), "Preview callback lifecycle changed.");
            Check(f.Exchange.IsPawnExchangeLocalSaveOnlyLoadId("load") == populated, "Local-save predicate lifecycle changed.");
            f.Exchange.LocalizeRestoredPawnWithCompatibility(pawn); f.Trade.ApplyPawnSoldEffects(pawn, null);
            Check(f.Trade.IsTradeablePawnByCompatibility(pawn) == populated && f.Trade.IsTradePawnRestoreAllowedByCompatibility(pawn) == populated,
                "Trade predicate lifecycle changed.");
        }
        for (int cycle = 0; cycle < 2; cycle++)
        {
            calls = 0; var owner = f.Scope.BeginRegistrationCycle("owner");
            f.Factions.RegisterFactionPreparedHandler((_, _, _) => calls++);
            f.Factions.RegisterPlayerProxyFactionPreparedHandler((_, _) => calls++);
            f.References.RegisterPawnReferenceMetadataCollector((_, _, _, _) => calls++);
            f.References.RegisterPawnReferenceMetadataResolver("key", (_, _, _, _) => { calls++; return "value"; });
            f.References.RegisterPawnReferenceMetadataRestorer("key", (_, _, _, _) => { calls++; return "restored"; });
            f.Exchange.RegisterPawnExchangeExtensionAppender((_, _) => calls++);
            f.Exchange.RegisterPawnExchangePackageNormalizer(_ => calls++);
            f.Exchange.RegisterPawnExchangePackageRegistrar((ModPawnExchangePackageDto _, out string message) => { calls++; message = "accepted"; return true; });
            f.Exchange.RegisterPawnPreviewRestorer((Pawn _, ModPawnExchangePackageDto _, out string message) => { calls++; message = "preview"; return true; });
            f.Exchange.RegisterPawnExchangeLocalSaveOnlyLoadIdPredicate(_ => { calls++; return true; });
            f.Exchange.RegisterPawnPostRestoreLocalizer(_ => calls++);
            f.Trade.RegisterPawnSoldEffectHandler((_, _) => calls++);
            f.Trade.RegisterTradeablePawnPredicate(_ => { calls++; return true; });
            f.Trade.RegisterTradePawnRestoreValidator(_ => { calls++; return true; });
            Invoke(true);
            Check(calls == 14 && f.Scope.Records.Count == 14, "Capabilities did not use all 14 collections in the shared scope.");
            var independent = new Fixture();
            Check(independent.References.ResolvePawnReferenceMetadata(pawn, "key", null, null) is null
                && !independent.Trade.IsTradeablePawnByCompatibility(pawn), "Independent capability instances leaked global state.");
            if (cycle == 0) Check(f.Scope.RevokeRegistrationToken(owner), "Owner revocation did not find capability registrations.");
            else f.Scope.BeginRegistrationCycle("next");
            Invoke(false);
            Check(calls == 14 && f.Scope.Records.Count == 0, "Revocation/new cycle retained callbacks.");
        }
    }

    private static void ReferenceKeysAndReplacement()
    {
        var f = new Fixture(); Pawn pawn = New<Pawn>(); var low = f.Scope.BeginRegistrationCycle("low");
        f.References.RegisterPawnReferenceMetadataResolver(" ID ", (_, _, _, _) => "old");
        f.References.RegisterPawnReferenceMetadataRestorer(" ID ", (_, _, _, _) => "old");
        var high = f.Scope.CreateRegistrationToken("high", 10);
        f.Scope.UseRegistrationToken(high, () =>
        {
            f.References.RegisterPawnReferenceMetadataResolver("ID", (actual, key, user, colony) =>
                ReferenceEquals(actual, pawn) && key == "ID" ? user + "/" + colony : throw new Exception("resolver inputs"));
            f.References.RegisterPawnReferenceMetadataRestorer("ID", (actual, key, value, label) =>
                ReferenceEquals(actual, pawn) && key == "ID" ? label + ":" + value : throw new Exception("restorer inputs"));
            f.References.RegisterPawnReferenceMetadataResolver("ID", (_, _, _, _) => "equal priority");
            f.References.RegisterPawnReferenceMetadataRestorer("ID", (_, _, _, _) => "equal priority");
        });
        f.Scope.RevokeRegistrationToken(low);
        Check(f.References.ResolvePawnReferenceMetadata(pawn, "ID", "user", "colony") == "user/colony"
            && f.References.ResolvePawnReferenceMetadata(pawn, "id", null, null) is null
            && f.References.ResolvePawnReferenceMetadata(pawn, " ID ", null, null) is null
            && f.References.RestorePawnReferenceMetadata(pawn, "ID", "value", "label") == "label:value",
            "Key trimming/ordinal lookup, priority replacement or old-owner revocation changed.");
        Check(f.Scope.Diagnostics.Count == 4 && f.Scope.Records.Count == 2, "Resolver/restorer duplicate registration bookkeeping diverged.");
        f.References.RegisterPawnReferenceMetadataRestorer("other", (_, _, _, _) => "/other");
        f.References.RegisterPawnReferenceMetadataRestorer("blank", (_, _, _, _) => " ");
        f.References.RegisterPawnReferenceMetadataRestorer("broken", (_, _, _, _) => throw new Exception("restore"));
        var metadata = new Dictionary<string, string?> { ["ID"] = "value", ["blank"] = null, ["broken"] = "bad", ["other"] = null };
        Check(f.References.RestorePawnReferenceMetadata(pawn, metadata, "label") == "label:value/other",
            "Bulk restoration lost input order or stopped after an empty/failed entry.");
        f.References.RegisterPawnReferenceMetadataCollector((_, _, _, _) => throw new Exception("collect"));
        f.References.RegisterPawnReferenceMetadataCollector((actual, output, user, colony) => output["context"] = user + "/" + colony);
        f.References.CollectPawnReferenceMetadata(pawn, metadata, "u", "c");
        Check(metadata["context"] == "u/c", "Failed collector blocked subsequent metadata capture.");
        f.References.RegisterPawnReferenceMetadataResolver("broken", (_, _, _, _) => throw new Exception("resolve"));
        f.References.RegisterPawnReferenceMetadataResolver("blank", (_, _, _, _) => " ");
        Check(f.References.ResolvePawnReferenceMetadata(pawn, "broken", null, null) is null
            && f.References.ResolvePawnReferenceMetadata(pawn, "blank", null, null) is null
            && f.Scope.Diagnostics.Count(x => x.Code == "CompatibilityCallbackException") == 3, "Reference failure diagnostics/fallback changed.");
    }

    private static void FactionNotificationOrder()
    {
        var f = new Fixture(); Faction faction = New<Faction>(); var trace = new List<string>();
        f.Scope.BeginRegistrationCycle("factions");
        FactionPreparedHandler later = (_, _, _) => trace.Add("later-general");
        PlayerProxyFactionPreparedHandler proxy = (_, owner) => trace.Add("proxy:" + owner);
        f.Factions.RegisterFactionPreparedHandler((actual, purpose, owner) =>
        {
            Check(ReferenceEquals(actual, faction), "Faction callback lost its instance.");
            trace.Add(purpose + ":" + owner);
            f.Factions.RegisterFactionPreparedHandler(later);
            f.Factions.RegisterPlayerProxyFactionPreparedHandler(proxy);
        });
        f.Factions.RegisterFactionPreparedHandler((_, _, _) => throw new Exception("general"));
        f.Factions.NotifyPlayerProxyFactionPrepared(faction, " owner ");
        Check(trace.SequenceEqual(new[] { "PlayerProxy: owner ", "proxy: owner " }),
            "Generic/proxy notifications changed their order, snapshot timing or owner input.");
        trace.Clear(); f.Factions.NotifyFactionPrepared(faction, " custom ");
        Check(trace.SequenceEqual(new[] { "custom:", "later-general" }), "Next faction call did not see registration or trim purpose.");
        trace.Clear(); f.Factions.NotifyFactionPrepared(faction, " ");
        Check(trace.SequenceEqual(new[] { "Unknown:", "later-general" }), "Blank purpose fallback changed.");
        trace.Clear(); f.Factions.NotifyFactionPrepared(null!, "ignored");
        f.Factions.NotifyPlayerProxyFactionPrepared(faction, " ");
        Check(trace.Count == 0 && f.Scope.Diagnostics.Count == 3, "Invalid faction input executed callbacks or failure isolation changed.");
    }

    private static void ExchangeSnapshotsAndFailures()
    {
        var f = new Fixture(); var package = new ModPawnExchangePackageDto(); var trace = new List<string>(); Pawn pawn = New<Pawn>();
        f.Scope.BeginRegistrationCycle("exchange");
        PawnExchangePackageNormalizer later = _ => trace.Add("later");
        f.Exchange.RegisterPawnExchangePackageNormalizer(actual =>
        {
            Check(ReferenceEquals(actual, package), "Normalizer lost its package.");
            trace.Add("first"); f.Exchange.RegisterPawnExchangePackageNormalizer(later);
            throw new Exception("normalize");
        });
        f.Exchange.NormalizePawnExchangePackage(package);
        Check(trace.SequenceEqual(new[] { "first" }) && f.Log.Count == 2
            && f.Log[0].Contains("normalizer begin:", StringComparison.Ordinal)
            && f.Log[1].Contains("normalizer end:", StringComparison.Ordinal), "Normalizer changed snapshot timing or lost completion logging after failure.");
        trace.Clear(); f.Log.Clear(); f.Exchange.NormalizePawnExchangePackage(package);
        Check(trace.SequenceEqual(new[] { "first", "later" }) && f.Log.Count == 4, "Next normalization did not see the new callback exactly once.");
        f.Exchange.RegisterPawnExchangeExtensionAppender((_, _) => throw new Exception("append"));
        f.Exchange.RegisterPawnExchangeExtensionAppender((actual, output) =>
        {
            Check(ReferenceEquals(actual, pawn) && ReferenceEquals(output, package), "Appender lost inputs.");
            output.PackageVersion = 7;
        });
        f.Exchange.AppendPawnExchangeExtensions(pawn, package);
        f.Exchange.RegisterPawnExchangeLocalSaveOnlyLoadIdPredicate(_ => throw new Exception("id"));
        f.Exchange.RegisterPawnExchangeLocalSaveOnlyLoadIdPredicate(id => id == " local ");
        f.Exchange.RegisterPawnPostRestoreLocalizer(_ => throw new Exception("localize"));
        f.Exchange.RegisterPawnPostRestoreLocalizer(actual => { Check(ReferenceEquals(actual, pawn), "Localizer lost its pawn."); trace.Add("localize"); });
        f.Exchange.LocalizeRestoredPawnWithCompatibility(pawn);
        Check(package.PackageVersion == 7 && f.Exchange.IsPawnExchangeLocalSaveOnlyLoadId(" local ")
            && !f.Exchange.IsPawnExchangeLocalSaveOnlyLoadId(" ") && trace.Last() == "localize"
            && f.Scope.Diagnostics.Count == 5, "Best-effort exchange callbacks or local ID input semantics changed.");
    }

    private static void PackageRegistrationFailures()
    {
        foreach (string outcome in new[] { "success", "reject", "throw" })
        {
            var f = new Fixture(); f.Scope.BeginRegistrationCycle("registrars");
            var package = new ModPawnExchangePackageDto(); int laterCalls = 0;
            f.Exchange.RegisterPawnExchangePackageRegistrar((ModPawnExchangePackageDto actual, out string message) =>
            {
                Check(ReferenceEquals(actual, package), "Registrar lost its package.");
                message = "first";
                if (outcome == "throw") throw new Exception("registrar");
                return outcome != "reject";
            });
            f.Exchange.RegisterPawnExchangePackageRegistrar((ModPawnExchangePackageDto _, out string message) => { laterCalls++; message = "last"; return true; });
            bool accepted = f.Exchange.TryRegisterPawnExchangePackageExtensions(package, out string result);
            Check(accepted == (outcome == "success") && result == (outcome == "success" ? "last" : outcome == "reject" ? "first" : "Compatibility package failed.")
                && laterCalls == (outcome == "success" ? 1 : 0) && f.Scope.Diagnostics.Count == (outcome == "throw" ? 1 : 0),
                "Package registration changed short-circuiting, result message or exception policy.");
            Check(f.Exchange.TryRegisterPawnExchangePackageExtensions(null!, out result) && result == "", "Absent package no longer skips extension registration.");
        }
    }

    private static void TradeFallbackAndEffects()
    {
        var f = new Fixture(); Pawn pawn = New<Pawn>(); Pawn negotiator = New<Pawn>(); var trace = new List<string>();
        f.Scope.BeginRegistrationCycle("trade");
        f.Trade.RegisterTradeablePawnPredicate(_ => false);
        f.Trade.RegisterTradeablePawnPredicate(_ => throw new Exception("tradeable"));
        f.Trade.RegisterTradeablePawnPredicate(actual => ReferenceEquals(actual, pawn));
        f.Trade.RegisterTradePawnRestoreValidator(_ => false);
        f.Trade.RegisterTradePawnRestoreValidator(_ => throw new Exception("restore"));
        f.Trade.RegisterTradePawnRestoreValidator(actual => ReferenceEquals(actual, pawn));
        Check(f.Trade.IsTradeablePawnByCompatibility(pawn) && f.Trade.IsTradePawnRestoreAllowedByCompatibility(pawn),
            "Trade/restore extension predicates lost their any-match semantics.");
        Check(!f.Trade.IsTradeablePawnByCompatibility(null!) && !f.Trade.IsTradePawnRestoreAllowedByCompatibility(null!), "Null pawn reached trade extension predicates.");
        f.Trade.RegisterPawnSoldEffectHandler((_, _) => throw new Exception("sold"));
        f.Trade.RegisterPawnSoldEffectHandler((actual, actualNegotiator) =>
        {
            Check(ReferenceEquals(actual, pawn) && ReferenceEquals(actualNegotiator, negotiator), "Sold effects lost pawn/negotiator inputs.");
            trace.Add("sold");
        });
        f.Trade.ApplyPawnSoldEffects(pawn, negotiator);
        Check(trace.SequenceEqual(new[] { "sold" }) && f.Scope.Diagnostics.Count == 3, "Failed sale effect blocked later callbacks or lost diagnostics.");
    }

    private static void PublicFacade()
    {
        var owner = ClashOfRimCompatibilityApi.BeginRegistrationCycle("pawn-facade");
        Pawn pawn = New<Pawn>(); Faction faction = New<Faction>(); var package = new ModPawnExchangePackageDto();
        var trace = new List<string>(); var metadata = new Dictionary<string, string?>();
        ClashOfRimCompatibilityApi.RegisterFactionPreparedHandler((_, purpose, _) => trace.Add(purpose));
        ClashOfRimCompatibilityApi.RegisterPlayerProxyFactionPreparedHandler((_, owner) => trace.Add(owner));
        ClashOfRimCompatibilityApi.RegisterPawnReferenceMetadataCollector((_, result, user, colony) => result["context"] = user + "/" + colony);
        ClashOfRimCompatibilityApi.RegisterPawnReferenceMetadataResolver("context", (_, _, user, colony) => user + "/" + colony);
        ClashOfRimCompatibilityApi.RegisterPawnReferenceMetadataRestorer("context", (_, _, value, label) => label + ":" + value);
        ClashOfRimCompatibilityApi.RegisterPawnExchangeExtensionAppender((_, result) => result.PackageVersion = 2);
        ClashOfRimCompatibilityApi.RegisterPawnExchangePackageNormalizer(result => result.PackageVersion++);
        ClashOfRimCompatibilityApi.RegisterPawnExchangePackageRegistrar((ModPawnExchangePackageDto result, out string message) => { message = "registered"; return result.PackageVersion == 3; });
        ClashOfRimCompatibilityApi.RegisterPawnExchangeLocalSaveOnlyLoadIdPredicate(id => id == "local");
        ClashOfRimCompatibilityApi.RegisterPawnPostRestoreLocalizer(_ => trace.Add("localized"));
        ClashOfRimCompatibilityApi.RegisterPawnSoldEffectHandler((_, _) => trace.Add("sold"));
        ClashOfRimCompatibilityApi.RegisterTradeablePawnPredicate(_ => true);
        ClashOfRimCompatibilityApi.RegisterTradePawnRestoreValidator(_ => true);
        ClashOfRimCompatibilityApi.NotifyPlayerProxyFactionPrepared(faction, "owner");
        ClashOfRimCompatibilityApi.CollectPawnReferenceMetadata(pawn, metadata, "u", "c");
        ClashOfRimCompatibilityApi.AppendPawnExchangeExtensions(pawn, package); ClashOfRimCompatibilityApi.NormalizePawnExchangePackage(package);
        ClashOfRimCompatibilityApi.LocalizeRestoredPawnWithCompatibility(pawn); ClashOfRimCompatibilityApi.ApplyPawnSoldEffects(pawn, null);
        Check(trace.SequenceEqual(new[] { "PlayerProxy", "owner", "localized", "sold" }) && metadata["context"] == "u/c"
            && ClashOfRimCompatibilityApi.ResolvePawnReferenceMetadata(pawn, "context", "u", "c") == "u/c"
            && ClashOfRimCompatibilityApi.RestorePawnReferenceMetadata(pawn, metadata, "label") == "label:u/c"
            && ClashOfRimCompatibilityApi.TryRegisterPawnExchangePackageExtensions(package, out string message) && message == "registered"
            && ClashOfRimCompatibilityApi.IsPawnExchangeLocalSaveOnlyLoadId("local")
            && ClashOfRimCompatibilityApi.IsTradeablePawnByCompatibility(pawn) && ClashOfRimCompatibilityApi.IsTradePawnRestoreAllowedByCompatibility(pawn),
            "Public pawn/faction API did not forward to the composed capabilities.");
        Check(ClashOfRimCompatibilityApi.RevokeRegistrationToken(owner)
            && ClashOfRimCompatibilityApi.ResolvePawnReferenceMetadata(pawn, "context", "u", "c") is null
            && !ClashOfRimCompatibilityApi.IsTradeablePawnByCompatibility(pawn), "Public revocation did not reach capability state.");
    }

    private sealed class Fixture
    {
        internal readonly List<string> Log = new();
        internal readonly CompatibilityRegistrationScope Scope = new(_ => { });
        internal readonly FactionPreparationCompatibility Factions;
        internal readonly PawnReferenceCompatibility References;
        internal readonly PawnExchangeCompatibility Exchange;
        internal readonly PawnTradeCompatibility Trade;
        internal Fixture()
        {
            Factions = new(Scope); References = new(Scope); Exchange = new(Scope, Log.Add); Trade = new(Scope);
        }
    }
    private static T New<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
