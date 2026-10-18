using AIRsLight.ClashOfRim.Modules.World;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim;

internal sealed class ClientStorytellerPage : IClientStorytellerPage
{
    private readonly Window page;
    private readonly Game? game;
    internal ClientStorytellerPage(Window page) { this.page = page; game = Current.Game; }
    public bool IsCurrent => ReferenceEquals(game, Current.Game) && Find.WindowStack.IsOpen(page);
    public void Continue()
    {
        Page? next = (page as Page)?.next;
        page.Close();
        Find.WindowStack.Add(next ?? new Page_CreateWorldParams());
    }
}
