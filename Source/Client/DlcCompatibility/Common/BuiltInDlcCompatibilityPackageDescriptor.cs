using System;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;
using HarmonyLib;

namespace AIRsLight.ClashOfRim.DlcCompatibility;

internal sealed class BuiltInDlcCompatibilityPackageDescriptor : IClientCompatibilityPackageDescriptor
{
    private readonly Action<Harmony> apply;

    public BuiltInDlcCompatibilityPackageDescriptor(string packageId, Action<Harmony> apply, int order = 0)
    {
        PackageId = packageId ?? string.Empty;
        this.apply = apply ?? (_ => { });
        Order = order;
    }

    public string PackageId { get; }

    public int Order { get; }

    public void Apply(Harmony harmony)
    {
        apply(harmony);
    }

    void IClientCompatibilityPackageDescriptor.Apply(ClientCompatibilityPackageApplyContext context)
    {
        Apply(context.Harmony);
    }
}
