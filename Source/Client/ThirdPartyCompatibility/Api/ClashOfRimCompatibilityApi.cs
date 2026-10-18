using AIRsLight.ClashOfRim.ThirdPartyCompatibility.RemoteMaps;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility.Pawns;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility.Things;
using AIRsLight.ClashOfRim.Modules.Compatibility;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.Raids;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim;
using RimWorld;
using RimWorld.Planet;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Linq;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim.ThirdPartyCompatibility;

public static partial class ClashOfRimCompatibilityApi
{
    private static readonly CompatibilityRegistrationScope Registrations = new(message => Log.Warning(message));
    private static readonly AdminBaselineCompatibility AdminBaseline = new(Registrations);
    private static readonly WorldConfigurationCompatibility WorldConfiguration = new(Registrations);
    private static readonly WorldGenerationSettingCompatibility WorldGenerationSettings = new(Registrations);
    private static readonly CompatibilityPackageCatalog CompatibilityPackages = new(Registrations);
    private static readonly RemoteMapLifecycleCompatibility RemoteMapLifecycle = new(Registrations);
    private static readonly RemoteMapProjectionCompatibility RemoteMapProjection = new(Registrations);
    private static readonly SnapshotSanitizationCompatibility SnapshotSanitization = new(Registrations);
    private static readonly ColonySiteCompatibility ColonySite = new(Registrations);
    private static readonly FactionPreparationCompatibility FactionPreparation = new(Registrations);
    private static readonly PawnReferenceCompatibility PawnReferences = new(Registrations);
    private static readonly PawnExchangeCompatibility PawnExchange = new(Registrations, message => ClashLog.Message(message));
    private static readonly PawnTradeCompatibility PawnTrade = new(Registrations);
    private static readonly ThingReferenceCompatibility ThingReferences = new(Registrations);
    private static readonly ThingTransferCompatibility ThingTransfers = new(Registrations);

    public static bool IsRemoteMapProjectionLoading => RemoteMapProjectionLoadScope.Active;

    public static bool HasSnapshotSaveSanitizers => SnapshotSanitization.HasSnapshotSaveSanitizers;

    public static CompatibilityRegistrationToken BeginRegistrationCycle(string owner) => Registrations.BeginRegistrationCycle(owner);
    public static CompatibilityRegistrationToken BeginRegistrationCycle(string owner, int priority) => Registrations.BeginRegistrationCycle(owner, priority);
    public static int CurrentRegistrationGeneration => Registrations.CurrentRegistrationGeneration;
    public static CompatibilityRegistrationToken CreateRegistrationToken(string owner) => Registrations.CreateRegistrationToken(owner);
    public static CompatibilityRegistrationToken CreateRegistrationToken(string owner, int priority) => Registrations.CreateRegistrationToken(owner, priority);
    public static bool UseRegistrationToken(CompatibilityRegistrationToken token, Action registration) => Registrations.UseRegistrationToken(token, registration);
    public static bool RevokeRegistrationToken(CompatibilityRegistrationToken? token) => Registrations.RevokeRegistrationToken(token);
    public static int RevokeRegistrationsByOwner(string owner) => Registrations.RevokeRegistrationsByOwner(owner);

    public static bool IsActiveMultiplayerSession
    {
        get
        {
            try
            {
                ClashOfRimMod? mod = LoadedModManager.GetMod<ClashOfRimMod>();
                return mod is not null
                    && (mod.IsInActiveMultiplayerSession
                        || mod.CanUseVanillaMenuSnapshotUpload
                        || ClashOfRimGameComponent.HasActiveRemoteMapSession
                        || ClashOfRimGameComponent.HasActiveRaidBattleSession);
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool IsCurrentUserAdministrator
    {
        get
        {
            try
            {
                return LoadedModManager.GetMod<ClashOfRimMod>()?.IsAdministrator == true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool RequestServerWorldBaselineRefresh(string reason)
    {
        try
        {
            ClashOfRimMod? mod = LoadedModManager.GetMod<ClashOfRimMod>();
            if (mod is null)
            {
                return false;
            }

            mod.RequestServerWorldBaselineRefresh(reason);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool RequestServerWorldBaselineRefreshByKey(string translationKey)
    {
        return RequestServerWorldBaselineRefresh(ClashOfRimText.Key(translationKey));
    }


}
