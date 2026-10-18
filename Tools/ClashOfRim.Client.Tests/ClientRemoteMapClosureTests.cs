using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AIRsLight.ClashOfRim.Modules.Maps;

internal static class ClientRemoteMapClosureTests
{
    internal static void Run()
    {
        var culture = CultureInfo.CurrentCulture;
        try
        {
            foreach (string language in new[] { "en-US", "zh-CN", "fr-FR", "tr-TR" })
            foreach (bool raid in new[] { false, true })
            foreach (bool hasMap in new[] { false, true })
            foreach (bool clear in new[] { false, true })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(language);
                var operation = new Operation { IsRaidBattle = raid, HasMap = hasMap };
                Check(ClientRemoteMapClosure.Close(operation, "reason-é", clear), "Valid closure failed.");
                var expected = (hasMap ? MapSteps : Array.Empty<string>()).Concat(new[] { "references", "parent", "complete", "release" });
                Check(operation.Calls.SequenceEqual(expected) && operation.Cleared == clear && operation.Reasons.All(r => r == "reason-é"), "Closure order or policy differs.");
                Check(!ClientRemoteMapClosure.Close(operation, "duplicate", clear), "Consumed operation reused.");
            }
            foreach (bool raid in new[] { false, true })
            foreach (string phase in MapSteps.Concat(new[] { "references", "parent" }))
            {
                var operation = new Operation { IsRaidBattle = raid, After = (o, step) => { if (step == phase) o.IsCurrent = false; } };
                Check(!ClientRemoteMapClosure.Close(operation, "stale", true) && !operation.Calls.Contains("complete")
                    && operation.Calls.Last() == "release" && !operation.Held, "Stale close continued after " + phase);
            }
            foreach (string phase in MapSteps.Concat(new[] { "references", "parent", "complete" }))
            {
                var operation = new Operation { After = (_, step) => { if (step == phase) throw new InvalidOperationException("injected"); } };
                bool threw = false;
                try { ClientRemoteMapClosure.Close(operation, "failure", true); } catch (InvalidOperationException) { threw = true; }
                Check(threw && !operation.Held && operation.Calls.Last() == "release", "Observation failure swallowed or retained admission: " + phase);
            }
            foreach (bool removedBeforeThrow in new[] { false, true })
            {
                var operation = new Operation { IsRaidBattle = true, RemoveBeforeThrow = removedBeforeThrow, ThrowOnRemove = true };
                bool closed = ClientRemoteMapClosure.Close(operation, "raid failure", true);
                Check(closed == removedBeforeThrow && operation.Calls.Contains("failure")
                    && operation.Calls.Contains("complete") == removedBeforeThrow && !operation.Held,
                    "Failed unload detached a live map or blocked already-completed removal.");
            }
            var staleFailure = new Operation { IsRaidBattle = true, ThrowOnRemove = true,
                After = (o, step) => { if (step == "failure") o.IsCurrent = false; } };
            Check(!ClientRemoteMapClosure.Close(staleFailure, "late", true) && !staleFailure.Calls.Contains("complete"), "Failure reporting rebound to replacement.");
            var reentrant = new Operation();
            reentrant.After = (o, _) => Check(!ClientRemoteMapClosure.Close(o, "nested", true), "Reentrant closure entered same target.");
            Check(ClientRemoteMapClosure.Close(reentrant, "outer", true), "Outer closure was affected by nested refusal.");
            var absent = new Operation { IsCurrent = false };
            Check(!ClientRemoteMapClosure.Close(absent, "missing", true) && absent.Calls.Count == 0, "Missing owner performed effects.");
        }
        finally { CultureInfo.CurrentCulture = culture; }
    }
    private static readonly string[] MapSteps = { "traps", "pockets", "fallback", "things", "remove" };
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class Operation : IClientRemoteMapCloseOperation, IDisposable
    {
        public bool IsCurrent { get; set; } = true;
        public bool IsRaidBattle { get; set; }
        public bool HasMap { get; set; } = true;
        internal bool Held, Used, ThrowOnRemove, RemoveBeforeThrow, Cleared;
        internal readonly List<string> Calls = new(), Reasons = new();
        internal Action<Operation, string>? After;
        public IDisposable? TryEnter() { if (!IsCurrent || Held || Used) return null; Used = Held = true; return this; }
        public void Dispose() { Held = false; Calls.Add("release"); }
        private void Step(string name, string? reason = null) { Calls.Add(name); if (reason is not null) Reasons.Add(reason); After?.Invoke(this, name); }
        public void EndHiddenTraps() => Step("traps");
        public void ClosePocketMaps(string reason) => Step("pockets", reason);
        public void PrepareCurrentMap() => Step("fallback");
        public void DiscardMapThings(string reason) => Step("things", reason);
        public void RemoveMap()
        {
            Step("remove");
            if (!ThrowOnRemove || RemoveBeforeThrow) HasMap = false;
            if (ThrowOnRemove) throw new InvalidOperationException("unload");
        }
        public void RemovalFailed(Exception exception, string reason) => Step("failure", reason);
        public void DiscardReferencePawns(string reason) => Step("references", reason);
        public void RemoveParent() => Step("parent");
        public void Complete(bool clearActiveSession, string reason) { Cleared = clearActiveSession; Step("complete", reason); }
    }
}
