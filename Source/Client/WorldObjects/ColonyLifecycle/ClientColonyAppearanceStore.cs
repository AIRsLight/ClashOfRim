using System.Collections.Generic;
using AIRsLight.ClashOfRim.Modules.World;

namespace AIRsLight.ClashOfRim.WorldObjects;

// Persistence boundary only; all keys, encoding and write decisions belong to the module.
internal sealed class ClientColonyAppearanceStore : IClientColonyAppearanceStore
{
    private readonly ClashOfRimSettings settings;
    internal ClientColonyAppearanceStore(ClashOfRimSettings settings) => this.settings = settings;
    public ClientColonyAppearanceAccount ReadAccount() => new(settings.ServerBaseUrl, settings.CurrentWorldConfigurationId, settings.UserId, settings.ColonyId);
    public string? Read(string key) => settings.ColonyAppearancesByAccount?.GetValueOrDefault(key);
    public void Write(string key, string? packed)
    {
        settings.ColonyAppearancesByAccount ??= new();
        if (packed is null) settings.ColonyAppearancesByAccount.Remove(key);
        else settings.ColonyAppearancesByAccount[key] = packed;
    }
    public void Persist() => settings.Write();
}
