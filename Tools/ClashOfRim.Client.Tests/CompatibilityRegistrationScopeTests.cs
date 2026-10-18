using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Compatibility;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;

internal static class CompatibilityRegistrationScopeTests
{
    internal static void Run()
    {
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            foreach (string culture in new[] { "en-US", "zh-CN", "tr-TR", "ja-JP" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                Lifecycle();
                PriorityAndReplacement();
                CallbackIsolation();
                PublicFacade();
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
            ClashOfRimCompatibilityApi.BeginRegistrationCycle("client-tests");
        }
    }

    private static void Lifecycle()
    {
        var warnings = new List<string>(); var scope = new CompatibilityRegistrationScope(warnings.Add);
        var items = new List<object>(); int changes = 0;
        scope.RegisterUnique(items, new object(), () => changes++);
        Check(scope.Records.Single().Owner == "<untracked>", "Registration without a token acquired an owner.");
        var root = scope.BeginRegistrationCycle(" root ", 3);
        Check(items.Count == 0 && scope.Records.Count == 0 && changes == 2 && root.Owner == "root",
            "A new cycle retained untracked capability data or its cache.");
        var plugin = scope.CreateRegistrationToken("plugin", 8);
        var trace = new List<string>();
        scope.UseRegistrationToken(plugin, () =>
        {
            scope.RegisterUnique(items, new object(), () => trace.Add("first"));
            scope.RegisterUnique(items, new object(), () => { trace.Add("last"); if (items.Count == 1) throw new Exception("cleanup"); });
        });
        trace.Clear();
        Check(scope.RevokeRegistrationToken(plugin) && items.Count == 0 && trace.SequenceEqual(new[] { "last", "first" })
            && warnings.Count == 1 && !scope.RevokeRegistrationToken(plugin), "Reverse cleanup stopped after a failed invalidation or repeated revocation.");
        var nested = scope.CreateRegistrationToken("nested");
        try { scope.UseRegistrationToken(nested, () => throw new InvalidOperationException("registration")); }
        catch (InvalidOperationException) { }
        scope.RegisterUnique(items, new object());
        Check(scope.Records.Single().Owner == "root" && scope.ActiveRegistrationPriority() == 3,
            "A failing token scope did not restore its previous owner.");
        var empty = scope.CreateRegistrationToken("root");
        Check(scope.RevokeRegistrationsByOwner(" ROOT ") == 1 && !scope.UseRegistrationToken(empty, () => { }),
            "Owner revocation changed case/whitespace or counted an empty token as registrations.");
        var replacement = scope.BeginRegistrationCycle("next");
        var foreign = new CompatibilityRegistrationToken(replacement.Owner, replacement.Generation, replacement.Id, replacement.Priority);
        Check(replacement.Generation == root.Generation + 1 && replacement.Id > root.Id && items.Count == 0
            && !scope.UseRegistrationToken(root, () => throw new Exception("old token"))
            && !scope.UseRegistrationToken(foreign, () => throw new Exception("foreign token")),
            "Generation reset reused token identity or accepted a foreign scope.");
    }

    private static void PriorityAndReplacement()
    {
        var scope = new CompatibilityRegistrationScope(_ => { }); var items = new List<Keyed>();
        var low = scope.BeginRegistrationCycle("low", 1);
        var high = scope.CreateRegistrationToken("high", 10); var equal = scope.CreateRegistrationToken("equal", 10);
        void Add(Keyed item) => scope.RegisterMetadataKeyedRegistration(items, item, "metadata", item.MetadataKey, "rejected", "replaced");
        var first = new Keyed("key"); var second = new Keyed("KEY"); var winner = new Keyed("key");
        Add(first); Add(second);
        scope.UseRegistrationToken(high, () => Add(winner));
        scope.UseRegistrationToken(equal, () => Add(new Keyed("key")));
        Check(items.SequenceEqual(new[] { winner, second }) && scope.Diagnostics.Select(x => x.Code).SequenceEqual(new[] { "replaced", "rejected" }),
            "Priority replacement, equal-priority rejection or ordinal metadata keys changed.");
        scope.RevokeRegistrationToken(low);
        Check(items.Count == 1 && ReferenceEquals(items[0], winner) && scope.Records.Single().Owner == "high",
            "Revoking the replaced owner removed the replacement.");
        scope.RevokeRegistrationToken(high);
        Check(items.Count == 0 && scope.Records.Count == 0, "Winning token retained its registration.");
        var callbacks = new List<Action>(); Action callback = NoOp;
        scope.RegisterUnique(callbacks, callback);
        scope.RegisterUnique(callbacks, new Action(NoOp));
        Check(callbacks.Count == 1, "Equivalent delegates registered twice.");
        var a = new object(); var b = new object(); var c = new object(); var ordered = new List<object>();
        var token = scope.CreateRegistrationToken("order", 20);
        scope.UseRegistrationToken(token, () => { scope.RegisterUnique(ordered, a); scope.RegisterUnique(ordered, b); });
        scope.RegisterUnique(ordered, c);
        Check(ordered.SequenceEqual(new[] { a, b, c }), "Equal-priority callbacks lost registration order.");
        scope.BeginRegistrationCycle("fresh");
        Check(ordered.Count == 0 && callbacks.Count == 0 && scope.Diagnostics.Count == 0, "Cycle reset retained capability data or diagnostics.");
    }

    private static void CallbackIsolation()
    {
        var warnings = new List<string>(); var scope = new CompatibilityRegistrationScope(warnings.Add);
        Action failing = () => throw new InvalidOperationException("callback");
        Check(!scope.TryInvokeCompatibilityCallback("action", failing, failing)
            && !scope.TryInvokeCompatibilityCallback<int>("value", failing, () => throw new Exception("value"), out int value)
            && value == 0 && scope.Diagnostics.Count == 2 && warnings.Count == 2, "Callback failure escaped or lost diagnostics.");
        Check(scope.TryInvokeCompatibilityCallback("valid", failing, () => 7, out int valid) && valid == 7,
            "An earlier failed callback blocked the next callback.");
    }

    private static void PublicFacade()
    {
        var token = ClashOfRimCompatibilityApi.BeginRegistrationCycle("facade", 10);
        var thing = new ModThingReferenceDto(); var parts = new List<string>();
        Check(!ClashOfRimCompatibilityApi.ThingReferenceCacheKeyParts(thing).Any(), "Cycle retained prior cached keys.");
        ClashOfRimCompatibilityApi.RegisterThingReferenceCacheKeyPartsProvider(_ => new[] { "key" });
        ClashOfRimCompatibilityApi.RegisterThingReferenceDisplay("test-display", (_, _, result) => result.Add("display"), _ => false);
        ClashOfRimCompatibilityApi.RegisterRemoteMapIntegerLoadIdRewriter("test-prefix", _ => true, () => 4);
        ClashOfRimCompatibilityApi.RegisterWorldGenerationFloatSettingProvider("test-setting", (ModWorldConfigurationDto _, out float result) => { result = 0.25f; return true; });
        ClashOfRimCompatibilityApi.AppendThingReferenceDisplayParts(thing, false, parts);
        Check(parts.SequenceEqual(new[] { "display" }) && ClashOfRimCompatibilityApi.ThingReferenceCacheKeyParts(thing).Single() == "key"
            && ClashOfRimCompatibilityApi.GetRemoteMapIntegerLoadIdRewriters().Single().NextId() == 4
            && ClashOfRimCompatibilityApi.ResolveWorldGenerationFloatSetting(new ModWorldConfigurationDto(), "TEST-SETTING", 1) == 0.25f,
            "Public registration did not reach the shared scope or invalidate snapshots.");
        Check(ClashOfRimCompatibilityApi.RevokeRegistrationToken(token), "Public revocation lost its token.");
        parts.Clear(); ClashOfRimCompatibilityApi.AppendThingReferenceDisplayParts(thing, false, parts);
        Check(parts.Count == 0 && !ClashOfRimCompatibilityApi.ThingReferenceCacheKeyParts(thing).Any()
            && ClashOfRimCompatibilityApi.GetRemoteMapIntegerLoadIdRewriters().Count == 0
            && ClashOfRimCompatibilityApi.ResolveWorldGenerationFloatSetting(new ModWorldConfigurationDto(), "test-setting", 1) == 1,
            "Revocation left capability data or cached handlers.");
        ClashOfRimCompatibilityApi.RegisterThingReferenceCacheKeyPartsProvider(_ => new[] { "untracked" });
        Check(ClashOfRimCompatibilityApi.ThingReferenceCacheKeyParts(thing).Single() == "untracked", "Untracked registration was rejected.");
        ClashOfRimCompatibilityApi.BeginRegistrationCycle("next");
        Check(!ClashOfRimCompatibilityApi.ThingReferenceCacheKeyParts(thing).Any(), "New cycle retained an untracked cached handler.");
    }

    private sealed class Keyed : IMetadataKeyedRegistration
    {
        internal Keyed(string key) => MetadataKey = key;
        public string MetadataKey { get; }
    }
    private static void NoOp() { }
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
}
