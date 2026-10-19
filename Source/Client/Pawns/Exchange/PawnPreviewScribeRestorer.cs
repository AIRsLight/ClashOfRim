using System;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using Verse;

namespace AIRsLight.ClashOfRim.Pawns;

internal static class PawnPreviewScribeRestorer
{
    internal static bool TryRestore(ModPawnExchangePackageDto input, out Pawn? pawn, out string message)
    {
        pawn = null;
        message = string.Empty;
        // Preview construction must not consume the ongoing simulation's RNG.
        Rand.PushState();
        try
        {
            string label = ClashOfRimText.Key("ClashOfRim.PawnExchange.LabelTradePawn");
            var package = ClientProtocolJson.Copy(input);
            ClashOfRimCompatibilityApi.NormalizePawnExchangePackage(package);
            if (!PawnScribeLoader.TryPrepare(package.Scribe, label, out var document, out message)) return false;
            if (!PawnScribeLoader.TryLoadPreview(document, label, out var loaded, out message)) return false;
            // Details do not authorize a sale. Faction/guest ownership was
            // deliberately detached, so purchase eligibility cannot be used here.
            if (loaded.RaceProps is null)
            { message = ClashOfRimText.Key("ClashOfRim.PawnExchange.ValidatorTradePawn"); return false; }
            if (!PawnScribeLoader.ValidateRuntimeState(loaded, label, out message)) return false;
            // Reserve unique engine IDs for detached objects too, so information
            // card/stat caches cannot alias a real pawn or its gear. This does not
            // register global exchange identities or invoke arrival localizers.
            PawnExchangeLoadIdUtility.LocalizePreviewPawn(loaded);
            if (!ClashOfRimCompatibilityApi.TryRestorePawnPreview(loaded, package, out message)) return false;
            pawn = loaded;
            return true;
        }
        finally { Rand.PopState(); }
    }
}
