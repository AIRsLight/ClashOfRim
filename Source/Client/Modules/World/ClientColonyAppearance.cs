using System;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;

namespace AIRsLight.ClashOfRim.Modules.World;

internal sealed record ClientColonyAppearanceAccount(string Server, string World, string User, string Colony);
internal interface IClientColonyAppearanceStore
{
    ClientColonyAppearanceAccount ReadAccount();
    string? Read(string key);
    void Write(string key, string? packed);
    void Persist();
}
internal interface IClientColonyAppearanceTarget
{
    bool IsCurrent { get; }
    bool IsAvailable { get; }
    string Label { get; }
}
internal interface IClientColonyAppearancePresentation : IClientWorldPreparationPresentation
{
    void Open(string colony, ColonyAppearanceSelection current, Action<ColonyAppearanceSelection> save);
    void Message(string message, ClientWorldWriteMessage kind);
}

// Account-scoped preferences remain in the existing persistent store. The module
// owns their encoding, writes and publication; dialogs retain the original owner.
internal sealed class ClientColonyAppearance
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientRequestTracker<string> scopes;
    private readonly IClientColonyAppearanceStore store;
    private readonly IClientColonyAppearancePresentation view;
    private object? unpersisted;
    private object editRevision = new();
    internal event Action<ModColonyAppearanceDto?, string>? RegistrationRequested;
    internal ClientColonyAppearance(ClientSessionLifecycle session, IClientColonyAppearanceStore store, IClientColonyAppearancePresentation view)
    {
        this.session = session; this.store = store; this.view = view;
        scopes = new(session, () => ClientColonyAppearanceCodec.AccountKey(store.ReadAccount()),
            key => key == ClientColonyAppearanceCodec.AccountKey(store.ReadAccount()), static () => { });
    }
    internal ColonyAppearanceSelection Read()
    {
        using var p = ClientSessionWork.EnterPublication();
        var scope = scopes.Capture(); var selection = Read(scope);
        return scopes.TryApply(scope, static () => { }) ? selection : ColonyAppearanceSelection.Empty;
    }
    internal ModColonyAppearanceDto? Capture() => Read().ToDto();
    private ColonyAppearanceSelection Read(ClientRequestTracker<string>.Scope scope)
        => scope.Boundary.Length == 0 ? ColonyAppearanceSelection.Empty : ClientColonyAppearanceCodec.Decode(store.Read(scope.Boundary));

    internal void Open(IClientColonyAppearanceTarget target)
    {
        using var p = ClientSessionWork.EnterPublication();
        var scope = scopes.Capture();
        bool Current() => scope.Boundary.Length != 0 && !session.IsUnavailable && scopes.TryApply(scope, static () => { }) && target.IsCurrent;
        if (!Current() || !target.IsAvailable) return;
        var selected = Read(scope); string label = target.Label;
        if (!Current()) return;
        bool consumed = false;
        view.Open(label, selected, selection =>
        {
            using var edit = ClientSessionWork.EnterPublication();
            if (consumed) return; consumed = true;
            if (!Current() || !target.IsAvailable || !Current()) return;
            try { Save(scope, selection, () => Current() && target.IsAvailable && Current()); }
            catch { if (Current()) consumed = false; throw; }
        });
    }
    internal void SaveCurrent(ColonyAppearanceSelection selection)
    {
        using var p = ClientSessionWork.EnterPublication();
        var scope = scopes.Capture(); Save(scope, selection, static () => true);
    }
    private void Save(ClientRequestTracker<string>.Scope scope, ColonyAppearanceSelection selection, Func<bool> targetCurrent)
    {
        if (!scopes.TryApply(scope, static () => { })) return;
        var request = scopes.Begin(replace: true)!;
        bool Current() => scope.Boundary.Length != 0 && !session.IsUnavailable && targetCurrent() && scopes.TryApply(request, static () => { });
        bool Step(Action action) { if (!Current()) return false; action(); return Current(); }
        try
        {
            if (!Current()) return;
            // Even an explicit reset or saving the same value is a new local intent.
            editRevision = new();
            if (!Store(scope, selection, Current)) return;
            string reason = "";
            if (!Step(() => reason = view.Text("ClashOfRim.ColonyAppearance.RegistrationReason"))) return;
            foreach (Action<ModColonyAppearanceDto?, string> handler in RegistrationRequested?.GetInvocationList() ?? Array.Empty<Delegate>())
                if (!Step(() => handler(selection.ToDto(), reason))) return;
            string message = "";
            if (Step(() => message = view.Text("ClashOfRim.ColonyAppearance.SavedMessage")))
                Step(() => view.Message(message, ClientWorldWriteMessage.Success));
        }
        finally { scopes.End(request); }
    }
    // The publishing source is responsible for validating its server receipt.
    // Empty server appearance preserves the existing local preference, as before.
    internal void ObserveServer(ModColonyAppearanceDto? appearance)
        => ObserveServer(CaptureObservation(), appearance);

    internal sealed record Observation(ClientRequestTracker<string>.Scope Scope, ClientColonyAppearanceAccount Account, object Revision);
    internal Observation CaptureObservation()
    {
        using var p = ClientSessionWork.EnterPublication();
        return new(scopes.Capture(), store.ReadAccount(), editRevision);
    }
    // Only a validated publication that itself changed the server world may rebind.
    internal Observation? AdoptWorld(Observation observation, string world)
    {
        using var p = ClientSessionWork.EnterPublication();
        if (!ReferenceEquals(editRevision, observation.Revision) || !session.Owns(observation.Scope.Owner, observation.Scope.Context, false)
            || ClientColonyAppearanceCodec.AccountKey(observation.Account with { World = world }) != ClientColonyAppearanceCodec.AccountKey(store.ReadAccount())) return null;
        return observation with { Scope = scopes.Capture(), Account = store.ReadAccount() };
    }
    internal void ObserveServer(Observation observation, ModColonyAppearanceDto? appearance, Func<bool>? current = null)
    {
        using var p = ClientSessionWork.EnterPublication();
        var selection = ColonyAppearanceSelection.FromDto(appearance);
        bool Owned() => ReferenceEquals(editRevision, observation.Revision) && current?.Invoke() != false
            && !session.IsUnavailable && scopes.TryApply(observation.Scope, static () => { });
        if (!selection.HasAny || !Owned()) return;
        var request = scopes.Begin(replace: true)!; var scope = observation.Scope;
        try { Store(scope, selection, () => scope.Boundary.Length != 0 && Owned() && scopes.TryApply(request, static () => { })); }
        finally { scopes.End(request); }
    }
    private bool Store(ClientRequestTracker<string>.Scope scope, ColonyAppearanceSelection selection, Func<bool> current)
    {
        if (!current()) return false;
        string? packed = selection.HasAny ? ClientColonyAppearanceCodec.Encode(selection) : null;
        string? previous = store.Read(scope.Boundary);
        if (!current()) return false;
        if (previous != packed)
        {
            unpersisted = new object();
            store.Write(scope.Boundary, packed);
            if (!current()) return false;
        }
        if (unpersisted is { } pending)
        {
            store.Persist();
            // Failed persistence remains retryable even when the dictionary already
            // contains the requested value. A reentrant write owns its newer ticket.
            if (ReferenceEquals(unpersisted, pending)) unpersisted = null;
        }
        return current();
    }
}

internal sealed record ColonyAppearanceSelection
{
    internal static readonly ColonyAppearanceSelection Empty = new(null, null, null, null);
    internal ColonyAppearanceSelection(string? mode, string? iconDefName, string? colorDefName, string? colorHex)
    { Mode = mode ?? ""; IconDefName = iconDefName ?? ""; ColorDefName = colorDefName ?? ""; ColorHex = colorHex ?? ""; }
    public string Mode { get; }
    public string IconDefName { get; }
    public string ColorDefName { get; }
    public string ColorHex { get; }
    public bool HasAny => !string.IsNullOrWhiteSpace(Mode) || !string.IsNullOrWhiteSpace(IconDefName)
        || !string.IsNullOrWhiteSpace(ColorDefName) || !string.IsNullOrWhiteSpace(ColorHex);
    internal static ColonyAppearanceSelection FromDto(ModColonyAppearanceDto? source)
        => source is null ? Empty : new(source.Mode, source.IconDefName, source.ColorDefName, source.ColorHex);
    internal ModColonyAppearanceDto? ToDto() => !HasAny ? null : new()
    { Mode = Value(Mode), IconDefName = Value(IconDefName), ColorDefName = Value(ColorDefName), ColorHex = Value(ColorHex) };
    private static string? Value(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
