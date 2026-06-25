using System;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.Quests;

internal static class ClashManagedQuestTimingUtility
{
    public const int TicksPerDay = 60000;

    public static long CurrentGameTicks => Find.TickManager?.TicksGame ?? 0;

    public static bool IsExpired(long dueAtGameTicks)
    {
        return IsExpiredAt(dueAtGameTicks, CurrentGameTicks);
    }

    public static bool IsExpired(long? dueAtGameTicks)
    {
        return dueAtGameTicks.HasValue && IsExpired(dueAtGameTicks.Value);
    }

    public static bool IsExpiredAt(long dueAtGameTicks, long currentGameTicks)
    {
        return dueAtGameTicks > 0 && currentGameTicks >= dueAtGameTicks;
    }

    public static long RemainingTicks(long dueAtGameTicks)
    {
        return Math.Max(0, dueAtGameTicks - CurrentGameTicks);
    }

    public static string FormatRemainingPeriod(long remainingTicks)
    {
        return ((int)Math.Min(int.MaxValue, Math.Max(0, remainingTicks)))
            .ToStringTicksToPeriodVerbose(true, true);
    }

    public static string FormatDueStatus(long dueAtGameTicks, string unknownKey, string overdueKey, string dueInKey)
    {
        if (dueAtGameTicks <= 0)
        {
            return ClashOfRimText.Key(unknownKey);
        }

        long remainingTicks = dueAtGameTicks - CurrentGameTicks;
        if (remainingTicks <= 0)
        {
            return ClashOfRimText.Key(overdueKey);
        }

        return ClashOfRimText.Key(
            dueInKey,
            FormatRemainingPeriod(remainingTicks).Named("TIME"));
    }

    public static bool ShouldSendDueWarning(long dueAtGameTicks, bool warningAlreadySent, int warningWindowTicks, out long remainingTicks)
    {
        remainingTicks = dueAtGameTicks - CurrentGameTicks;
        return dueAtGameTicks > 0
            && !warningAlreadySent
            && remainingTicks > 0
            && remainingTicks <= warningWindowTicks;
    }
}
