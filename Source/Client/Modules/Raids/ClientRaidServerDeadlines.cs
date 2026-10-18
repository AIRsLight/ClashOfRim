using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.Raids;

internal sealed record ClientRaidServerDeadlines(long StartedAtUtcTicks, long DeadlineUtcTicks, long FinalDeadlineUtcTicks)
{
    internal static ClientRaidServerDeadlines? Read(ModEventCreationResponseDto response) =>
        ClientServerTime.TryParseUtc(response.RaidStartedAtUtc, out var started)
        && ClientServerTime.TryParseUtc(response.RaidDeadlineUtc, out var deadline)
        && ClientServerTime.TryParseUtc(response.RaidFinalDeadlineUtc, out var final)
            ? new(started.UtcDateTime.Ticks, deadline.UtcDateTime.Ticks, final.UtcDateTime.Ticks) : null;
}
