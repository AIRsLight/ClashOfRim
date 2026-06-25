using System;
using AIRsLight.ClashOfRim.ThirdPartyCompatibility;

namespace AIRsLight.ClashOfRim.CoreCompatibility;

internal sealed class BuiltInCoreCompatibilityPackageDescriptor : IClientCompatibilityPackageDescriptor
{
    public BuiltInCoreCompatibilityPackageDescriptor(string packageId, Action apply, int order = 0)
    {
        PackageId = packageId;
        Apply = apply;
        Order = order;
    }

    public string PackageId { get; }

    public int Order { get; }

    public Action Apply { get; }

    void IClientCompatibilityPackageDescriptor.Apply(ClientCompatibilityPackageApplyContext context)
    {
        Apply();
    }
}
