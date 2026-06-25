using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.Raids;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using Verse;

namespace AIRsLight.ClashOfRim.ThirdPartyCompatibility;

public static partial class ClashOfRimCompatibilityApi
{
    public static void RegisterRaidCaravanMapEntryHandler(RaidCaravanMapEntryHandler handler)
    {
        RegisterUnique(RaidCaravanMapEntryHandlers, handler);
    }

    public static void RegisterRemoteDefenderMapPreparedHandler(RemoteDefenderMapPreparedHandler handler)
    {
        RegisterUnique(RemoteDefenderMapPreparedHandlers, handler);
    }

    public static void RegisterRemoteMapLoadedHandler(RemoteMapLoadedHandler handler)
    {
        RegisterUnique(RemoteMapLoadedHandlers, handler);
    }

    public static void RegisterRemoteMapProjectionReferenceRewriter(RemoteMapProjectionReferenceRewriter rewriter)
    {
        RegisterUnique(RemoteMapProjectionReferenceRewriters, rewriter);
    }

    public static void RegisterRemoteMapProjectionSanitizer(RemoteMapProjectionSanitizer sanitizer)
    {
        RegisterUnique(RemoteMapProjectionSanitizers, sanitizer);
    }

    public static void RegisterRemoteSessionPawnCleanupHandler(RemoteSessionPawnCleanupHandler handler)
    {
        RegisterUnique(RemoteSessionPawnCleanupHandlers, handler);
    }

    public static void RegisterRemoteMapIntegerLoadIdRewriter(
        string loadIdPrefix,
        Func<XElement, bool> predicate,
        Func<int> nextId)
    {
        if (string.IsNullOrWhiteSpace(loadIdPrefix) || predicate is null || nextId is null)
        {
            return;
        }

        string normalizedPrefix = loadIdPrefix.Trim();
        if (RemoteMapIntegerLoadIdRewriters.Any(registration =>
            string.Equals(registration.LoadIdPrefix, normalizedPrefix, StringComparison.Ordinal)
            && Equals(registration.Predicate, predicate)
            && Equals(registration.NextId, nextId)))
        {
            return;
        }

        if (RemoteMapIntegerLoadIdRewriters.Any(registration =>
            string.Equals(registration.LoadIdPrefix, normalizedPrefix, StringComparison.Ordinal)))
        {
            AddRegistrationDiagnostic(
                "Warning",
                "RemoteMapLoadIdPrefixShared",
                nameof(RemoteMapIntegerLoadIdRewriterRegistration),
                normalizedPrefix,
                "Remote map load id prefix '" + normalizedPrefix + "' is registered by multiple compatibility handlers.");
        }

        var registration = new RemoteMapIntegerLoadIdRewriterRegistration(normalizedPrefix, predicate, nextId);
        RemoteMapIntegerLoadIdRewriters.Add(registration);
        int priority = ActiveRegistrationPriority();
        RegistrationPriorities[registration] = priority;
        RegistrationOrders[registration] = unchecked(++registrationOrderSequence);
        SortRegistrationsByPriority(RemoteMapIntegerLoadIdRewriters);
        CompatibilityRegistrationRecord record = AddRegistrationRecord(
            nameof(RemoteMapIntegerLoadIdRewriterRegistration),
            normalizedPrefix,
            priority);
        TrackRegistration(() =>
        {
            RemoteMapIntegerLoadIdRewriters.Remove(registration);
            RegistrationPriorities.Remove(registration);
            RegistrationOrders.Remove(registration);
            RegistrationRecords.Remove(record);
        });
    }

    public static IReadOnlyList<RemoteMapIntegerLoadIdRewriterRegistration> GetRemoteMapIntegerLoadIdRewriters()
    {
        return RemoteMapIntegerLoadIdRewriters.ToList();
    }

    public static void RegisterRemoteMapAreaLoadIdProvider(RemoteMapAreaLoadIdProvider provider)
    {
        RegisterUnique(RemoteMapAreaLoadIdProviders, provider);
    }

    public static string? BuildRemoteMapAreaLoadId(XElement area, int id)
    {
        foreach (RemoteMapAreaLoadIdProvider provider in RemoteMapAreaLoadIdProviders.ToList())
        {
            if (!TryInvokeCompatibilityCallback(
                    nameof(BuildRemoteMapAreaLoadId),
                    provider,
                    () => provider(area, id),
                    out string? value))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    public static void RegisterSnapshotSaveSanitizer(CompatibilitySnapshotSaveSanitizer sanitizer)
    {
        RegisterUnique(SnapshotSaveSanitizers, sanitizer);
    }

    public static void RegisterPlayerColonySiteRegistrationSuppressor(PlayerColonySiteRegistrationSuppressor suppressor)
    {
        RegisterUnique(PlayerColonySiteRegistrationSuppressors, suppressor);
    }

    public static bool IsPlayerColonySiteRegistrationSuppressedByCompatibility()
    {
        foreach (PlayerColonySiteRegistrationSuppressor suppressor in PlayerColonySiteRegistrationSuppressors.ToList())
        {
            if (!TryInvokeCompatibilityCallback(
                    nameof(IsPlayerColonySiteRegistrationSuppressedByCompatibility),
                    suppressor,
                    () => suppressor(),
                    out bool suppressed))
            {
                continue;
            }

            if (suppressed)
            {
                return true;
            }
        }

        return false;
    }

}
