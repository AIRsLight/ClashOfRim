using AIRsLight.ClashOfRim.ClientNetwork;
using RimTest.Runtime;
using RimWorld.Planet;
using Verse;

namespace ClashOfRim.RimTestBridge;

// Composition only. Each command module owns its registrations and observations;
// no module reaches through this root for another domain's state or helpers.
internal static class DomainCommands
{
    internal static void Register(CommandRouter router, ObjectRegistry objects, BridgeActionPolicy policy,
        EventDetailsReader readEvents, Func<ModBankStatusResponseDto?> bankStatus,
        Func<IReadOnlyList<ModServerShopListingDto>> shopListings, Action<MapParent> openAbandon,
        Func<int> achievementVersion, Func<string> worldConfigurationId, Func<string, Window>? uiWindow = null)
    {
        _ = new CompatibilityCommands(router, policy);
        _ = new ThirdPartyCommands(router, objects, policy);
        _ = new ThirdPartyMapCommands(router, policy);
        _ = new SupportCommands(router, objects, policy);
        _ = new MercenaryCommands(router, objects, policy);
        _ = new WorldStateCommands(router, objects, worldConfigurationId);
        _ = new GiftCommands(router, objects, policy);
        _ = new EventCommands(router, objects, readEvents);
        _ = new RemoteMapCommands(router, objects, policy);
        _ = new RemoteMapPerformanceCommands(router, policy);
        _ = new PocketMapCommands(router, objects, policy);
        _ = new UiLayoutCommands(router, policy, uiWindow ?? (_ => throw new InvalidOperationException("UI fixture provider missing.")));
        _ = new CombatCommands(router, policy);
        _ = new TradeCommands(router, objects, policy);
        _ = new StabilityCommands(router, objects, policy);
        _ = new CatalogCommands(router, objects, policy, shopListings);
        _ = new BankCommands(router, objects, policy, bankStatus);
        _ = new RelocationCommands(router, objects, policy);
        _ = new GravshipCommands(router, objects, policy);
        _ = new DlcPersistenceCommands(router, policy);
        _ = new AccountLifecycleCommands(router, policy, openAbandon, achievementVersion);
    }
}
