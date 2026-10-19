using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Modules.Gifts;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Synchronization;

internal sealed class ClientGiftTestFixture : IClientGiftSource, IClientGiftTransport, IClientGiftPresentation
{
    internal readonly EventModuleTestPorts Ports = new();
    internal readonly EventModuleFailureView Failure;
    internal readonly ClientSynchronizationCoordinator Sync = new();
    internal readonly ClientLocalMutationCoordinator Mutations = new();
    internal readonly ClientSessionLifecycle Session;
    internal readonly ClientGiftOperations Gifts;
    internal ClientGiftCapabilities Capabilities = new(true, true);
    internal ClientGiftTarget Target = new("bob", "there", "map", "world", 4, 0, "目标");
    internal ClientGiftCargo Cargo;
    internal readonly List<ModThingReferenceDto> Items = new() { new() { GlobalKey = "wood", DefName = "WoodLog", StackCount = 5,
        Metadata = new() { ["state"] = "original" }, ThingPackage = new() { Label = "original-package" } } };
    internal bool TransportFailure, ServerRejection, RemovalFailure, RemovalException, CaptureRejected, CaptureThrowOnce;
    internal int Captured, Removed, Submissions;
    internal ClientGiftCommand? Sent;
    internal ClashOfRimClientNetworkContext? Context;
    internal ModSnapshotPackageMetadataDto? Package;
    internal byte[]? Payload;
    internal TaskCompletionSource<ClashOfRimClientNetworkResult<ModEventCreationResponseDto>>? Hold;
    internal readonly TaskCompletionSource Submitted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int gameThread;
    internal ClientGiftTestFixture(ClientGiftSourceKind kind, bool resetSession = true)
    {
        if (resetSession) ClientSessionWork.Reset(); gameThread = Environment.CurrentManagedThreadId;
        Cargo = new(kind, "cargo", 4, 0, true);
        Session = new ClientSessionLifecycle(Ports, Ports, new ClientSessionConnection(Ports));
        Failure = new EventModuleFailureView(Ports);
        var transactions = new ClientSnapshotTransaction(Session, Ports, new ClientSnapshotPublication(Ports, Ports));
        Gifts = new ClientGiftOperations(new ClientSnapshotMutationAdmission(Session, Sync, Mutations, transactions), Sync, Mutations,
            transactions, new ClientSnapshotFailureRecovery(Session, Mutations, Sync, Ports, Failure), this, this, () => Capabilities);
    }
    internal Task Start() => Gifts.SendAsync(this, Target, forced: true, "礼物");
    public ClientGiftCargo Describe() => Cargo;
    public ClientGiftCapture Capture(ClashOfRimClientNetworkContext context, bool forced)
    {
        // The caravan path must execute before handing work to the network worker.
        if (Cargo.Kind == ClientGiftSourceKind.Caravan) AssertGameThread();
        Captured++;
        if (CaptureThrowOnce) { CaptureThrowOnce = false; throw new InvalidOperationException("capture failure"); }
        return new(Items, CaptureRejected ? "not transferable" : null);
    }
    public ClientGiftRemoval Remove()
    { Removed++; if (RemovalException) throw new InvalidOperationException("partial remove"); return new(!RemovalFailure, "removed"); }
    public Task<ClashOfRimClientNetworkResult<ModEventCreationResponseDto>> SubmitAsync(ClashOfRimClientNetworkContext context,
        ClientGiftCommand command, ModSnapshotPackageMetadataDto snapshot, byte[] payload, CancellationToken token)
    {
        Context = context; Sent = command; Package = snapshot; Payload = payload; Submissions++; Submitted.TrySetResult();
        return Hold?.Task ?? Task.FromResult(TransportFailure ? ClashOfRimClientNetworkResult<ModEventCreationResponseDto>.Failed("HttpRequestException", "offline") : Success());
    }
    internal ClashOfRimClientNetworkResult<ModEventCreationResponseDto> Success() => ClashOfRimClientNetworkResult<ModEventCreationResponseDto>.Ok(
        new() { Result = new() { Accepted = !ServerRejection, ErrorCode = 1 }, EventId = "gift", AppliedSnapshotId = "accepted", NextLineageToken = "lineage" });
    public string Text(string key, params (string Name, object? Value)[] arguments) => key;
    public string Admission(LocalMutationAdmission admission, LocalMutationState? pending) => admission.ToString();
    public void Status(string message) { }
    public void Message(string message, bool rejected, bool forced = false) { }
    public void Diagnose(Exception exception) { }
    private void AssertGameThread()
    {
        if (Environment.CurrentManagedThreadId != gameThread)
            throw new InvalidOperationException("Caravan capture left the caller's game thread.");
    }
}
