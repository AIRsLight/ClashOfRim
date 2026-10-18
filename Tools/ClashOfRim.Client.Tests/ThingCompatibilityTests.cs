using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Compatibility;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility.Things;
using UnityEngine;
using Verse;

internal static class ThingCompatibilityTests
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
                DisplayAndIterationSnapshots();
                MetadataFailurePolicies();
                FactoryAndDefinitionFallback();
                TransferFailureAndOrdering();
            }
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    private static void OwnershipAndLifecycle()
    {
        var scope = new CompatibilityRegistrationScope(_ => { });
        var references = new ThingReferenceCompatibility(scope);
        var transfers = new ThingTransferCompatibility(scope);
        var independent = new ThingReferenceCompatibility(new CompatibilityRegistrationScope(_ => { }));
        Thing thing = MakeThing(); var dto = new ModThingReferenceDto();
        for (int cycle = 0; cycle < 2; cycle++)
        {
            var token = scope.BeginRegistrationCycle("items");
            int callbacks = 0;
            references.RegisterThingReferenceEditor(
                (string _, ThingDef? _, ModThingReferenceDto _, Rect _, out float height) => { height = 12; callbacks++; return true; },
                (_, _, _) => callbacks++, (_, _, _) => { callbacks++; return false; });
            references.RegisterThingReferenceDefaultMetadataProvider((_, _, _) => callbacks++);
            references.RegisterThingReferenceUniqueRequestPredicate(_ => { callbacks++; return true; });
            references.RegisterThingReferenceMetadata((_, _) => callbacks++, (_, _) => { callbacks++; return false; },
                (_, _) => { callbacks++; return false; },
                (ModThingReferenceDto _, Thing _, out string? missing) => { missing = "missing"; callbacks++; return false; },
                _ => { callbacks++; return 3; });
            references.RegisterThingReferenceDisplay((_, _, parts) => { parts.Add("display"); callbacks++; },
                _ => { callbacks++; return true; });
            references.RegisterThingReferenceCacheKeyPartsProvider(_ => { callbacks++; return new[] { "key" }; });
            references.RegisterThingReferenceMetadataNormalizer(_ => callbacks++);
            references.RegisterThingReferenceThingFactory((ThingDef _, ThingDef? _, out Thing? created) => { created = thing; callbacks++; return true; });
            references.RegisterThingReferenceDefKindPredicate("item", _ => { callbacks++; return true; });
            references.RegisterDefensePointDefNameProvider(() => { callbacks++; return new[] { " Turret ", "", " " }; });
            transfers.RegisterThingTransferRule("policy", null, (_, _, _) => callbacks++, null);
            Check(scope.Records.Count == 11, "Capabilities did not share the registration lifecycle.");
            Check(references.TryDrawThingReferenceEditor("trade", thing.def, dto, default, out float height) && height == 12,
                "Editor did not return its height.");
            references.ClearThingReferenceMetadata("trade", thing.def, dto);
            references.ApplyThingReferenceDefaultMetadata("trade", thing.def, dto);
            references.NormalizeThingReferenceMetadata(dto);
            Check(!references.IsThingReferenceComplete("trade", thing.def, dto) && references.IsThingReferenceUniqueRequest(thing.def), "Editor validation or unique request predicate was lost.");
            references.AppendThingReferenceMetadata(thing, dto);
            Check(!references.ThingReferenceMetadataMatches(dto, thing) && !references.ThingReferenceMetadataMatches(dto, dto)
                && !references.TryApplyThingReferenceMetadata(dto, thing, out string? missing) && missing == "missing"
                && references.ThingReferenceMetadataStrictness(dto) == 3, "Metadata operations changed.");
            var parts = new List<string>(); references.AppendThingReferenceDisplayParts(dto, true, parts);
            Check(parts.Single() == "display" && references.SuppressesStandardThingStats(thing.def)
                && references.ThingReferenceCacheKeyParts(dto).Single() == "key", "Display/cache operations were lost.");
            Check(references.TryMakeThingReferenceThing(thing.def, null, out Thing? created) && ReferenceEquals(thing, created)
                && references.HasThingReferenceDefKind("item", thing.def)
                && references.GetCompatibilityDefensePointDefNames().Single() == "Turret", "Factory/definition operations changed.");
            Check(transfers.PrepareThingTransfer(thing, dto, ThingTransferContext.Outbound("gift"), out _), "Transfer capture failed.");
            Check(callbacks == 18, "A registered operation was skipped or invoked twice.");
            Check(!independent.IsThingReferenceUniqueRequest(thing.def) && !independent.ThingReferenceCacheKeyParts(dto).Any(),
                "An independent capability instance inherited global registrations.");

            if (cycle == 0) Check(scope.RevokeRegistrationToken(token), "Capability owner was not revoked.");
            else scope.BeginRegistrationCycle("next");
            parts.Clear(); references.AppendThingReferenceDisplayParts(dto, false, parts);
            references.ClearThingReferenceMetadata("trade", thing.def, dto);
            references.ApplyThingReferenceDefaultMetadata("trade", thing.def, dto);
            references.NormalizeThingReferenceMetadata(dto); references.AppendThingReferenceMetadata(thing, dto);
            Check(!references.TryDrawThingReferenceEditor("trade", thing.def, dto, default, out height) && height == 0
                && references.IsThingReferenceComplete("trade", thing.def, dto) && !references.IsThingReferenceUniqueRequest(thing.def)
                && references.ThingReferenceMetadataMatches(dto, thing) && references.ThingReferenceMetadataMatches(dto, dto)
                && references.TryApplyThingReferenceMetadata(dto, thing, out missing) && missing is null
                && references.ThingReferenceMetadataStrictness(dto) == 0 && parts.Count == 0
                && !references.SuppressesStandardThingStats(thing.def) && !references.ThingReferenceCacheKeyParts(dto).Any()
                && !references.TryMakeThingReferenceThing(thing.def, null, out created) && created is null
                && !references.HasThingReferenceDefKind("item", thing.def) && !references.GetCompatibilityDefensePointDefNames().Any()
                && transfers.PrepareThingTransfer(thing, dto, ThingTransferContext.Outbound("gift"), out _)
                && callbacks == 18 && scope.Records.Count == 0, "Revocation/new cycle retained a capability or cached callback.");
        }
    }

    private static void DisplayAndIterationSnapshots()
    {
        var scope = new CompatibilityRegistrationScope(_ => { }); var references = new ThingReferenceCompatibility(scope);
        var old = scope.BeginRegistrationCycle("old"); var dto = new ModThingReferenceDto();
        var parts = new List<string>();
        references.AppendThingReferenceDisplayParts(dto, false, parts); // Cache the empty snapshot before registration.
        references.RegisterThingReferenceDisplay("display", (_, _, output) => output.Add("old"), _ => false);
        references.AppendThingReferenceDisplayParts(dto, false, parts);
        Check(parts.SequenceEqual(new[] { "old" }), "Registration did not invalidate an empty display cache.");
        var winner = scope.CreateRegistrationToken("winner", 10);
        scope.UseRegistrationToken(winner, () => references.RegisterThingReferenceDisplay("display",
            (_, requirement, output) => output.Add(requirement ? "requirement" : "winner"), _ => true));
        scope.RevokeRegistrationToken(old);
        parts.Clear(); references.AppendThingReferenceDisplayParts(dto, true, parts);
        Check(parts.SequenceEqual(new[] { "requirement" }) && references.SuppressesStandardThingStats(null),
            "Replacement/revocation retained the old cached display or lost its arguments.");
        scope.RevokeRegistrationToken(winner);
        parts.Clear(); references.AppendThingReferenceDisplayParts(dto, false, parts);
        Check(parts.Count == 0 && !references.SuppressesStandardThingStats(null), "Revocation retained a display snapshot.");

        scope.BeginRegistrationCycle("snapshot");
        references.RegisterThingReferenceDisplay("first", (_, _, output) =>
        {
            output.Add("first");
            references.RegisterThingReferenceDisplay("later", (_, _, next) => next.Add("later"), _ => false);
        }, _ => false);
        references.AppendThingReferenceDisplayParts(dto, false, parts);
        Check(parts.SequenceEqual(new[] { "first" }), "Reentrant registration changed an active display enumeration.");
        parts.Clear(); references.AppendThingReferenceDisplayParts(dto, false, parts);
        Check(parts.SequenceEqual(new[] { "first", "later" }), "Next display call did not see the new registration.");

        IEnumerable<string> deferred = references.ThingReferenceCacheKeyParts(dto);
        references.RegisterThingReferenceCacheKeyPartsProvider(_ => new[] { "one", null! });
        using (var enumerator = deferred.GetEnumerator())
        {
            Check(enumerator.MoveNext() && enumerator.Current == "one", "Cache provider snapshot was captured before enumeration began.");
            references.RegisterThingReferenceCacheKeyPartsProvider(_ => new[] { "two" });
            Check(enumerator.MoveNext() && enumerator.Current == "" && !enumerator.MoveNext(),
                "Active key iteration changed its snapshot or stopped normalizing null parts.");
        }
        Check(references.ThingReferenceCacheKeyParts(dto).SequenceEqual(new[] { "one", "", "two" }), "Next key enumeration retained a stale snapshot.");
        scope.BeginRegistrationCycle("fresh");
        Check(!deferred.Any(), "Deferred enumeration retained providers from an old cycle.");
    }

    private static void MetadataFailurePolicies()
    {
        var scope = new CompatibilityRegistrationScope(_ => { }); var references = new ThingReferenceCompatibility(scope);
        scope.BeginRegistrationCycle("failures");
        var trace = new List<string>(); var dto = new ModThingReferenceDto(); Thing thing = MakeThing();
        references.RegisterThingReferenceMetadata("broken", (_, _) => throw new Exception("append"),
            (_, _) => throw new Exception("thing match"), (_, _) => throw new Exception("dto match"),
            (ModThingReferenceDto _, Thing _, out string? missing) => { missing = "partial"; throw new Exception("apply"); },
            _ => throw new Exception("strictness"));
        references.RegisterThingReferenceMetadata("valid", (_, _) => trace.Add("append"),
            (_, _) => { trace.Add("thing match"); return true; }, (_, _) => { trace.Add("dto match"); return true; },
            (ModThingReferenceDto _, Thing _, out string? missing) => { trace.Add("apply"); missing = null; return true; },
            _ => 7);
        references.AppendThingReferenceMetadata(thing, dto);
        Check(!references.ThingReferenceMetadataMatches(dto, thing) && !references.ThingReferenceMetadataMatches(dto, dto)
            && !references.TryApplyThingReferenceMetadata(dto, thing, out string? missing) && missing is null
            && references.ThingReferenceMetadataStrictness(dto) == 7 && trace.SequenceEqual(new[] { "append" }),
            "Metadata failure no longer distinguishes fail-closed matching/application from best-effort capture/scoring.");
        references.RegisterThingReferenceMetadataNormalizer(_ => throw new Exception("normalize"));
        references.RegisterThingReferenceMetadataNormalizer(item => item.Metadata["normalized"] = "yes");
        references.RegisterThingReferenceDefaultMetadataProvider((_, _, _) => throw new Exception("default"));
        references.RegisterThingReferenceDefaultMetadataProvider((_, _, item) => item.Metadata["default"] = "yes");
        references.NormalizeThingReferenceMetadata(dto); references.ApplyThingReferenceDefaultMetadata("request", null, dto);
        references.RegisterThingReferenceEditor("broken",
            (string _, ThingDef? _, ModThingReferenceDto _, Rect _, out float height) => { height = 100; throw new Exception("editor"); },
            (_, _, _) => throw new Exception("cleaner"), (_, _, _) => throw new Exception("validator"));
        references.RegisterThingReferenceEditor("valid",
            (string _, ThingDef? _, ModThingReferenceDto _, Rect _, out float height) => { height = 4; return true; },
            (_, _, _) => trace.Add("clean"), (_, _, _) => { trace.Add("validate"); return true; });
        Check(references.TryDrawThingReferenceEditor("request", null, dto, default, out float height) && height == 4
            && !references.IsThingReferenceComplete("request", null, dto), "Editor fallback/validation failure policy changed.");
        references.ClearThingReferenceMetadata("request", null, dto);
        Check(dto.Metadata["normalized"] == "yes" && dto.Metadata["default"] == "yes"
            && trace.SequenceEqual(new[] { "append", "clean" }) && scope.Diagnostics.Count == 10
            && scope.Diagnostics.All(x => x.Code == "CompatibilityCallbackException"), "Best-effort callback failure lost diagnostics or blocked later callbacks.");
    }

    private static void FactoryAndDefinitionFallback()
    {
        var scope = new CompatibilityRegistrationScope(_ => { }); var references = new ThingReferenceCompatibility(scope);
        scope.BeginRegistrationCycle("factories"); Thing expected = MakeThing();
        references.RegisterThingReferenceThingFactory("broken", (ThingDef _, ThingDef? _, out Thing? thing) => throw new Exception("factory"));
        references.RegisterThingReferenceThingFactory("no result", (ThingDef _, ThingDef? _, out Thing? thing) => { thing = null; return true; });
        references.RegisterThingReferenceThingFactory("not handled", (ThingDef _, ThingDef? _, out Thing? thing) => { thing = expected; return false; });
        references.RegisterThingReferenceThingFactory("valid", (ThingDef def, ThingDef? stuff, out Thing? thing) =>
        {
            Check(ReferenceEquals(def, expected.def) && ReferenceEquals(stuff, expected.def), "Factory lost its definition/stuff inputs.");
            thing = expected; return true;
        });
        Check(references.TryMakeThingReferenceThing(expected.def, expected.def, out Thing? result) && ReferenceEquals(result, expected), "Factory did not fall through failed/unhandled candidates.");
        references.RegisterThingReferenceDefKindPredicate(" ITEM ", _ => throw new Exception("predicate"));
        references.RegisterThingReferenceDefKindPredicate("ITEM", _ => true);
        Check(references.HasThingReferenceDefKind(" ITEM ", expected.def) && !references.HasThingReferenceDefKind("item", expected.def), "Definition kind lost ordinal matching or fallback.");
        references.RegisterDefensePointDefNameProvider(() => throw new Exception("def names"));
        references.RegisterDefensePointDefNameProvider(() => null!);
        references.RegisterDefensePointDefNameProvider(() => new[] { null!, " ", " Turret " });
        Check(references.GetCompatibilityDefensePointDefNames().Single() == "Turret", "Definition provider filtering/fallback changed.");
        references.RegisterThingReferenceUniqueRequestPredicate(_ => throw new Exception("unique"));
        references.RegisterThingReferenceUniqueRequestPredicate(_ => true);
        Check(references.IsThingReferenceUniqueRequest(null) && scope.Diagnostics.Count == 4, "Unique predicate fallback lost its diagnostics.");
    }

    private static void TransferFailureAndOrdering()
    {
        var scope = new CompatibilityRegistrationScope(_ => { }); var transfers = new ThingTransferCompatibility(scope);
        var trace = new List<string>(); Thing thing = MakeThing(); var dto = new ModThingReferenceDto();
        var context = ThingTransferContext.Outbound("gift");
        scope.BeginRegistrationCycle("low");
        transfers.RegisterThingTransferRule("low", (_, _) => { trace.Add("low"); return ThingTransferDecision.Allow(); },
            (_, _, _) => trace.Add("capture"), null);
        var high = scope.CreateRegistrationToken("high", 10);
        scope.UseRegistrationToken(high, () => transfers.RegisterThingTransferRule("reject", (_, _) => ThingTransferDecision.Reject("denied"), null, null));
        Check(!transfers.PrepareThingTransfer(thing, dto, context, out string? reason) && reason == "denied" && trace.Count == 0,
            "Transfer preparation ran capture or lower-priority validation after rejection.");
        scope.RevokeRegistrationToken(high);
        Check(transfers.PrepareThingTransfer(thing, dto, context, out reason) && reason is null
            && trace.SequenceEqual(new[] { "low", "capture" }), "Valid transfer did not validate before capture.");
        foreach (string failure in new[] { "validator", "capture", "finalizer" })
        {
            scope.BeginRegistrationCycle(failure); trace.Clear();
            transfers.RegisterThingTransferRule("broken",
                failure == "validator" ? (_, _) => throw new Exception("validation") : null,
                failure == "capture" ? (_, _, _) => throw new Exception("capture") : null,
                failure == "finalizer" ? (ModThingReferenceDto _, Thing _, ThingTransferContext _, out string? missing) => { missing = "missing-def"; throw new Exception("finalize"); } : null);
            transfers.RegisterThingTransferRule("later", null, (_, _, _) => trace.Add("capture"),
                (ModThingReferenceDto _, Thing _, ThingTransferContext _, out string? missing) => { missing = null; trace.Add("finalize"); return true; });
            if (failure == "finalizer")
                Check(!transfers.FinalizeThingTransfer(dto, thing, context, out string? missing) && missing == "missing-def", "Failed finalizer lost its missing definition.");
            else Check(!transfers.PrepareThingTransfer(thing, dto, context, out reason) && reason == ThingTransferDecision.ProcessorFailureCode,
                "Transfer callback exception did not fail closed.");
            Check(trace.Count == 0 && scope.Diagnostics.Count == 1, "Failed transfer invoked later stages or lost diagnostics.");
        }
        scope.BeginRegistrationCycle("empty");
        Check(!transfers.CanTransferThing(null!, context, out reason) && reason == ThingTransferDecision.ProcessorFailureCode
            && !transfers.PrepareThingTransfer(thing, null!, context, out reason) && reason == ThingTransferDecision.ProcessorFailureCode
            && !transfers.FinalizeThingTransfer(dto, thing, null!, out _), "Invalid transfer inputs no longer fail closed.");
    }

    private static Thing MakeThing()
    {
        var thing = (Thing)RuntimeHelpers.GetUninitializedObject(typeof(Thing));
        thing.def = (ThingDef)RuntimeHelpers.GetUninitializedObject(typeof(ThingDef));
        thing.def.defName = "TestThing";
        return thing;
    }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
