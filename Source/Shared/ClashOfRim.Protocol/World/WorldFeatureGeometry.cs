using System;

namespace AIRsLight.ClashOfRim.Protocol;

// Wire feature catalogs identify geometry, not live feature IDs or planet layers.
// Both ends must use the same tolerance and reject non-finite coordinates.
public readonly struct WorldFeatureGeometry
{
    public WorldFeatureGeometry(string defName, float size, float x, float y, float z)
    { DefName = defName; Size = size; X = x; Y = y; Z = z; }
    public string DefName { get; }
    public float Size { get; }
    public float X { get; }
    public float Y { get; }
    public float Z { get; }
    public static WorldFeatureGeometry From(WorldFeatureDto feature)
        => new(feature.DefName, feature.MaxDrawSizeInTiles, feature.DrawCenterX, feature.DrawCenterY, feature.DrawCenterZ);
    public bool Matches(WorldFeatureGeometry other)
        => string.Equals(DefName, other.DefName, StringComparison.Ordinal) && Near(Size, other.Size)
            && Near(X, other.X) && Near(Y, other.Y) && Near(Z, other.Z);
    private static bool Near(float left, float right) => Math.Abs(left - right) <= 0.0001f;
}
