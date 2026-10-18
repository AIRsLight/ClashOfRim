using AIRsLight.ClashOfRim.Modules.Compatibility;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace AIRsLight.ClashOfRim.ThirdPartyCompatibility.Pawns;

internal sealed class PawnTradeCompatibility
{
    private readonly List<PawnSoldEffectHandler> PawnSoldEffectHandlers = new();
    private readonly List<TradeablePawnPredicate> TradeablePawnPredicates = new();
    private readonly List<TradePawnRestoreValidator> TradePawnRestoreValidators = new();
    private readonly CompatibilityRegistrationScope Registrations;

    internal PawnTradeCompatibility(CompatibilityRegistrationScope registrations) => Registrations = registrations;

    internal void RegisterPawnSoldEffectHandler(PawnSoldEffectHandler handler)
    {
        Registrations.RegisterUnique(PawnSoldEffectHandlers, handler);
    }

    internal void ApplyPawnSoldEffects(Pawn pawn, Pawn? negotiator)
    {
        foreach (PawnSoldEffectHandler handler in PawnSoldEffectHandlers.ToList())
        {
            Registrations.TryInvokeCompatibilityCallback(
                nameof(ApplyPawnSoldEffects),
                handler,
                () => handler(pawn, negotiator));
        }
    }

    internal void RegisterTradeablePawnPredicate(TradeablePawnPredicate predicate)
    {
        Registrations.RegisterUnique(TradeablePawnPredicates, predicate);
    }

    internal bool IsTradeablePawnByCompatibility(Pawn pawn)
    {
        if (pawn is null)
        {
            return false;
        }

        foreach (TradeablePawnPredicate predicate in TradeablePawnPredicates.ToList())
        {
            if (!Registrations.TryInvokeCompatibilityCallback(
                    nameof(IsTradeablePawnByCompatibility),
                    predicate,
                    () => predicate(pawn),
                    out bool matched))
            {
                continue;
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }

    internal void RegisterTradePawnRestoreValidator(TradePawnRestoreValidator validator)
    {
        Registrations.RegisterUnique(TradePawnRestoreValidators, validator);
    }

    internal bool IsTradePawnRestoreAllowedByCompatibility(Pawn pawn)
    {
        if (pawn is null)
        {
            return false;
        }

        foreach (TradePawnRestoreValidator validator in TradePawnRestoreValidators.ToList())
        {
            if (!Registrations.TryInvokeCompatibilityCallback(
                    nameof(IsTradePawnRestoreAllowedByCompatibility),
                    validator,
                    () => validator(pawn),
                    out bool matched))
            {
                continue;
            }

            if (matched)
            {
                return true;
            }
        }

        return false;
    }
}
