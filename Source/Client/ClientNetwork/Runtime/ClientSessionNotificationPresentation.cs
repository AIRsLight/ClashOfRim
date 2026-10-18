using System;
using System.Linq;
using AIRsLight.ClashOfRim.Modules.Presence;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientSessionNotificationPresentation : IClientSessionNotificationPresentation
{
    private readonly Action<string> status;
    internal ClientSessionNotificationPresentation(Action<string> status) => this.status = status;
    public string Text(string key, params (string Name, object? Value)[] args)
        => ClashOfRimText.Key(key, args.Select(a => (a.Value is string text ? ClashOfRimText.SafeArgument(text) : a.Value).Named(a.Name)).ToArray());
    public void Status(string text) => status(text);
    public void Enqueue(Action action, Action discard) => ClientMainThreadDispatcher.EnqueueSession(action, discard);
    public void Message(string text) => Messages.Message(text, MessageTypeDefOf.NeutralEvent, historical: false);
    public void Diagnose(string eventName, Exception error) => Log.Warning("[ClashOfRim] Session stream event failed: event=" + eventName + ", error=" + error);
}
