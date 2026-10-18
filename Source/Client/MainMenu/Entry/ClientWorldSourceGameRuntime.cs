using AIRsLight.ClashOfRim.Modules.World;

namespace AIRsLight.ClashOfRim;

internal sealed class ClientWorldSourceGameRuntime : IClientWorldSourceGame
{
    public object? Current => Verse.Current.Game;
}
