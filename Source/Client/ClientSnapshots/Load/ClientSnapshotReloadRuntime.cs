using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.Snapshots;
using Verse;

namespace AIRsLight.ClashOfRim.ClientSnapshots;

internal sealed class ClientSnapshotReloadRuntime : IClientSnapshotReloadRuntime
{
    public void Enqueue(Action action, Action discarded) => ClientMainThreadDispatcher.Enqueue(action, discarded);
    public IClientSnapshotLoadOperation Bind() => new Operation();
    private sealed class Operation : IClientSnapshotLoadOperation
    {
        private Game? owner = Current.Game;
        private ServerSessionGameDataLoader.PendingServerSessionSave? pending;
        public bool IsCurrent => ReferenceEquals(Current.Game, owner);
        public bool MutationStarted { get; private set; }
        public IClientSnapshotGameSession Game => new ClientSnapshotGame().BindCurrentGame();
        public async Task<bool> LoadAsync(ClientRestoredSnapshot snapshot, Func<Action, bool> apply, CancellationToken token)
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = token.Register(() => completion.TrySetCanceled(token));
            token.ThrowIfCancellationRequested();
            pending = ServerSessionGameDataLoader.LoadOwned(snapshot.SaveBytes, snapshot.SaveName, apply,
                game => owner = game, () => MutationStarted = true,
                () => completion.TrySetResult(true), ex => completion.TrySetException(ex), () => completion.TrySetResult(false));
            try { return await completion.Task.ConfigureAwait(false); }
            finally { pending.Dispose(); }
        }
        public void Dispose() => pending?.Dispose();
    }
}
