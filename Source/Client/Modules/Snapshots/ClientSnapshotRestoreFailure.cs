namespace AIRsLight.ClashOfRim.Modules.Snapshots;

internal enum ClientSnapshotRestoreFailureKind
{ PackageMissing, PayloadSizeMismatch, PayloadHashMismatch, DecodeFailed, OriginalSizeMismatch, OriginalHashMismatch, UnsupportedEncoding, InvalidXml, MetadataRewriteFailed }

internal sealed record ClientSnapshotRestoreFailure(ClientSnapshotRestoreFailureKind Kind, string? Message = null);
