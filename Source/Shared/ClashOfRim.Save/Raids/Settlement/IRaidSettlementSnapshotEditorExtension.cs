using System.Xml.Linq;

namespace AIRsLight.ClashOfRim.Save;

public interface IRaidSettlementSnapshotEditorExtension
{
    bool TryApplySettlementDamage(XElement thing, RaidSettlementLoss loss);

    void ApplyPostSettlementEdit(XElement targetMap, RaidSettlementDiffResult settlement)
    {
    }
}
