using AIRsLight.ClashOfRim.Modules.Compatibility;
using AIRsLight.ClashOfRim.ClientNetwork;
using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace AIRsLight.ClashOfRim.ThirdPartyCompatibility.Pawns;

internal sealed class PawnExchangeCompatibility
{
    private readonly List<PawnExchangeExtensionAppender> PawnExchangeExtensionAppenders = new();
    private readonly List<PawnExchangePackageRegistrar> PawnExchangePackageRegistrars = new();
    private readonly List<PawnPreviewRestorer> PawnPreviewRestorers = new();
    private readonly List<PawnExchangePackageNormalizer> PawnExchangePackageNormalizers = new();
    private readonly List<PawnExchangeLocalSaveOnlyLoadIdPredicate> PawnExchangeLocalSaveOnlyLoadIdPredicates = new();
    private readonly List<PawnPostRestoreLocalizer> PawnPostRestoreLocalizers = new();
    private readonly CompatibilityRegistrationScope Registrations;
    private readonly Action<string> log;

    internal PawnExchangeCompatibility(CompatibilityRegistrationScope registrations, Action<string> log)
    {
        Registrations = registrations;
        this.log = log;
    }

    internal void RegisterPawnExchangeExtensionAppender(PawnExchangeExtensionAppender appender)
    {
        Registrations.RegisterUnique(PawnExchangeExtensionAppenders, appender);
    }

    internal void AppendPawnExchangeExtensions(Pawn pawn, ModPawnExchangePackageDto package)
    {
        if (pawn is null || package is null)
        {
            return;
        }

        foreach (PawnExchangeExtensionAppender appender in PawnExchangeExtensionAppenders.ToList())
        {
            Registrations.TryInvokeCompatibilityCallback(
                nameof(AppendPawnExchangeExtensions),
                appender,
                () => appender(pawn, package));
        }
    }

    internal void RegisterPawnExchangePackageRegistrar(PawnExchangePackageRegistrar registrar)
    {
        Registrations.RegisterUnique(PawnExchangePackageRegistrars, registrar);
    }

    internal void RegisterPawnExchangePackageNormalizer(PawnExchangePackageNormalizer normalizer)
    {
        Registrations.RegisterUnique(PawnExchangePackageNormalizers, normalizer);
    }

    internal void RegisterPawnExchangeLocalSaveOnlyLoadIdPredicate(PawnExchangeLocalSaveOnlyLoadIdPredicate predicate)
    {
        Registrations.RegisterUnique(PawnExchangeLocalSaveOnlyLoadIdPredicates, predicate);
    }

    internal void NormalizePawnExchangePackage(ModPawnExchangePackageDto package)
    {
        if (package is null)
        {
            return;
        }

        foreach (PawnExchangePackageNormalizer normalizer in PawnExchangePackageNormalizers.ToList())
        {
            string key = CompatibilityRegistrationScope.RegistrationKey(normalizer);
            log("[ClashOfRim][Compatibility] pawn package normalizer begin: " + key + ".");
            Registrations.TryInvokeCompatibilityCallback(
                nameof(NormalizePawnExchangePackage),
                normalizer,
                () => normalizer(package));
            log("[ClashOfRim][Compatibility] pawn package normalizer end: " + key + ".");
        }
    }

    internal bool TryRegisterPawnExchangePackageExtensions(ModPawnExchangePackageDto package, out string message)
    {
        if (package is null) { message = string.Empty; return true; }
        return TryRestore(PawnExchangePackageRegistrars, nameof(TryRegisterPawnExchangePackageExtensions), registrar =>
        { bool accepted = registrar(package, out string message); return (accepted, message); }, out message);
    }

    internal void RegisterPawnPreviewRestorer(PawnPreviewRestorer restorer) => Registrations.RegisterUnique(PawnPreviewRestorers, restorer);

    internal bool TryRestorePawnPreview(Pawn pawn, ModPawnExchangePackageDto package, out string message)
        => TryRestore(PawnPreviewRestorers, nameof(TryRestorePawnPreview), restorer =>
        { bool accepted = restorer(pawn, package, out string message); return (accepted, message); }, out message);

    private bool TryRestore<T>(List<T> callbacks, string operation, Func<T, (bool Accepted, string Message)> invoke, out string message) where T : Delegate
    {
        message = string.Empty;
        foreach (T callback in callbacks.ToList())
        {
            (bool Accepted, string Message) result = (false, string.Empty);
            if (!Registrations.TryInvokeCompatibilityCallback(operation, callback,
                () => { result = invoke(callback); return result.Accepted; }, out bool accepted))
            { message = "Compatibility package failed."; return false; }
            message = result.Message;
            if (!accepted) return false;
        }
        return true;
    }

    internal bool IsPawnExchangeLocalSaveOnlyLoadId(string? loadId)
    {
        if (string.IsNullOrWhiteSpace(loadId))
        {
            return false;
        }

        foreach (PawnExchangeLocalSaveOnlyLoadIdPredicate predicate in PawnExchangeLocalSaveOnlyLoadIdPredicates.ToList())
        {
            if (!Registrations.TryInvokeCompatibilityCallback(
                    nameof(IsPawnExchangeLocalSaveOnlyLoadId),
                    predicate,
                    () => predicate(loadId!),
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

    internal void RegisterPawnPostRestoreLocalizer(PawnPostRestoreLocalizer localizer)
    {
        Registrations.RegisterUnique(PawnPostRestoreLocalizers, localizer);
    }

    internal void LocalizeRestoredPawnWithCompatibility(Pawn pawn)
    {
        foreach (PawnPostRestoreLocalizer localizer in PawnPostRestoreLocalizers.ToList())
        {
            Registrations.TryInvokeCompatibilityCallback(
                nameof(LocalizeRestoredPawnWithCompatibility),
                localizer,
                () => localizer(pawn));
        }
    }
}
