namespace AIRsLight.ClashOfRim.Network.Plugins.DlcCompatibility;

public sealed class WorldPollutedTileDto
{
    public WorldPollutedTileDto(int tile, float pollution)
    {
        Tile = tile;
        Pollution = pollution;
    }

    public int Tile { get; }

    public float Pollution { get; }
}
