using AIRsLight.ClashOfRim.Modules.Compatibility;
using AIRsLight.ClashOfRim.ClientNetwork;
using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace AIRsLight.ClashOfRim.ThirdPartyCompatibility.Things;

internal sealed class ThingTransferCompatibility
{
    private readonly List<ThingTransferRuleRegistration> ThingTransferRuleRegistrations = new();
    private readonly CompatibilityRegistrationScope Registrations;

    internal ThingTransferCompatibility(CompatibilityRegistrationScope registrations) => Registrations = registrations;

    internal void RegisterThingTransferRule(
        string key,
        ThingTransferValidator? validator,
        ThingTransferMetadataCapturer? capturer,
        ThingTransferFinalizer? finalizer)
    {
        if (string.IsNullOrWhiteSpace(key)
            || (validator is null && capturer is null && finalizer is null))
        {
            return;
        }

        Registrations.RegisterMetadataKeyedRegistration(
            ThingTransferRuleRegistrations,
            new ThingTransferRuleRegistration(key, validator, capturer, finalizer),
            nameof(ThingTransferRuleRegistration),
            key,
            "ThingTransferRuleDuplicateRejected",
            "ThingTransferRuleDuplicateReplaced");
    }

    internal bool CanTransferThing(
        Thing thing,
        ThingTransferContext context,
        out string? rejectionCode)
    {
        rejectionCode = null;
        if (thing is null || context is null)
        {
            rejectionCode = ThingTransferDecision.ProcessorFailureCode;
            return false;
        }

        foreach (ThingTransferRuleRegistration registration in ThingTransferRuleRegistrations.ToList())
        {
            if (registration.Validator is null)
            {
                continue;
            }

            if (!Registrations.TryInvokeCompatibilityCallback(
                    nameof(CanTransferThing),
                    registration,
                    () => registration.Validator(thing, context),
                    out ThingTransferDecision decision))
            {
                rejectionCode = ThingTransferDecision.ProcessorFailureCode;
                return false;
            }

            if (!decision.Allowed)
            {
                rejectionCode = decision.RejectionCode ?? ThingTransferDecision.ProcessorFailureCode;
                return false;
            }
        }

        return true;
    }

    internal bool PrepareThingTransfer(
        Thing thing,
        ModThingReferenceDto reference,
        ThingTransferContext context,
        out string? rejectionCode)
    {
        if (!CanTransferThing(thing, context, out rejectionCode) || reference is null)
        {
            rejectionCode ??= ThingTransferDecision.ProcessorFailureCode;
            return false;
        }

        foreach (ThingTransferRuleRegistration registration in ThingTransferRuleRegistrations.ToList())
        {
            if (registration.Capturer is null)
            {
                continue;
            }

            if (!Registrations.TryInvokeCompatibilityCallback(
                    nameof(PrepareThingTransfer),
                    registration,
                    () => registration.Capturer(thing, reference, context)))
            {
                rejectionCode = ThingTransferDecision.ProcessorFailureCode;
                return false;
            }
        }

        rejectionCode = null;
        return true;
    }

    internal bool FinalizeThingTransfer(
        ModThingReferenceDto reference,
        Thing thing,
        ThingTransferContext context,
        out string? missingDefName)
    {
        missingDefName = null;
        if (reference is null || thing is null || context is null)
        {
            return false;
        }

        foreach (ThingTransferRuleRegistration registration in ThingTransferRuleRegistrations.ToList())
        {
            if (registration.Finalizer is null)
            {
                continue;
            }

            string? callbackMissingDefName = null;
            if (!Registrations.TryInvokeCompatibilityCallback(
                    nameof(FinalizeThingTransfer),
                    registration,
                    () => registration.Finalizer(reference, thing, context, out callbackMissingDefName),
                    out bool finalized)
                || !finalized)
            {
                missingDefName = callbackMissingDefName;
                return false;
            }
        }

        return true;
    }
}
