using System;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Modules.Diplomacy;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Diplomacy;

internal sealed class ClientDiplomacyCreationPresentation : IClientDiplomacyCreationPresentation
{
    private readonly Action<string> setStatus;
    internal ClientDiplomacyCreationPresentation(Action<string> setStatus) => this.setStatus = setStatus;
    public string DefaultMessage(string kind) => ClientDiplomacyPresentation.DefaultDiplomacyMessage(kind);
    public void ApplyRelation(string userId, string relation, string kind) =>
        ClientDiplomacyRelationEffects.Apply(userId, relation, ClientDiplomacyPresentation.DiplomacyRelationReason(kind));
    public void Report(ClientDiplomacyCreationNotice notice)
    {
        string text = Format(notice); setStatus(text);
        if (notice.Stage == ClientDiplomacyCreationStage.Accepted) Messages.Message(text, MessageTypeDefOf.NeutralEvent, historical: false);
        if (notice.Exception is not null) Log.Warning("[ClashOfRim] Diplomacy event creation failed: " + notice.Exception);
    }
    internal static string Format(ClientDiplomacyCreationNotice notice) => notice.Stage switch
    {
        ClientDiplomacyCreationStage.AdmissionFailed => ClientLocalMutationPresentation.AdmissionFailure(notice.Admission, notice.Pending),
        ClientDiplomacyCreationStage.Busy => ClashOfRimText.Key("ClashOfRim.Sync.StatusBusy"),
        ClientDiplomacyCreationStage.Accepted => ClashOfRimText.Key("ClashOfRim.Diplomacy.Message" + notice.Kind + "Sent",
            (string.IsNullOrWhiteSpace(notice.Target.DisplayName) ? notice.Target.UserId : notice.Target.DisplayName!).Named("PLAYER")),
        _ => ClashOfRimText.Key("ClashOfRim.Diplomacy.Creation" + notice.Stage, (notice.Code ?? string.Empty).Named("CODE"),
            (notice.Message ?? notice.Exception?.Message ?? string.Empty).Named("MESSAGE"), (notice.Exception?.GetType().Name ?? string.Empty).Named("TYPE"))
    };
}

internal static class ClientDiplomacyRelationEffects
{
    internal static void Apply(string userId, string? relationKind, string reason)
    {
        if (ClientDiplomacyPresentation.TryResolveServerRelationKind(relationKind, out var relation))
        {
            if (!PlayerFactionProxyUtility.SetPlayerRelation(userId, relation, reason))
                Log.Warning($"[ClashOfRim][Diplomacy] proxy unavailable user={userId} relation={relationKind}; refreshing projections.");
        }
        else Log.Warning($"[ClashOfRim][Diplomacy] unrecognized receipt relation={relationKind ?? "<null>"}; refreshing projections.");
    }
}
