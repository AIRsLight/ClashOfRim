using System;
using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Bank;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.Snapshots;

namespace AIRsLight.ClashOfRim.Modules.Mercenaries;

internal sealed class ClientMercenaryQuotes
{
    private readonly ClientSessionLifecycle session;
    private readonly IClientSnapshotGame game;
    private readonly ClientBankState bank;
    private readonly IClientMercenaryQuoteTransport transport;
    private readonly IClientMercenaryPresentation presentation;
    private readonly Func<long> readTicks;

    internal ClientMercenaryQuotes(ClientSessionLifecycle session, IClientSnapshotGame game, ClientBankState bank,
        IClientMercenaryQuoteTransport transport, IClientMercenaryPresentation presentation, Func<long> readTicks)
    { this.session = session; this.game = game; this.bank = bank; this.transport = transport; this.presentation = presentation; this.readTicks = readTicks; }

    internal Task QuoteAsync(string key, string skill, int level, int days, Action<string, string, ModMercenaryQuoteResponseDto?>? callback) =>
        ExecuteAsync(key, callback, (context, ticks, token) => transport.QuoteAsync(context, ticks, skill, level, days, token),
            r => r.Result, r => r.BankStatus, r => presentation.Text("PriceLine", ("PRICE", r.PriceSilver)));

    internal Task QuoteGuardAsync(string key, string tier, Action<string, string, ModMercenaryGuardQuoteResponseDto?>? callback) =>
        ExecuteAsync(key, callback, (context, ticks, token) => transport.QuoteGuardAsync(context, ticks, tier, token),
            r => r.Result, r => r.BankStatus, r => presentation.Text("PriceLine", ("PRICE", r.PriceSilver)));

    private Task ExecuteAsync<T>(string key, Action<string, string, T?>? callback,
        Func<ClashOfRimClientNetworkContext, long, CancellationToken, Task<ClashOfRimClientNetworkResult<T>>> send,
        Func<T, ModProtocolResponseDto?> protocol, Func<T, ModBankStatusResponseDto?> statusOf, Func<T, string> describe) where T : class
    {
        if (callback is null) return Task.CompletedTask;
        using var p = ClientSessionWork.EnterPublication();
        var owner = session.CaptureOwnership();
        var context = session.ReadContext();
        var owningGame = game.BindCurrentGame();
        long ticks = readTicks();
        int bankVersion = bank.Version;
        bool Apply(Action action)
        {
            bool applied = false;
            ClientSessionWork.TryApply(() =>
            {
                if (!owningGame.IsCurrent || !session.Owns(owner, context, requireSnapshot: false)) return;
                action(); applied = true;
            });
            return applied;
        }
        return ClientSessionWork.Run(async () =>
        {
            if (!Apply(static () => { })) return;
            T? response = null;
            string message;
            try
            {
                var result = await send(context, ticks, owner.Token).ConfigureAwait(false);
                if (!result.Success || result.Response is null)
                    message = presentation.Text("StatusQuoteFailed", ("CODE", result.ErrorCode), ("MESSAGE", result.Message));
                else
                {
                    response = result.Response;
                    message = protocol(response) is { Accepted: false } rejected
                        ? presentation.Text("StatusQuoteRejected", ("CODE", rejected.ErrorCode), ("MESSAGE", rejected.Message))
                        : describe(response);
                }
            }
            catch (Exception ex)
            {
                if (!Apply(() => presentation.Diagnose(ex))) return;
                message = presentation.Text("StatusQuoteException", ("TYPE", ex.GetType().Name), ("MESSAGE", ex.Message));
            }
            // Each caller owns its key and callback, so simultaneous quote selections do not
            // need a global busy flag or the snapshot mutation lease. The same owner is checked
            // before enqueue and when the game thread actually publishes the result.
            await ClientGamePublication.EnqueueAsync(owningGame.Enqueue, owner.Token, Apply, () =>
            {
                // Finish the caller's request even if an upload or another bank
                // publication superseded the ancillary configuration it carries.
                if (response is not null && bank.Version == bankVersion && session.Owns(owner, context, requireSnapshot: true))
                    bank.View = statusOf(response);
                callback(key, message, response);
            }).ConfigureAwait(false);
        });
    }
}
