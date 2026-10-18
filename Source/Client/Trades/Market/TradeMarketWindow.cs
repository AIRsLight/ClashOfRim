using AIRsLight.ClashOfRim.Multiplayer.Pages;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Trades;

public sealed class TradeMarketWindow : Window
{
    private readonly MultiplayerTradePage page;

    // Retain the public construction signature as a one-way composition entry.
    public TradeMarketWindow(ClashOfRimMod mod) : this(mod.CreateTradePage()) { }

    internal TradeMarketWindow(MultiplayerTradePage page)
    {
        this.page = page;
        doCloseX = true;
        closeOnClickedOutside = false;
        absorbInputAroundWindow = false;
        forcePause = false;
        draggable = true;
    }

    public override Vector2 InitialSize => new(940f, 700f);
    public override void PostOpen()
    {
        base.PostOpen();
        page.Refresh();
    }
    public override void PostClose()
    {
        page.Close();
        base.PostClose();
    }
    public override void DoWindowContents(Rect inRect) => page.Draw(inRect, showCreate: false);
}
