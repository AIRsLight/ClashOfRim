using Verse;

namespace AIRsLight.ClashOfRim.RemoteMaps;

public sealed class RemoteSessionMapOpenResult
{
    private RemoteSessionMapOpenResult(bool success, RemoteSessionMapParent? mapParent, Map? map, string failureReason)
    {
        Success = success;
        MapParent = mapParent;
        Map = map;
        FailureReason = failureReason;
    }

    public bool Success { get; }

    public RemoteSessionMapParent? MapParent { get; }

    public Map? Map { get; }

    public string FailureReason { get; }

    public static RemoteSessionMapOpenResult Opened(RemoteSessionMapParent? mapParent, Map? map)
    {
        return new RemoteSessionMapOpenResult(true, mapParent, map, string.Empty);
    }

    public static RemoteSessionMapOpenResult Failed(string failureReason)
    {
        return new RemoteSessionMapOpenResult(false, null, null, failureReason);
    }
}
