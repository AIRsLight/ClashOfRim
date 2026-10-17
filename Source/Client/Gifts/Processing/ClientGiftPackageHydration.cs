using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Packages;
using AIRsLight.ClashOfRim.Modules.Transport;
using AIRsLight.ClashOfRim.Pawns;
using Verse;

namespace AIRsLight.ClashOfRim.Gifts;

// Engine-facing presentation around the shared package hydration algorithm.
// The caller supplies the captured identity; this adapter never reads settings.
internal sealed class ClientGiftPackageHydration
{
    private readonly IClientProtocolEnvironment environment;
    private readonly Func<HttpClient> createHttpClient;

    internal ClientGiftPackageHydration(IClientProtocolEnvironment environment, Func<HttpClient>? createHttpClient = null)
    {
        this.environment = environment;
        this.createHttpClient = createHttpClient ?? (static () => new HttpClient());
    }

    internal async Task<PawnPackageTransferResult> HydrateAsync(GiftLandingPlan plan,
        ClashOfRimClientNetworkContext context, CancellationToken cancellationToken = default)
    {
        var items = plan.Items.Where(item =>
            (item.PawnPackage is null && !string.IsNullOrWhiteSpace(item.PawnPackageId))
            || (item.ThingPackage is null && !string.IsNullOrWhiteSpace(item.ThingPackageId))).ToList();
        if (items.Count == 0) return PawnPackageTransferResult.Ok();

        try
        {
            using var http = createHttpClient();
            var reader = new ClientPackageReader(new ClientProtocolTransport(http, context, environment), context,
                () => ClashOfRimText.Key("ClashOfRim.Network.StatusNotConfigured"));
            var pawns = await PawnPackageHydration.HydrateGiftItemsAsync(reader, items, cancellationToken).ConfigureAwait(false);
            if (!pawns.Success) return pawns;
            return await PawnPackageHydration.HydrateGiftThingStatePackagesAsync(reader, items, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException)
        {
            string message = ClashOfRimText.Key("ClashOfRim.PawnPackage.DownloadException",
                ex.GetType().Name.Named("TYPE"), ex.Message.Named("MESSAGE"));
            Log.Warning("[ClashOfRim][GiftProcess] pawn package hydrate failed: " + ex);
            return PawnPackageTransferResult.Failed(message);
        }
    }
}
