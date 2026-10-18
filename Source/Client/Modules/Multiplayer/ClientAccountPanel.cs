using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Accounts;
using AIRsLight.ClashOfRim.Modules.Sessions;

namespace AIRsLight.ClashOfRim.Modules.Multiplayer;

internal sealed class ClientAccountPanel
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientPasswordChange passwords;
    private readonly ClientRequestTracker<bool> lifetime;
    private ClientAccountDraft draft = new();

    internal ClientAccountPanel(ClientSessionLifecycle session, ClientPasswordChange passwords)
    {
        this.session = session; this.passwords = passwords;
        // Drafts belong to the account session, independently of Game/snapshot changes.
        lifetime = new(session, () => session.IsUnavailable, unavailable => unavailable == session.IsUnavailable, () =>
        { draft.Clear(); draft = new(); });
    }
    internal string UserId => lifetime.Read(() => session.ReadContext().UserId);
    internal ClientAccountDraft Read() => lifetime.Read(() => draft);
    internal bool Busy => passwords.Busy;
    internal string Status => passwords.Message;
    internal void Close() => lifetime.Reset();

    internal Task SubmitAsync(ClientAccountDraft submitted) => lifetime.Read(() =>
    {
        if (!ReferenceEquals(draft, submitted) || passwords.Busy) return Task.CompletedTask;
        string current = draft.CurrentPassword, next = draft.NewPassword;
        draft.Clear();
        return passwords.StartAsync(current, next);
    });
}

// The displayed draft is also the submit target. A closed/replaced page cannot
// accidentally submit empty credentials for the new account after normalization.
internal sealed class ClientAccountDraft
{
    internal string CurrentPassword = "", NewPassword = "";
    internal void Clear() => CurrentPassword = NewPassword = "";
}
