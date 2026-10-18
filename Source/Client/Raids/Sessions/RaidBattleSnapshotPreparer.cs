using System;
using AIRsLight.ClashOfRim.Modules.Raids;
using Verse;

namespace AIRsLight.ClashOfRim.Raids;

// Public compatibility facade; validation and save serialization belong to the raid module.
public static class RaidBattleSnapshotPreparer
{
    public static bool TryPrepareReturnedSnapshot(byte[] saveBytes, ActiveRaidBattleSession session,
        out byte[] preparedSaveBytes, out string failureReason)
    {
        byte[]? prepared = ClientRaidSettlementSnapshot.Prepare(saveBytes, session.EventId, session.TargetMapId, out var failure);
        preparedSaveBytes = prepared ?? Array.Empty<byte>();
        failureReason = failure is null ? string.Empty : ClashOfRimText.Key("ClashOfRim.Raid." + failure.Key, (failure.Message ?? string.Empty).Named("MESSAGE"));
        return prepared is not null;
    }
}
