namespace AIRsLight.ClashOfRim.Protocol;

public enum ProtocolDeliverySemantics
{
    OnlineImmediate,
    OfflinePending,
    RequiresSnapshotConfirmation,
    ServerNotification
}
