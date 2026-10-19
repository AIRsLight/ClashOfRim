namespace AIRsLight.ClashOfRim.Modules.Quests;

internal static class ClientQuestTiming
{
    internal const int TicksPerDay = 60000;
    internal static bool IsExpired(long due, long now) => due > 0 && now >= due;
    internal static int OverdueDays(long due, long now) => !IsExpired(due, now) ? 0
        : (int)System.Math.Min(int.MaxValue, (now - due) / TicksPerDay);
}
