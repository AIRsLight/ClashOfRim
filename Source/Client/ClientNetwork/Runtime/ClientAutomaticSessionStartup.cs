using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.Modules.World;
using Verse;

namespace AIRsLight.ClashOfRim.ClientNetwork;

// GameComponent supplies its own Game, not whichever scene is active when an
// old component callback arrives. The module owns admission and one-time use.
internal sealed class ClientAutomaticSessionStartup
{
    internal static ClientAutomaticSessionStartup? Current { get; private set; }
    internal static void Attach(ClientAutomaticSessionStartup? value) => Current = value;
    private readonly ClientSessionStartup startup;
    private readonly ConditionalWeakTable<Game, ClientSessionStartup.AutomaticStartup> games = new();
    internal ClientAutomaticSessionStartup(ClientSessionStartup startup) => this.startup = startup;
    internal Task PollAsync(Game game)
    {
        if (!Owns(game)) return Task.CompletedTask;
        if (!games.TryGetValue(game, out var automatic)) automatic = games.GetValue(game, Create);
        return automatic.PollAsync();
    }
    private bool Owns(Game game) => ReferenceEquals(Current, this) && ReferenceEquals(game, Verse.Current.Game);
    private ClientSessionStartup.AutomaticStartup Create(Game game) => startup.CreateAutomatic(() => Owns(game));
}
