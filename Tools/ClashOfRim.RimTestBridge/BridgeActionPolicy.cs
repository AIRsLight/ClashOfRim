using System.Text.Json;
using RimTest.Protocol;
using RimTest.Runtime;

namespace ClashOfRim.RimTestBridge;

// Shared transport admission only. Domain commands still enforce their own
// current game, window and object requirements before applying an action.
internal sealed class BridgeActionPolicy(Func<string?> serverAddress, CommandRouter router)
{
    internal void RequireLoopback()
    {
        if (!Uri.TryCreate(serverAddress(), UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Scheme != "http")
            throw new CommandException("invalid_context", "Test actions require a loopback HTTP server.");
    }

    internal void RequireFixture(JsonElement arguments)
    {
        RequireLoopback();
        if (!arguments.Bool("acknowledgeFixture") || !router.Capabilities.Any(c => c.Method == "debug.invoke"))
            throw new CommandException("disabled", "Explicit fixture acknowledgement and AllowDebug required.");
    }
}
