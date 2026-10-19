using System;
using AIRsLight.ClashOfRim.Modules.World;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim;

internal sealed class ClientStorytellerPage : IClientStorytellerPage
{
    internal static ClientStorytellerConfiguration? Configuration { get; private set; }
    internal static void Attach(ClientStorytellerConfiguration? value) => Configuration = value;
    private readonly Window page;
    private readonly Game? game;
    internal ClientStorytellerPage(Window page) { this.page = page; game = Current.Game; }
    public bool IsCurrent => ReferenceEquals(game, Current.Game) && Find.WindowStack.IsOpen(page);
    public void Continue(Func<bool> current)
    {
        Page? next = (page as Page)?.next;
        page.Close();
        if (ReferenceEquals(game, Current.Game) && current()) Find.WindowStack.Add(next ?? new Page_CreateWorldParams());
    }
}
