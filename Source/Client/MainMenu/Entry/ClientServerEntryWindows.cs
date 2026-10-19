using System;
using AIRsLight.ClashOfRim.Modules.World;
using Verse;

namespace AIRsLight.ClashOfRim.MainMenu;

internal sealed class ClientServerEntryWindows
{
    internal static ClientServerEntryWindows? Current { get; private set; }
    internal static void Attach(ClientServerEntryWindows? value) => Current = value;
    private readonly ClientWorldEntryStart entry;
    internal ClientServerEntryWindows(ClientWorldEntryStart entry) => this.entry = entry;
    internal static Action PrepareOpen()
    {
        var source = Current; var game = Verse.Current.Game;
        var form = source?.entry.CreateForm();
        bool CurrentMenu() => ReferenceEquals(Current, source) && ReferenceEquals(game, Verse.Current.Game)
            && Verse.Current.ProgramState == ProgramState.Entry;
        return () =>
        {
            if (!CurrentMenu()) return;
            if (form is null)
            {
                string message = ClashOfRimText.Key("ClashOfRim.ServerEntry.ModUnavailable");
                if (!CurrentMenu()) return;
                Log.Warning("[ClashOfRim] Main menu server entry requested before entry windows were available.");
                Find.WindowStack.Add(new Dialog_MessageBox(message));
                return;
            }
            if (form.IsCurrent && Find.WindowStack.WindowOfType<ClashOfRimServerEntryDialog>() is null)
                Find.WindowStack.Add(new ClashOfRimServerEntryDialog(form));
        };
    }
}
