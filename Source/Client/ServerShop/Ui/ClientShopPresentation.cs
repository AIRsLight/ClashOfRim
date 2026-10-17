using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Shop;
using AIRsLight.ClashOfRim.Modules.Synchronization;
using AIRsLight.ClashOfRim.Trades;
using Verse;

namespace AIRsLight.ClashOfRim.ServerShop;

internal sealed class ClientShopPresentation : IClientShopPresentation
{
    public string NotAdministrator => ClashOfRimText.Key("ClashOfRim.Admin.NotAdministrator");
    public string Text(string key, params (string Name, object? Value)[] arguments) =>
        ClashOfRimText.Key("ClashOfRim.Shop." + key, arguments.Select(a => (a.Value ?? string.Empty).Named(a.Name)).ToArray());
    public string Admission(LocalMutationAdmission admission, LocalMutationState? pending) => ClientLocalMutationPresentation.AdmissionFailure(admission, pending);
    public void PreparePreviews(IReadOnlyList<ModServerShopListingDto> listings)
    {
        foreach (var listing in listings)
            if (listing.Item is { } item) TradeUiUtility.PreparePawnPreviewsForThingReferences(new[] { item }, listing.ListingId);
    }
    public void Diagnose(Exception exception) => Log.Warning("[ClashOfRim] Server shop operation failed: " + exception);
}
