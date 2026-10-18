using System;
using System.Collections.Generic;
using AIRsLight.ClashOfRim.Modules.World;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim;

internal sealed class ClientWorldGenerationPage : IClientWorldGenerationPage
{
    private readonly Window page;
    private readonly Game? game;
    private readonly Page? next;
    private readonly Action? nextAction;
    private readonly List<Window> blocking = new();
    internal ClientWorldGenerationPage(Window page)
    {
        this.page = page; game = Current.Game; next = (page as Page)?.next; nextAction = (page as Page)?.nextAct;
        for (int i = 0; i < Find.WindowStack.Count; i++)
            if (Find.WindowStack[i] is Page_SelectStoryteller or Page_CreateWorldParams or Page_SelectScenario) blocking.Add(Find.WindowStack[i]);
    }
    public bool IsCurrent => ReferenceEquals(game, Current.Game) && Find.WindowStack.IsOpen(page);
    public void Close() { if (next is not null) next.prev = null; page.Close(); }
    public void Reopen() { if (ReferenceEquals(game, Current.Game) && !Find.WindowStack.IsOpen(page)) Find.WindowStack.Add(page); }
    public void Continue(Func<bool> current)
    {
        foreach (var window in blocking)
        { if (!current()) return; if (Find.WindowStack.IsOpen(window)) window.Close(doCloseSound: false); }
        if (!current()) return;
        if (next is not null) Find.WindowStack.Add(next); else nextAction?.Invoke();
    }
}
internal sealed class ClientWorldGenerationPresentation : IClientWorldGenerationPresentation
{
    private readonly Action<string> status;
    internal ClientWorldGenerationPresentation(Action<string> status) => this.status = status;
    public void Generated(string seed) => status(ClashOfRimText.Key("ClashOfRim.WorldBaseline.StatusGenerated", seed.Named("SEED")));
    public void Failed(Exception exception)
    {
        Log.Error("[ClashOfRim] Server world generation failed: " + exception);
        string message = ClashOfRimText.Key("ClashOfRim.WorldBaseline.StatusGenerationFailed");
        status(message); Messages.Message(message, MessageTypeDefOf.RejectInput, historical: false);
    }
}
