using System;
using System.Collections.Generic;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace ClashOfRim.RimTestBridge;

internal delegate IReadOnlyList<ModEventDetailDto>? EventDetailsReader(int? knownVersion, out int version);

// Reuse detached observations until the product publishes new detail data. RimTest
// assigns handles by object identity; copying on every poll would exhaust its leases.
internal sealed class EventDetailsObservation
{
    private readonly EventDetailsReader read;
    private int? version;
    private IReadOnlyList<ModEventDetailDto> snapshot = Array.Empty<ModEventDetailDto>();

    internal EventDetailsObservation(EventDetailsReader read) => this.read = read;

    internal IReadOnlyList<ModEventDetailDto> Read()
    {
        var changed = read(version, out int current);
        if (changed is not null)
        {
            snapshot = changed;
            version = current;
        }
        return snapshot;
    }
}
