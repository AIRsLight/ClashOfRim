using AIRsLight.ClashOfRim.ThirdPartyCompatibility.RemoteMaps;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility.Pawns;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility.Things;
using AIRsLight.ClashOfRim.Modules.Compatibility;
using AIRsLight.ClashOfRim.Modules.Administration;
using AIRsLight.ClashOfRim.Modules.Sessions;
using AIRsLight.ClashOfRim.Modules.World;
using AIRsLight.ClashOfRim.RemoteMaps;
using System;
using Verse;

namespace AIRsLight.ClashOfRim.ThirdPartyCompatibility;

public static partial class ClashOfRimCompatibilityApi
{
    // Composition binds each existing domain directly. Public compatibility
    // entry points do not own a second session, permission or catalog state.
    private static ClientSessionLifecycle? session;
    private static ClientAdminAccess? administration;
    private static ClientWorldCatalog? catalog;
    internal static void AttachSession(ClientSessionLifecycle? value) => session = value;
    internal static void AttachAdministration(ClientAdminAccess? value) => administration = value;
    internal static void AttachCatalog(ClientWorldCatalog? value) => catalog = value;
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
                var current = session;
                return current is not null
                    && (current.HasSnapshotSession(current.ReadContext())
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
                return administration?.IsAdministrator == true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool RequestServerWorldBaselineRefresh(string reason)
        => RequestWorldBaseline(() => reason);

    public static bool RequestServerWorldBaselineRefreshByKey(string translationKey)
        => RequestWorldBaseline(() => ClashOfRimText.Key(translationKey));

    private static bool RequestWorldBaseline(Func<string> reason)
    {
        try
        {
            var current = catalog;
            return current is not null && current.TryRefresh(reason, generation: true, () => ReferenceEquals(catalog, current), out _);
        }
        catch
        {
            return false;
        }
    }

}
