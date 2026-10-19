using System;
using System.Collections.Generic;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Chat;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.Chat;

internal sealed class ClientChatOverlay
{
    internal static ClientChatOverlay? Current { get; private set; }
    internal static void Attach(ClientChatOverlay? value) => Current = value;
    private readonly ClientChatOverlayModel model;
    internal ClientChatOverlay(ClientChatOverlayModel model) => this.model = model;
    private bool Owns(Game game, ClientChatOverlayModel.View view)
        => view.IsCurrent && ReferenceEquals(Current, this) && ReferenceEquals(game, Verse.Current.Game);
    internal void Draw(Game game)
    {
        if (!ReferenceEquals(Current, this) || !ReferenceEquals(game, Verse.Current.Game)
            || Verse.Current.ProgramState != ProgramState.Playing || WorldRendererUtility.WorldSelected) return;
        var view = model.Read();
        if (view is null || !Owns(game, view)) return;
        _ = view.PollAsync(Time.realtimeSinceStartup);
        if (!Owns(game, view)) return;

        float top = ClashOfRimGameComponent.ActiveRaidBattleSession is null ? 12f : 128f;
        int unread = view.Unread;
        if (view.Minimized)
        {
            Rect minimizedRect = new(UI.screenWidth - 100f, top, 88f, 28f);
            string label = unread > 0
                ? ClashOfRimText.Key("ClashOfRim.Chat.MinimizedWithUnread", unread.Named("COUNT"))
                : ClashOfRimText.Key("ClashOfRim.Chat.Minimized");
            if (Widgets.ButtonText(minimizedRect, label) && Owns(game, view))
            {
                view.Minimized = false;
            }

            return;
        }

        if (!Owns(game, view)) return;
        view.MarkRead();
        Rect rect = new(UI.screenWidth - 388f, top, 376f, 316f);
        Widgets.DrawWindowBackground(rect);
        Rect inner = rect.ContractedBy(10f);
        Text.Font = GameFont.Small;
        Text.Anchor = TextAnchor.UpperLeft;
        Widgets.Label(new Rect(inner.x, inner.y, inner.width - 72f, 24f), ClashOfRimText.Key("ClashOfRim.Chat.Title"));
        if (Widgets.ButtonText(new Rect(inner.xMax - 66f, inner.y, 66f, 24f), ClashOfRimText.Key("ClashOfRim.Chat.Minimize")) && Owns(game, view))
        {
            view.Minimized = true;
            return;
        }

        IReadOnlyList<ModChatMessageDto> messages = view.Messages;
        Rect outRect = new(inner.x, inner.y + 30f, inner.width, 214f);
        Rect viewRect = new(0f, 0f, outRect.width - 16f, Math.Max(outRect.height, messages.Count * 44f + 8f));
        if (messages.Count != view.LastMessageCount)
        {
            view.LastMessageCount = messages.Count;
            view.ScrollY = viewRect.height;
        }

        var scroll = new Vector2(view.ScrollX, view.ScrollY);
        Widgets.BeginScrollView(outRect, ref scroll, viewRect);
        view.ScrollX = scroll.x; view.ScrollY = scroll.y;
        float y = 0f;
        Text.Font = GameFont.Tiny;
        foreach (ModChatMessageDto message in messages)
        {
            bool incomingPrivate = string.Equals(message.Channel, "Private", StringComparison.Ordinal)
                && string.Equals(message.TargetUserId, view.UserId, StringComparison.Ordinal);
            string channel = string.Equals(message.Channel, "Private", StringComparison.Ordinal)
                ? ClashOfRimText.Key("ClashOfRim.Chat.PrivateChannel")
                : ClashOfRimText.Key("ClashOfRim.Chat.PublicChannel");
            string header = string.Equals(message.Channel, "Private", StringComparison.Ordinal)
                ? ClashOfRimText.Key(
                    "ClashOfRim.Chat.PrivateHeader",
                    message.FromUserId.Named("FROM"),
                    (message.TargetUserId ?? string.Empty).Named("TO"))
                : ClashOfRimText.Key("ClashOfRim.Chat.PublicHeader", message.FromUserId.Named("FROM"));
            if (incomingPrivate)
            {
                GUI.color = new Color(1f, 0.86f, 0.58f);
            }

            Widgets.Label(new Rect(0f, y, viewRect.width, 18f), channel + " " + header);
            GUI.color = Color.white;
            Widgets.Label(new Rect(0f, y + 18f, viewRect.width, 34f), message.Text ?? string.Empty);
            y += 44f;
        }

        Text.Font = GameFont.Small;
        Widgets.EndScrollView();

        Rect inputRect = new(inner.x, inner.yMax - 64f, inner.width - 72f, 30f);
        view.Input = Widgets.TextField(inputRect, view.Input ?? string.Empty);
        bool canSend = view.CanSend;
        if (!canSend)
        {
            GUI.color = Color.gray;
        }

        if (Widgets.ButtonText(new Rect(inner.xMax - 64f, inner.yMax - 64f, 64f, 30f), ClashOfRimText.Key("ClashOfRim.Chat.Send")) && canSend && Owns(game, view))
        {
            _ = view.SendAsync();
        }

        GUI.color = Color.white;
        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(inner.x, inner.yMax - 30f, inner.width, 28f), view.Status);
        Text.Font = GameFont.Small;
        Text.Anchor = TextAnchor.UpperLeft;
    }

}
