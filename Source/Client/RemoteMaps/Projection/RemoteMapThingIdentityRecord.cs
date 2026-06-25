using Verse;

namespace AIRsLight.ClashOfRim.RemoteMaps;

public sealed class RemoteMapThingIdentityRecord : IExposable
{
    public string SessionId = string.Empty;
    public string RelatedEventId = string.Empty;
    public string Mode = string.Empty;
    public string SourceMapId = string.Empty;
    public string MapUniqueId = string.Empty;
    public string ProjectedThingId = string.Empty;
    public string OriginalThingId = string.Empty;

    public void ExposeData()
    {
        Scribe_Values.Look(ref SessionId, "sessionId", string.Empty);
        Scribe_Values.Look(ref RelatedEventId, "relatedEventId", string.Empty);
        Scribe_Values.Look(ref Mode, "mode", string.Empty);
        Scribe_Values.Look(ref SourceMapId, "sourceMapId", string.Empty);
        Scribe_Values.Look(ref MapUniqueId, "mapUniqueId", string.Empty);
        Scribe_Values.Look(ref ProjectedThingId, "projectedThingId", string.Empty);
        Scribe_Values.Look(ref OriginalThingId, "originalThingId", string.Empty);
        SessionId ??= string.Empty;
        RelatedEventId ??= string.Empty;
        Mode ??= string.Empty;
        SourceMapId ??= string.Empty;
        MapUniqueId ??= string.Empty;
        ProjectedThingId ??= string.Empty;
        OriginalThingId ??= string.Empty;
    }

    public RemoteMapThingIdentityRecord Clone()
    {
        return new RemoteMapThingIdentityRecord
        {
            SessionId = SessionId,
            RelatedEventId = RelatedEventId,
            Mode = Mode,
            SourceMapId = SourceMapId,
            MapUniqueId = MapUniqueId,
            ProjectedThingId = ProjectedThingId,
            OriginalThingId = OriginalThingId
        };
    }
}
