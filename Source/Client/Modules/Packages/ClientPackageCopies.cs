using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.Packages;

internal static class ClientPackageCopies
{
    internal static List<ModThingReferenceDto> CopyThings(IReadOnlyList<ModThingReferenceDto> source)
    {
        var copy = ClientProtocolJson.Deserialize<List<ModThingReferenceDto>>(ClientProtocolJson.Serialize(source.ToList()), static type => type);
        CopyPreviewSidecars(source, copy);
        return copy;
    }

    // Preview bytes are deliberately excluded from authoritative package JSON.
    // Keep an independent copy when freezing a local command for later upload.
    internal static void CopyPreviewSidecars(IReadOnlyList<ModThingReferenceDto> source, IReadOnlyList<ModThingReferenceDto> copy)
    {
        for (int i = 0; i < source.Count; i++)
            if (source[i].PawnPackage?.PendingPreviewPng is { } preview && copy[i].PawnPackage is { } pawn)
                pawn.PendingPreviewPng = (byte[])preview.Clone();
    }
}
