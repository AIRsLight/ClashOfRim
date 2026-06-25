using System.Collections.Generic;

namespace AIRsLight.ClashOfRim.Protocol;

public interface ITradeThingMetadataMatcher
{
    int RequirementStrictness(ThingReferenceDto requirement);

    bool Matches(ThingReferenceDto requirement, ThingReferenceDto candidate);

    IReadOnlyList<string> DescribeConstraints(ThingReferenceDto requirement);
}
