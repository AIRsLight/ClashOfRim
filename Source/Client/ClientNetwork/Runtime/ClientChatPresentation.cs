using System;
using System.Linq;
using AIRsLight.ClashOfRim.Modules.Chat;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim.ClientNetwork;

internal sealed class ClientChatPresentation : IClientChatPresentation
{
    public string Text(string key, params (string Name, object? Value)[] args) => ClashOfRimText.Key(key, args.Select(a => a.Value.Named(a.Name)).ToArray());
    public void Enqueue(Action action, Action discard) => ClientMainThreadDispatcher.EnqueueSession(action, discard);
    public void Reject(string text) => Messages.Message(text, MessageTypeDefOf.RejectInput, historical: false);
    public void Diagnose(Exception error) => Log.Warning("[ClashOfRim] Chat failed: " + error);
}
