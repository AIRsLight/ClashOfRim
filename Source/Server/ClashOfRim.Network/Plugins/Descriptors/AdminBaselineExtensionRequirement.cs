namespace AIRsLight.ClashOfRim.Network.Plugins;

public sealed record AdminBaselineExtensionRequirement(
    string ProviderId,
    string Kind,
    string? RequiredPackageId = null,
    string? DisplayName = null);

public sealed record AdminBaselineRequirementContext(IReadOnlySet<string> ServerPackageIds);

public interface IAdminBaselineRequirementProvider
{
    IEnumerable<AdminBaselineExtensionRequirement> GetRequirements(AdminBaselineRequirementContext context);
}
