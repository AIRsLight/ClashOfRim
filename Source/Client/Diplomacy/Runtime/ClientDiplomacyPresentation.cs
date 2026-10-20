using AIRsLight.ClashOfRim.Modules.Diplomacy;
using System;
using AIRsLight.ClashOfRim.Protocol;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Diplomacy;

internal static class ClientDiplomacyPresentation
{
    internal static FactionRelationKind ResolveDiplomacyRelationKind(ServerEventType eventType)
    {
        return eventType switch
        {
            ServerEventType.AllianceRequest => FactionRelationKind.Ally,
            ServerEventType.AllianceCancellation => FactionRelationKind.Neutral,
            ServerEventType.WarDeclaration => FactionRelationKind.Hostile,
            ServerEventType.PeaceRequest => FactionRelationKind.Neutral,
            _ => FactionRelationKind.Neutral
        };
    }

    internal static bool TryResolveServerRelationKind(string? relationKind, out FactionRelationKind resolved)
    {
        bool valid = ClientDiplomacyNames.TryRelation(relationKind, out var relation);
        resolved = relation switch
        {
            ClientDiplomacyRelation.Ally => FactionRelationKind.Ally,
            ClientDiplomacyRelation.Hostile => FactionRelationKind.Hostile,
            _ => FactionRelationKind.Neutral
        };
        return valid;
    }

    internal static string DiplomacyRelationReason(ServerEventType eventType)
    {
        return eventType switch
        {
            ServerEventType.WarDeclaration => ClashOfRimText.Key("ClashOfRim.Diplomacy.RelationReasonWarDeclaration"),
            ServerEventType.AllianceCancellation => ClashOfRimText.Key("ClashOfRim.Diplomacy.RelationReasonAllianceCancellation"),
            ServerEventType.AllianceRequest => ClashOfRimText.Key("ClashOfRim.Diplomacy.RelationReasonAllianceRequest"),
            ServerEventType.PeaceRequest => ClashOfRimText.Key("ClashOfRim.Diplomacy.RelationReasonPeaceRequest"),
            _ => "ClashOfRim"
        };
    }

    internal static string FormatDiplomacyRelationKind(FactionRelationKind relationKind)
    {
        return relationKind switch
        {
            FactionRelationKind.Ally => ClashOfRimText.Key("ClashOfRim.Diplomacy.RelationAlly"),
            FactionRelationKind.Hostile => ClashOfRimText.Key("ClashOfRim.Diplomacy.RelationHostile"),
            _ => ClashOfRimText.Key("ClashOfRim.Diplomacy.RelationNeutral")
        };
    }

    internal static string DefaultDiplomacyMessage(string kind)
    {
        return kind switch
        {
            "AllianceRequest" => ClashOfRimText.Key("ClashOfRim.EventLetter.DiplomacyDefaultAllianceRequest"),
            "AllianceCancellation" => ClashOfRimText.Key("ClashOfRim.EventLetter.DiplomacyDefaultAllianceCancellation"),
            "SupportRequest" => ClashOfRimText.Key("ClashOfRim.EventLetter.DiplomacyDefaultSupportRequest"),
            "WarDeclaration" => ClashOfRimText.Key("ClashOfRim.EventLetter.DiplomacyDefaultWarDeclaration"),
            "PeaceRequest" => ClashOfRimText.Key("ClashOfRim.EventLetter.DiplomacyDefaultPeaceRequest"),
            _ => ClashOfRimText.Key("ClashOfRim.EventLetter.DiplomacyDefaultGeneric")
        };
    }

    internal static string DiplomacyRelationReason(string eventKind) =>
        Enum.TryParse(eventKind, out ServerEventType kind) ? DiplomacyRelationReason(kind) : "ClashOfRim";
}
