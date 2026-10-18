using System;
using System.Linq;
using AIRsLight.ClashOfRim.Modules.Achievements;
using Verse;

namespace AIRsLight.ClashOfRim.Achievements;

internal sealed class ClientAchievementPresentation : IClientAchievementPresentation
{
    public string Text(string key, params (string Name, object? Value)[] arguments)
        => ClashOfRimText.Key(key, arguments.Select(argument => (argument.Value ?? "").Named(argument.Name)).ToArray());
    public void Diagnose(Exception exception) => Log.Warning("[ClashOfRim] List achievements failed: " + exception);
}
