using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Events;

// Owns the session's event cache and transient execution bookkeeping. It does not
// apply game effects or confirm snapshots. Lock order: session publication, then gate.
internal sealed class ClientEventInbox
{
    private readonly object gate = new();
    private readonly List<string> queuedIds = new();
    private readonly Dictionary<string, string> groups = new(StringComparer.Ordinal);
    private readonly List<ModEventDetailDto> details = new();
    private int detailsVersion;
    private readonly HashSet<(string EventId, ClientEventEffect Kind)> effects = new();
    private readonly List<string> pendingConfirmations = new();

    internal void ReplaceQueue(ModEventQueueSummaryDto? queue)
    {
        using var publication = ClientSessionWork.EnterPublication();
        lock (gate)
        {
            queuedIds.Clear();
            groups.Clear();
            if (queue is null) return;
            AddGroup(queue.DirectlyProcessable, "DirectlyProcessable");
            AddGroup(queue.WaitingForUserChoice, "WaitingForUserChoice");
            AddGroup(queue.DeliveredUnconfirmed, "DeliveredUnconfirmed");
            AddGroup(queue.Conflicts, "Conflicts");
            AddGroup(queue.Rejected, "Rejected");
        }
    }

    private void AddGroup(IReadOnlyCollection<ModEventReferenceDto>? references, string group)
    {
        if (references is null) return;
        foreach (var reference in references)
        {
            if (string.IsNullOrWhiteSpace(reference.EventId)) continue;
            // Keep first-occurrence queue order and the last reported group, as before.
            if (!groups.ContainsKey(reference.EventId)) queuedIds.Add(reference.EventId);
            groups[reference.EventId] = group;
        }
    }

    internal IReadOnlyList<string> CopyQueuedIds()
    {
        lock (gate) return Array.AsReadOnly(queuedIds.ToArray());
    }

    internal string? GroupOf(string eventId)
    {
        lock (gate) return groups.TryGetValue(eventId, out string? group) ? group : null;
    }

    internal bool IsInGroup(string eventId, string expectedGroup) =>
        !string.IsNullOrWhiteSpace(eventId) && string.Equals(GroupOf(eventId), expectedGroup, StringComparison.Ordinal);

    // A confirmed event can still have cached details or an open letter. Removing its
    // actionable group preserves those diagnostics without allowing the old action again.
    internal void ForgetGroup(string eventId)
    {
        using var publication = ClientSessionWork.EnterPublication();
        lock (gate) groups.Remove(eventId);
    }

    internal void ReplaceDetails(IReadOnlyCollection<ModEventDetailDto>? incoming)
    {
        using var publication = ClientSessionWork.EnterPublication();
        lock (gate)
        {
            detailsVersion++;
            details.Clear();
            if (incoming is not null) details.AddRange(incoming.Select(detail => detail.Copy()));
        }
    }

    // Both collection and nested DTOs are detached; callers cannot mutate stored events.
    internal IReadOnlyList<ModEventDetailDto> CopyDetails() => CopyDetails(null, out _)!;

    // Revision only describes cached detail data; it neither owns nor resets a session.
    internal IReadOnlyList<ModEventDetailDto>? CopyDetails(int? knownVersion, out int version)
    {
        lock (gate)
        {
            version = detailsVersion;
            return knownVersion == version ? null : Array.AsReadOnly(details.Select(detail => detail.Copy()).ToArray());
        }
    }

    internal ModEventDetailDto? FindDetail(string eventId)
    {
        lock (gate) return details.FirstOrDefault(detail => string.Equals(detail.EventId, eventId, StringComparison.Ordinal))?.Copy();
    }

    internal bool HasEffect(string eventId, ClientEventEffect kind)
    {
        lock (gate) return effects.Contains((eventId, kind));
    }

    internal bool TryMarkEffect(string eventId, ClientEventEffect kind)
    {
        using var publication = ClientSessionWork.EnterPublication();
        lock (gate) return effects.Add((eventId, kind));
    }

    internal int PendingConfirmationCount { get { lock (gate) return pendingConfirmations.Count; } }
    internal string? FirstPendingConfirmation { get { lock (gate) return pendingConfirmations.FirstOrDefault(); } }

    internal bool HasPendingConfirmation(string eventId)
    {
        lock (gate) return pendingConfirmations.Contains(eventId, StringComparer.Ordinal);
    }

    internal bool TryTrackPendingConfirmation(string eventId)
    {
        using var publication = ClientSessionWork.EnterPublication();
        lock (gate)
        {
            if (pendingConfirmations.Contains(eventId, StringComparer.Ordinal)) return false;
            pendingConfirmations.Add(eventId);
            return true;
        }
    }

    internal void ForgetPendingConfirmation(string eventId)
    {
        using var publication = ClientSessionWork.EnterPublication();
        lock (gate) pendingConfirmations.RemoveAll(id => string.Equals(id, eventId, StringComparison.Ordinal));
    }

    internal void Reset()
    {
        using var publication = ClientSessionWork.EnterPublication();
        lock (gate)
        {
            queuedIds.Clear();
            groups.Clear();
            detailsVersion++;
            details.Clear();
            effects.Clear();
            pendingConfirmations.Clear();
        }
    }
}

internal enum ClientEventEffect { Letter, ServerNotification, Diplomacy }
