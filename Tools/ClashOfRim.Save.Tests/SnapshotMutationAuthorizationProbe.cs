using System.Text.Json;
using AIRsLight.ClashOfRim.Network;

internal static class SnapshotMutationAuthorizationProbe
{
    internal static void RevokedWhileWaiting(ClashOfRimNetworkState state, string user, string colony,
        bool replaceSession, Func<bool> execute)
    {
        string[] users = state.Ledger.ListAll().SelectMany(item => new[] { item.Actor.UserId, item.Target.UserId })
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        string Snapshot() => JsonSerializer.Serialize(state.Ledger.ListAll())
            + JsonSerializer.Serialize(users.Select(state.EventNotifications.GetVersion).ToArray());
        string before = Snapshot();
        bool? accepted = null; Exception? error = null;
        var thread = new Thread(() =>
        {
            try { accepted = execute(); }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true };
        bool waiting;
        lock (state.RaidSettlementSnapshotMutationGate)
        {
            thread.Start();
            waiting = SpinWait.SpinUntil(() => (thread.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5));
            if (replaceSession)
                state.LoginSessions.TryCreate(user, colony, DateTimeOffset.UtcNow, out _);
            else
                state.LoginSessions.EndUser(user);
        }
        if (!thread.Join(TimeSpan.FromSeconds(5))) throw new Exception("mutation did not release business lock");
        if (!waiting || error is not null) throw new Exception("mutation did not wait on business lock", error);
        if (accepted != false || before != Snapshot())
            throw new Exception("revoked session executed mutation after lock wait");
    }
}
