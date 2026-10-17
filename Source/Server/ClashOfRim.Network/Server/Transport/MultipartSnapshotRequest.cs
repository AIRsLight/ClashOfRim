namespace AIRsLight.ClashOfRim.Network;

internal sealed record MultipartSnapshotRequest<TRequest>(TRequest? Request, byte[] Payload);
