using AIRsLight.ClashOfRim.ClientNetwork;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.ThirdPartyCompatibility;

public enum ThingTransferDirection
{
    Outbound = 0,
    Inbound = 1
}

public sealed class ThingTransferContext
{
    private ThingTransferContext(string surface, ThingTransferDirection direction, Faction? receivingFaction)
    {
        Surface = string.IsNullOrWhiteSpace(surface) ? "unknown" : surface.Trim();
        Direction = direction;
        ReceivingFaction = receivingFaction;
    }

    public string Surface { get; }

    public ThingTransferDirection Direction { get; }

    public Faction? ReceivingFaction { get; }

    public static ThingTransferContext Outbound(string surface)
    {
        return new ThingTransferContext(surface, ThingTransferDirection.Outbound, null);
    }

    public static ThingTransferContext Inbound(string surface, Faction? receivingFaction)
    {
        return new ThingTransferContext(surface, ThingTransferDirection.Inbound, receivingFaction);
    }
}

public readonly struct ThingTransferDecision
{
    public const string ProcessorFailureCode = "thing-transfer.processor-failure";

    private ThingTransferDecision(bool allowed, string? rejectionCode)
    {
        Allowed = allowed;
        RejectionCode = rejectionCode;
    }

    public bool Allowed { get; }

    public string? RejectionCode { get; }

    public static ThingTransferDecision Allow()
    {
        return new ThingTransferDecision(true, null);
    }

    public static ThingTransferDecision Reject(string rejectionCode)
    {
        return new ThingTransferDecision(
            false,
            string.IsNullOrWhiteSpace(rejectionCode) ? ProcessorFailureCode : rejectionCode.Trim());
    }
}

public delegate ThingTransferDecision ThingTransferValidator(Thing thing, ThingTransferContext context);

public delegate void ThingTransferMetadataCapturer(
    Thing thing,
    ModThingReferenceDto reference,
    ThingTransferContext context);

public delegate bool ThingTransferFinalizer(
    ModThingReferenceDto reference,
    Thing thing,
    ThingTransferContext context,
    out string? missingDefName);

internal sealed class ThingTransferRuleRegistration : IMetadataKeyedRegistration
{
    public ThingTransferRuleRegistration(
        string metadataKey,
        ThingTransferValidator? validator,
        ThingTransferMetadataCapturer? capturer,
        ThingTransferFinalizer? finalizer)
    {
        MetadataKey = metadataKey?.Trim() ?? string.Empty;
        Validator = validator;
        Capturer = capturer;
        Finalizer = finalizer;
    }

    public string MetadataKey { get; }

    public ThingTransferValidator? Validator { get; }

    public ThingTransferMetadataCapturer? Capturer { get; }

    public ThingTransferFinalizer? Finalizer { get; }
}
