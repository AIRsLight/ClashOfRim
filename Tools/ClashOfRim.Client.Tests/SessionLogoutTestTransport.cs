using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Sessions;

using static SessionReconnectTestTransport;

internal sealed class SessionLogoutTestTransport : IClientSessionLogoutTransport
{
    internal readonly List<(ClashOfRimClientNetworkContext Context, string Session)> Calls = new();
    internal bool Pause, Fails;
    internal readonly TaskCompletionSource<bool> Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal readonly TaskCompletionSource<bool> Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task<ClashOfRimClientNetworkResult<ModLogoutResponseDto>> LogoutAsync(ClashOfRimClientNetworkContext context, string sessionId)
    {
        Calls.Add((context, sessionId)); Started.TrySetResult(true);
        if (Pause) await Continue.Task;
        if (Fails) throw new InvalidOperationException("cleanup failure");
        return ClashOfRimClientNetworkResult<ModLogoutResponseDto>.Ok(new() { Result = Accepted() });
    }
}
