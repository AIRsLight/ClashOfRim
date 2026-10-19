using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using AIRsLight.ClashOfRim.Modules.Multiplayer;

namespace AIRsLight.ClashOfRim.Modules.Chat;

// The overlay draft belongs to a Game and login. The conversation itself keeps
// ClientChat's account lifetime, including its existing cursor and unread rules.
internal sealed class ClientChatOverlayModel
{
    private readonly ClientChat chat;
    private readonly ClientSessionLifecycle session;
    private readonly Func<bool> activeWorld;
    private readonly ClientGameRequestTracker lifetime;
    private View? current;
    internal ClientChatOverlayModel(ClientChat chat, ClientSessionLifecycle session, IClientSnapshotGame game, Func<bool> activeWorld)
    {
        this.chat = chat; this.session = session; this.activeWorld = activeWorld;
        lifetime = new(session, game, () => { current?.Clear(); current = null; });
    }
    internal View? Read()
    {
        var scope = lifetime.Capture();
        View? result = null;
        Apply(scope, () => result = current ??= new(this, scope));
        return result;
    }
    private bool Apply(ClientGameRequestTracker.Scope scope, Action action)
    {
        using var publication = ClientSessionWork.EnterPublication();
        bool available = false;
        lifetime.TryApply(scope, () => available = activeWorld());
        return available && !session.IsUnavailable && lifetime.TryApply(scope, action);
    }
    internal sealed class View
    {
        private readonly ClientChatOverlayModel owner;
        private readonly ClientGameRequestTracker.Scope scope;
        private int cachedVersion = -1;
        private IReadOnlyList<ModChatMessageDto> cached = Array.Empty<ModChatMessageDto>();
        private readonly PanelRefreshSchedule refresh = new(10f);
        internal bool Minimized = true;
        internal string Input = "";
        internal float ScrollX, ScrollY;
        internal int LastMessageCount;
        internal View(ClientChatOverlayModel owner, ClientGameRequestTracker.Scope scope) { this.owner = owner; this.scope = scope; }
        internal bool IsCurrent => owner.Apply(scope, static () => { });
        internal string UserId => scope.Context.UserId;
        internal bool CanSend => IsCurrent && !owner.chat.Busy && !string.IsNullOrWhiteSpace(Input);
        internal int Unread => owner.chat.UnreadPrivate;
        internal string Status => owner.chat.Status;
        internal IReadOnlyList<ModChatMessageDto> Messages
        {
            get
            {
                IReadOnlyList<ModChatMessageDto> result = Array.Empty<ModChatMessageDto>();
                owner.Apply(scope, () =>
                {
                    int version = owner.chat.Version;
                    if (version != cachedVersion) { cached = owner.chat.CopyMessages(); cachedVersion = version; }
                    result = cached;
                });
                return result;
            }
        }
        internal Task PollAsync(float now)
        {
            Task work = Task.CompletedTask;
            owner.Apply(scope, () =>
            {
                if (!refresh.TryStart(now, busy: false)) return;
                work = owner.chat.RefreshAsync();
            });
            return work;
        }
        internal void MarkRead() => owner.Apply(scope, owner.chat.MarkPrivateRead);
        internal Task SendAsync()
        {
            Task work = Task.CompletedTask;
            owner.Apply(scope, () =>
            {
                if (owner.chat.Busy || string.IsNullOrWhiteSpace(Input)) return;
                string text = Input; Input = "";
                work = owner.chat.SendAsync(text);
            });
            return work;
        }
        internal void Clear() { Input = ""; cached = Array.Empty<ModChatMessageDto>(); }
    }
}
