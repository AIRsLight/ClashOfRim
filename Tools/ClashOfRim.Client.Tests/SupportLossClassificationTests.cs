using System;
using System.Reflection;
using AIRsLight.ClashOfRim;

internal static class SupportLossClassificationTests
{
    public static void Run()
    {
        var assembly = typeof(ClashOfRimMod).Assembly;
        var reader = assembly.GetType("AIRsLight.ClashOfRim.Support.SupportPawnPayloadReader", true)!.GetMethod("Read")!;
        var classify = assembly.GetType("AIRsLight.ClashOfRim.Support.SupportPawnApplicator", true)!.GetMethod("IsSupportLossReturn", BindingFlags.Static | BindingFlags.NonPublic)!;
        bool Loss(string json) => (bool)classify.Invoke(null, new[] { reader.Invoke(null, new object[] { json }) })!;
        foreach (string reason in new[] { "QuestExpired", "EarlyReturn", "custom:reason" })
            if (!Loss("{\"returnToSender\":true,\"returnReason\":\"" + reason + "\",\"pawnReference\":{\"dead\":true}}"))
                throw new Exception("Death return depends on a reason label.");
        foreach (string reason in new[] { "SnapshotDeath", "RaidSettlementLost", "MapUnloaded" })
            if (!Loss("{\"returnToSender\":true,\"returnReason\":\"" + reason + "\"}"))
                throw new Exception("Persisted legacy death return is no longer recognized.");
        foreach (string json in new[]
        {
            "{\"returnToSender\":false,\"pawnReference\":{\"dead\":true}}",
            "{\"returnToSender\":true,\"returnReason\":\"QuestExpired\"}",
            "{\"returnToSender\":true,\"pawnReference\":{\"dead\":true},\"pawnPackage\":{}}"
        }) if (Loss(json)) throw new Exception("Non-loss or malformed package was classified as a death.");
    }
}
