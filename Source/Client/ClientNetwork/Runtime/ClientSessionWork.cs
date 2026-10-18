using System;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.CompilerServices;

namespace AIRsLight.ClashOfRim.ClientNetwork;

/// <summary>Owns asynchronous work across login/exit boundaries, independently of the game engine.</summary>
public static class ClientSessionWork
{
    private static readonly object Gate = new();
    private static readonly AsyncLocal<CancellationToken?> Ambient = new();
    private static volatile CancellationTokenSource current = new();

    public static CancellationToken Token => Ambient.Value ?? current.Token;
    public static bool IsCurrent => Token == current.Token && !Token.IsCancellationRequested;

    public static void Reset() => TryReset(static () => true, out _);

    internal static bool TryReset(Func<bool> owns, out CancellationToken replacement)
    {
        CancellationTokenSource previous;
        lock (Gate)
        {
            replacement = default;
            if (!owns()) return false;
            previous = current;
            current = new CancellationTokenSource();
            replacement = current.Token;
            Ambient.Value = null;
        }
        // Cancel outside the gate: callbacks may wait for network completions.
        previous.Cancel();
        // Tokens can still be registered by finishing work, so do not dispose the old source here.
        return true;
    }

    public static Task Run(Func<Task> action)
    {
        CancellationToken token = Token;
        return Task.Run(async () =>
        {
            Ambient.Value = token;
            try
            {
                ThrowIfExpired();
                await action();
            }
            catch (Exception) when (!IsCurrent)
            {
                // The owner exited. Publication and completion use the same boundary as Reset.
            }
        });
    }

    public static async Task<T> Observe<T>(Task<T> task)
    {
        T result = await task;
        ThrowIfExpired();
        return result;
    }

    public static async Task Observe(Task task)
    {
        await task;
        ThrowIfExpired();
    }

    public static async Task<T> Observe<T>(ConfiguredTaskAwaitable<T> task)
    {
        T result = await task;
        ThrowIfExpired();
        return result;
    }

    public static async Task Observe(ConfiguredTaskAwaitable task)
    {
        await task;
        ThrowIfExpired();
    }

    public static Action Guard(Action action, Action? onDiscard = null)
    {
        CancellationToken token = Token;
        return () =>
        {
            // Queued game actions run on the same main thread as session exit/login.
            if (token != current.Token || token.IsCancellationRequested)
            {
                onDiscard?.Invoke();
                return;
            }
            CancellationToken? previous = Ambient.Value;
            Ambient.Value = token;
            try { action(); }
            finally { Ambient.Value = previous; }
        };
    }

    public static bool TryApply(Action action)
    {
        lock (Gate)
        {
            if (!IsCurrent) return false;
            action();
            return true;
        }
    }

    /// <summary>
    /// Publishes a synchronous response under its session owner. Never await or wait for the
    /// main thread while holding this scope. Lock order: session publication, then cache locks.
    /// </summary>
    public static IDisposable EnterPublication()
    {
        Monitor.Enter(Gate);
        try
        {
            ThrowIfExpired();
            return new Publication();
        }
        catch
        {
            Monitor.Exit(Gate);
            throw;
        }
    }

    private sealed class Publication : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Monitor.Exit(Gate);
        }
    }

    private static void ThrowIfExpired()
    {
        if (!IsCurrent) throw new OperationCanceledException("The multiplayer session ended.", Token);
    }
}
