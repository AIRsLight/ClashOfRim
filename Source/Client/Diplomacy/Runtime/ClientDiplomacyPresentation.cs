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
        if (string.Equals(relationKind, "Ally", StringComparison.OrdinalIgnoreCase))
        {
            resolved = FactionRelationKind.Ally;
            return true;
        }

        if (string.Equals(relationKind, "Hostile", StringComparison.OrdinalIgnoreCase))
        {
            resolved = FactionRelationKind.Hostile;
            return true;
        }

        if (string.Equals(relationKind, "Neutral", StringComparison.OrdinalIgnoreCase))
        {
            resolved = FactionRelationKind.Neutral;
            return true;
        }

        resolved = FactionRelationKind.Neutral;
        return false;
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

    internal static string FormatDiplomacyKind(string kind)
    {
        return kind switch
        {
            "AllianceRequest" => ClashOfRimText.Key("ClashOfRim.EventType.AllianceRequest"),
            "AllianceCancellation" => ClashOfRimText.Key("ClashOfRim.EventType.AllianceCancellation"),
            "SupportRequest" => ClashOfRimText.Key("ClashOfRim.EventType.SupportRequest"),
            "WarDeclaration" => ClashOfRimText.Key("ClashOfRim.EventType.WarDeclaration"),
            "PeaceRequest" => ClashOfRimText.Key("ClashOfRim.EventType.PeaceRequest"),
            _ => kind
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
