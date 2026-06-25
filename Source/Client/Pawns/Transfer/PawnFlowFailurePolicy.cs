using AIRsLight.ClashOfRim.Gifts;
using AIRsLight.ClashOfRim.Support;
using Verse;

namespace AIRsLight.ClashOfRim.Pawns;

internal static class PawnFlowFailurePolicy
{
    public static bool ShouldReportTerminalFailure(GiftLandingApplicationResult? result)
    {
        return result is not null && ShouldReportTerminalGiftFailure(result.Kind);
    }

    public static bool ShouldReportTerminalFailure(SupportPawnApplicationResult? result)
    {
        return result?.TerminalFailure == true;
    }

    public static void LogApplicationFailure(
        string stage,
        string eventId,
        GiftLandingApplicationResult result)
    {
        LogFailure(
            stage,
            eventId,
            "GiftLanding",
            result?.Kind.ToString() ?? "<null>",
            result?.Message ?? "<null>",
            ShouldReportTerminalFailure(result));
    }

    public static void LogApplicationFailure(
        string stage,
        string eventId,
        SupportPawnApplicationResult result)
    {
        LogFailure(
            stage,
            eventId,
            "SupportPawn",
            result?.TerminalFailure == true ? "TerminalFailure" : "RecoverableFailure",
            result?.Message ?? "<null>",
            ShouldReportTerminalFailure(result));
    }

    public static void LogFailure(
        string stage,
        string eventId,
        string flowKind,
        string failureKind,
        string message,
        bool willReportToServer)
    {
        Log.Warning(
            $"[ClashOfRim][PawnFlowFailure] stage={stage ?? "<null>"} event={eventId ?? "<null>"} flow={flowKind ?? "<null>"} kind={failureKind ?? "<null>"} terminalReport={willReportToServer} message={message ?? "<null>"}");
    }

    private static bool ShouldReportTerminalGiftFailure(GiftLandingApplicationResultKind kind)
    {
        return kind is GiftLandingApplicationResultKind.MissingThingDef
            or GiftLandingApplicationResultKind.ThingCreationFailed;
    }
}
