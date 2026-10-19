using System;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;

namespace AIRsLight.ClashOfRim.Modules.World;

// One window owns one form. Closing or losing its original session/Game cannot
// turn its saved credentials into an entry request for a later session.
internal sealed class ClientWorldEntryForm
{
    private readonly ClientWorldEntryStart entry;
    private readonly IClientWorldEntryPresentation view;
    private readonly ClientGameRequestTracker lifetime;
    private readonly ClientGameRequestTracker.Scope owner;
    private readonly bool registration;
    private bool closed, submitting;
    internal string ServerBaseUrl, UserId, Password = "", PasswordConfirmation = "";

    internal ClientWorldEntryForm(ClientSessionLifecycle session, IClientSnapshotGame game,
        ClientWorldEntryStart entry, IClientWorldEntryPresentation view, bool registration,
        string? server = null, string? user = null)
    {
        this.entry = entry; this.view = view; this.registration = registration;
        lifetime = new(session, game, ClearPasswords);
        owner = lifetime.Capture();
        ServerBaseUrl = server ?? owner.Context.ServerBaseUrl;
        if (!registration && string.IsNullOrWhiteSpace(ServerBaseUrl)) ServerBaseUrl = "http://127.0.0.1:5000";
        UserId = user ?? owner.Context.UserId;
    }

    internal bool IsCurrent
    {
        get
        {
            if (closed) return false;
            if (lifetime.TryApply(owner, static () => { })) return true;
            Close(); return false;
        }
    }
    internal void Close()
    {
        using var p = ClientSessionWork.EnterPublication();
        closed = true; ClearPasswords();
    }
    internal bool Submit()
    {
        using var p = ClientSessionWork.EnterPublication();
        if (submitting || !IsCurrent) return false;
        submitting = true;
        try
        {
            string? invalid = registration && string.IsNullOrEmpty(Password) ? "ClashOfRim.ServerEntry.PasswordRequired"
                : registration && !string.Equals(Password, PasswordConfirmation, StringComparison.Ordinal)
                    ? "ClashOfRim.ServerEntry.PasswordMismatch" : null;
            if (invalid is not null)
            {
                string message = view.Text(invalid);
                if (IsCurrent) view.Reject(message);
                return false;
            }
            // Entry owns normalization, admission, identity changes and network work.
            // Its successful transition invalidates this form but not its continuation.
            bool accepted = entry.TryStart(ServerBaseUrl, UserId, Password, registration);
            if (accepted) Close();
            else _ = IsCurrent; // Clear secrets if entry failed after changing identity.
            return accepted;
        }
        finally { submitting = false; }
    }
    private void ClearPasswords() => Password = PasswordConfirmation = "";
}
