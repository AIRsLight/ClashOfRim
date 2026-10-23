using System;
using System.Globalization;
using System.Reflection;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Pawns;
using Verse;

namespace AIRsLight.ClashOfRim.Trades;

// A corpse is an item containing a dead pawn, not a pawn sale or a definition-only item.
internal static class CorpseTransferUtility
{
    internal const string AgeMetadataKey = "ClashOfRim.CorpseAgeTicks";
    internal const string VanishAfterTimestampMetadataKey = "ClashOfRim.CorpseVanishAfterTimestamp";
    internal const string EverBuriedInSarcophagusMetadataKey = "ClashOfRim.CorpseEverBuriedInSarcophagus";
    private static readonly FieldInfo? VanishAfterTimestampField = typeof(Corpse).GetField(
        "vanishAfterTimestamp", BindingFlags.Instance | BindingFlags.NonPublic);

    internal static void Append(Thing thing, ModThingReferenceDto reference, string userId,
        string colonyId, string snapshotId, string containerKey)
    {
        if (thing is not Corpse corpse) return;
        Pawn pawn = corpse.InnerPawn ?? throw new InvalidOperationException("Cannot transfer a corpse without its inner pawn.");
        reference.PawnPackage = GiftPawnPackageUtility.BuildPackage(pawn, PawnGlobalIdUtility.Build(userId, pawn),
            userId, colonyId, snapshotId, containerKey);
        reference.Metadata[AgeMetadataKey] = corpse.Age.ToString(CultureInfo.InvariantCulture);
        reference.Metadata[VanishAfterTimestampMetadataKey] = ((int?)VanishAfterTimestampField?.GetValue(corpse) ?? -1).ToString(CultureInfo.InvariantCulture);
        reference.Metadata[EverBuriedInSarcophagusMetadataKey] = corpse.everBuriedInSarcophagus.ToString();
    }

    internal static bool TryRestore(ModThingReferenceDto reference, ThingDef def, out Thing? thing)
    {
        thing = null;
        if (reference.PawnPackage is null
            || !GiftPawnPackageUtility.TryRestoreCorpsePawn(reference.PawnPackage, out Pawn? pawn, out _)
            || pawn?.RaceProps?.corpseDef != def)
            return false;

        // MakeCorpse attaches the inner pawn before any rot/health metadata is applied.
        Corpse? corpse = pawn.MakeCorpse(null, inBed: false, bedRotation: 0f);
        if (corpse is null) return false;
        if (reference.Metadata.TryGetValue(AgeMetadataKey, out string? ageText)
            && int.TryParse(ageText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int age) && age >= 0)
            corpse.Age = age;
        if (reference.Metadata.TryGetValue(VanishAfterTimestampMetadataKey, out string? vanishText)
            && int.TryParse(vanishText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int vanish))
            VanishAfterTimestampField?.SetValue(corpse, vanish);
        if (reference.Metadata.TryGetValue(EverBuriedInSarcophagusMetadataKey, out string? buriedText)
            && bool.TryParse(buriedText, out bool buried))
            corpse.everBuriedInSarcophagus = buried;
        thing = corpse;
        return true;
    }
}
