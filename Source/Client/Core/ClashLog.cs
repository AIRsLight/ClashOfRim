using Verse;

namespace AIRsLight.ClashOfRim;

internal static class ClashLog
{
    public static void Message(string text)
    {
        if (Prefs.DevMode)
        {
            Log.Message(text);
        }
    }
}
