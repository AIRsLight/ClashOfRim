using System;
using System.Linq;
using AIRsLight.ClashOfRim.Modules.World;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim;

internal class ClientWorldWritePresentation : IClientWorldWritePresentation, IClientWorldWriteRuntime, IClientWorldSynchronizationPresentation
{
    private readonly Action<string> status;
    internal ClientWorldWritePresentation(Action<string> status) => this.status = status;
    public bool IsObserving => ClashOfRimGameComponent.HasActiveObservationSession;
    public string Text(string key, params (string Name, object? Value)[] arguments)
        => ClashOfRimText.Key(key, arguments.Select(argument => (argument.Value ?? "").Named(argument.Name)).ToArray());
    public static string DescribeCaptureFailure(ClientWorldBaselineCapture.Failure failure)
        => failure.MessageKey is { } key ? ClashOfRimText.Key(key) : failure.Detail ?? "Unknown";
    public string CaptureFailure(ClientWorldBaselineCapture.Failure failure) => DescribeCaptureFailure(failure);
    public void Diagnose(string message) => Log.Warning("[ClashOfRim] " + message);
    public void Status(string text) => status(text);
    public void Message(string text, ClientWorldWriteMessage kind) => Messages.Message(text,
        kind == ClientWorldWriteMessage.Reject ? MessageTypeDefOf.RejectInput : kind == ClientWorldWriteMessage.Progress ? MessageTypeDefOf.NeutralEvent : MessageTypeDefOf.PositiveEvent, historical: false);
    public void Enqueue(Action action, Action discard) => ClientMainThreadDispatcher.Enqueue(action, discard);
}
