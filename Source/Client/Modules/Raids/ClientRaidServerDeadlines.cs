using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Transport;

namespace AIRsLight.ClashOfRim.Modules.Raids;

internal sealed record ClientRaidServerDeadlines(long StartedAtUtcTicks, long DeadlineUtcTicks, long FinalDeadlineUtcTicks)
{
    internal static ClientRaidServerDeadlines? Read(ModEventCreationResponseDto response) =>
        Read(response.RaidStartedAtUtc, response.RaidDeadlineUtc, response.RaidFinalDeadlineUtc);
    internal static ClientRaidServerDeadlines? Read(string? startedAt, string? deadlineAt, string? finalAt) =>
        ClientServerTime.TryParseUtc(startedAt, out var started)
        && ClientServerTime.TryParseUtc(deadlineAt, out var deadline)
        && ClientServerTime.TryParseUtc(finalAt, out var final)
            ? new(started.UtcDateTime.Ticks, deadline.UtcDateTime.Ticks, final.UtcDateTime.Ticks) : null;
}
