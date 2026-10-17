using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Events;

// Coalesces automatic notifications and deferrals. Completion belongs to the exact
// attempt and identity; it cannot release or restart a replacement session's work.
internal sealed class ClientEventRefreshQueue
{
    private readonly ClientEventReader reader;
    private ClientEventReader.ReadScope? owner;
    private Attempt? active;
    private bool queued;

    internal ClientEventRefreshQueue(ClientEventReader reader) => this.reader = reader;

    internal Attempt? Begin()
    {
        using var publication = ClientSessionWork.EnterPublication();
        Normalize();
        if (active is not null) { queued = true; return null; }
        queued = false;
        return active = new Attempt(owner!);
    }

    internal void Defer()
    {
        using var publication = ClientSessionWork.EnterPublication();
        Normalize();
        queued = true;
    }

    internal bool TakeDeferred()
    {
        using var publication = ClientSessionWork.EnterPublication();
        Normalize();
        if (!queued || active is not null) return false;
        queued = false;
        return true;
    }

    internal bool Complete(Attempt attempt)
    {
        bool repeat = false;
        ClientSessionWork.TryApply(() =>
        {
            if (!ReferenceEquals(active, attempt) || !reader.Owns(attempt.Owner)) return;
            active = null;
            repeat = queued;
            queued = false;
        });
        return repeat;
    }

    private void Normalize()
    {
        if (owner is not null && reader.Owns(owner)) return;
        owner = reader.CaptureScope();
        active = null;
        queued = false;
    }

    internal sealed class Attempt
    {
        internal Attempt(ClientEventReader.ReadScope owner) => Owner = owner;
        internal ClientEventReader.ReadScope Owner { get; }
    }
}
