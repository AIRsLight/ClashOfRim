using AIRsLight.ClashOfRim.Modules.Snapshots;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Packages;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Shop;

internal sealed class ClientShopCatalog
{
    private readonly ClientShopState state;
    private readonly ClientLocalMutationCoordinator mutations;
    private readonly IClientShopCatalogTransport transport;
    private readonly IClientShopPresentation presentation;
    private readonly Func<bool> isAdministrator;
    internal ClientShopCatalog(ClientShopState state, ClientLocalMutationCoordinator mutations,
        IClientShopCatalogTransport transport, IClientShopPresentation presentation, Func<bool> isAdministrator)
    { this.state = state; this.mutations = mutations; this.transport = transport; this.presentation = presentation; this.isAdministrator = isAdministrator; }

    internal Action Bind(Action action)
    { var owner = state.Capture(); return () => state.TryApply(owner, action); }

    internal Action<ClientShopListingEdit> BindUpsert()
    { var owner = state.Capture(); return edit => state.TryApply(owner, () => _ = UpsertAsync(edit)); }

    internal Action BindRemove(string listingId) => Bind(() => _ = RemoveAsync(listingId));

    internal Task RefreshAsync() => ExecuteAsync(false, "StatusRefreshing", "StatusRefresh",
        r => transport.ListAsync(r.Context, r.Scope.Owner.Token), r => r.Result, r => r.Listings ?? new(),
        listings => { state.Replace(listings); state.Message = presentation.Text("StatusRefreshed", ("COUNT", listings.Count)); });

    internal Task UpsertAsync(ClientShopListingEdit edit)
    {
        // Freeze editor values before dispatch, using the package copy policy shared
        // with trades. A still-open editor cannot change an in-flight command.
        var command = edit with { Item = ClientPackageCopies.CopyThings(new[] { edit.Item })[0] };
        return ExecuteAsync(true, "StatusSavingListing", "StatusSave",
            r => transport.UpsertAsync(r.Context, command, r.Scope.Owner.Token), r => r.Result,
            r => r.Listing is { } listing ? new[] { listing } : Array.Empty<ModServerShopListingDto>(),
            listings => { if (listings.Count > 0) state.Upsert(listings[0]); state.Message = presentation.Text("StatusListingSaved"); });
    }

    internal Task RemoveAsync(string listingId)
    {
        using var p = ClientSessionWork.EnterPublication();
        if (string.IsNullOrWhiteSpace(listingId)) { state.Message = presentation.Text("StatusMissingListing"); return Task.CompletedTask; }
        return ExecuteAsync<ModRemoveServerShopListingResponseDto>(true, "StatusRemovingListing", "StatusRemove",
            r => transport.RemoveAsync(r.Context, listingId, r.Scope.Owner.Token), r => r.Result, _ => Array.Empty<ModServerShopListingDto>(),
            _ => { state.Remove(listingId); state.Message = presentation.Text("StatusListingRemoved"); });
    }

    private ClientGameRequestTracker.Request? Begin(bool administrator, string status)
    {
        if (administrator && !isAdministrator()) { state.Message = presentation.NotAdministrator; return null; }
        var request = state.Begin();
        if (request is null) return null;
        var check = mutations.CheckAdmission(request.Context.IsConfigured, !string.IsNullOrWhiteSpace(request.Context.CurrentSnapshotId));
        if (check != LocalMutationAdmission.Allowed)
        { state.Message = presentation.Admission(check, mutations.Current); state.End(request); return null; }
        state.Message = presentation.Text(status);
        return request;
    }

    // List/upsert/remove share validation, hydration, owned publication and cleanup.
    private Task ExecuteAsync<T>(bool administrator, string status, string failurePrefix,
        Func<ClientGameRequestTracker.Request, Task<ClashOfRimClientNetworkResult<T>>> send, Func<T, ModProtocolResponseDto?> resultOf,
        Func<T, IReadOnlyList<ModServerShopListingDto>> listingsOf, Action<IReadOnlyList<ModServerShopListingDto>> accepted)
    {
        using var p = ClientSessionWork.EnterPublication();
        var request = Begin(administrator, status);
        if (request is null) return Task.CompletedTask;
        return ClientSessionWork.Run(async () =>
        {
            try
            {
                if (!state.TryApply(request, static () => { })) return;
                var result = await send(request).ConfigureAwait(false);
                bool valid = false;
                state.TryApply(request, () =>
                {
                    if (!result.Success || result.Response is null)
                        state.Message = presentation.Text(failurePrefix + "Failed", ("CODE", result.ErrorCode), ("MESSAGE", result.Message));
                    else if (resultOf(result.Response) is { Accepted: false } protocol)
                        state.Message = presentation.Text(failurePrefix + "Rejected", ("CODE", protocol.ErrorCode), ("MESSAGE", protocol.Message));
                    else valid = true;
                });
                if (!valid) return;
                var listings = ClientShopState.Copy(listingsOf(result.Response!));
                if (listings.Count > 0) await transport.HydrateAsync(request.Context, listings, request.Scope.Owner.Token).ConfigureAwait(false);
                state.TryApply(request, () =>
                {
                    accepted(listings);
                    QueuePreviews(request.Scope, listings);
                });
            }
            catch (Exception ex) { state.TryApply(request, () => Report(ex)); }
            finally { state.End(request); }
        });
    }

    private void QueuePreviews(ClientGameRequestTracker.Scope owner, IReadOnlyList<ModServerShopListingDto> listings)
    {
        if (!owner.Game.HasGame || listings.Count == 0) return;
        int version = state.Version;
        owner.Game.Enqueue(() => state.TryApply(owner, () =>
        { if (state.Version == version) presentation.PreparePreviews(listings); }), static () => { });
    }

    private void Report(Exception ex)
    { state.Message = presentation.Text("StatusException", ("TYPE", ex.GetType().Name), ("MESSAGE", ex.Message)); presentation.Diagnose(ex); }
}
