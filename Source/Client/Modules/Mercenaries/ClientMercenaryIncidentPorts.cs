using System.Threading;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Synchronization;

namespace AIRsLight.ClashOfRim.Modules.Mercenaries;

internal sealed record ClientMercenaryIncidentCommand(string Key, string ContractId, string Kind, long GameTicks)
{
    internal bool IsCompletion => Kind == "Completed";
}
internal interface IClientMercenaryIncidentTransport
{
    Task<ClashOfRimClientNetworkResult<ModMercenaryIncidentResponseDto>> ReportAsync(ClashOfRimClientNetworkContext context,
        ClientMercenaryIncidentCommand command, CancellationToken token);
}
internal interface IClientMercenaryIncidentRuntime
{
    long GameTicks { get; }
    void ApplyDebt(ModBankDebtSummaryDto debt, ModBankStatusResponseDto status);
}
internal interface IClientMercenaryIncidentPresentation : IClientMercenaryPresentation
{
    string BaseSnapshotChanged { get; }
    string Admission(LocalMutationAdmission admission, LocalMutationState? pending);
    void IncidentReported(string message);
}
