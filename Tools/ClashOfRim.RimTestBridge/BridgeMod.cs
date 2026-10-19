using System.Collections;
using System.Reflection;
using System.Text.Json;
using AIRsLight.ClashOfRim;
using RimTest.Protocol;
using RimTest.RimWorld;
using RimTest.Runtime;
using Verse;

namespace ClashOfRim.RimTestBridge;

public sealed class BridgeMod : Mod
{
    public BridgeMod(ModContentPack content) : base(content)
    {
        AmbientDamageObservation.Install();
        RimTestApi.RegisterExtension(b => new ClientActions(b));
    }
}

/// <summary>Explicit UI-entry allowlist; no dependency on this adapter is introduced into the product.</summary>
internal sealed class ClientActions
{
    private readonly GameBridge bridge;
    private static readonly MethodInfo[] Actions = ClientActionContract.ResolveActions(typeof(ClashOfRimMod)).OrderBy(Signature).ToArray();
    private static readonly Dictionary<string, PropertyInfo> Properties = ClientActionContract.ResolveProperties(typeof(ClashOfRimMod));
    private static ClashOfRimMod Mod => LoadedModManager.GetMod<ClashOfRimMod>();
    private static string Signature(MethodInfo m) => m.Name + "(" + string.Join(",", m.GetParameters().Select(p => p.ParameterType.FullName)) + ")";
    internal ClientActions(GameBridge bridge)
    {
        this.bridge = bridge;
        new DomainCommands(bridge);
        WorldObservations.Register(bridge);
        bridge.Router.Register("clash.actions", "Discover explicit client UI entry points and exact parameter contracts. Discovery is not test coverage.", "observe", a =>
            ObjectInspector.Paginate(Actions.Select(m => new
            {
                id = Signature(m),
                name = m.Name,
                parameters = m.GetParameters().Select(p => new
                { name = p.Name, type = p.ParameterType.FullName, optional = p.IsOptional, defaultValue = p.IsOptional ? p.DefaultValue : null }).ToArray()
            }).ToArray(), a));
        bridge.Router.Register("clash.state", "Read a bounded allowlist of client lifecycle/status properties; no credentials.", "observe", _ =>
            ClientActionContract.StateProperties.ToDictionary(n => char.ToLowerInvariant(n[0]) + n[1..], n => Property(n)));
        bridge.Router.Register("clash.records", "Page copied client view-model records with references for subsequent UI actions.", "observe", a =>
        {
            if (!ClientActionContract.RecordProperties.TryGetValue(a.String("kind"), out string? property)) throw new CommandException("invalid_arguments", "Unknown records kind.");
            return ObjectInspector.Paginate(((IEnumerable)Property(property)!).Cast<object>().Select(o => new { reference = bridge.Objects.Add(o), data = o }).ToArray(), a);
        });
        bridge.Router.Register("clash.bank", "Read the last bank response, including loans, debt and contracts.", "observe", _ => Property("BankStatusSnapshot"));
        bridge.Router.Register("clash.admin", "Read last administrator configuration/status response.", "observe", _ => Property("AdminStatusSnapshot"));
        bridge.Router.Register("clash.invoke", "Invoke one allowlisted original client action once. Poll domain results; void return does not imply acceptance/completion.", "semantic", Invoke);
    }
    private static object? Property(string name) => Properties[name].GetValue(Mod);
    private object Invoke(JsonElement a)
    {
        string name = a.String("action"); var input = a.TryGetProperty("arguments", out var args) ? args : Wire.Element(new { });
        if (input.ValueKind != JsonValueKind.Object) throw new CommandException("invalid_arguments", "arguments must be an object.");
        var matches = Actions.Where(m => (m.Name == name || Signature(m) == name) &&
            input.EnumerateObject().All(v => m.GetParameters().Any(p => p.Name == v.Name)) &&
            m.GetParameters().All(p => p.IsOptional || input.TryGetProperty(p.Name!, out _))).ToArray();
        if (matches.Length != 1) throw new CommandException("ambiguous", "Supply an exact discovered action and its named arguments.");
        var method = matches[0]; var values = method.GetParameters().Select(p => input.TryGetProperty(p.Name!, out var value) ? Bind(value, p.ParameterType) : p.DefaultValue).ToArray();
        string? server = method.Name == "StartMainMenuServerEntryFlow" ? values[0] as string : Property("ServerBaseUrl") as string;
        if (!Uri.TryCreate(server, UriKind.Absolute, out var uri) || !uri.IsLoopback || uri.Scheme != "http")
            throw new CommandException("invalid_context", "This test adapter requires an explicit loopback HTTP server.");
        try { return new { state = "invoked", returnValue = method.Invoke(Mod, values) }; }
        catch (TargetInvocationException ex) when (ex.InnerException is not null) { throw new CommandException("client_exception", ex.InnerException.ToString()); }
    }
    private object? Bind(JsonElement value, Type type)
    {
        if (value.ValueKind == JsonValueKind.Null)
        {
            if (type.IsValueType && Nullable.GetUnderlyingType(type) is null) throw new CommandException("invalid_arguments", "Non-null value required.");
            return null;
        }
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("handle", out var handle))
        {
            var instance = bridge.Objects.Get(handle.GetString()!);
            if (!type.IsInstanceOfType(instance)) throw new CommandException("wrong_type", "Handle must match " + type.FullName);
            return instance;
        }
        if (type.IsEnum)
        {
            if (value.ValueKind != JsonValueKind.String || !Enum.TryParse(type, value.GetString(), false, out var parsed) || !Enum.IsDefined(type, parsed!))
                throw new CommandException("invalid_arguments", "Invalid enum value for " + type.Name);
            return parsed;
        }
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 200) throw new CommandException("invalid_arguments", "Expected bounded list.");
            var element = type.GenericTypeArguments[0]; var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(element))!;
            foreach (var item in value.EnumerateArray()) list.Add(Bind(item, element)); return list;
        }
        // Configuration is user-authored; all other complex arguments must be discovered references.
        if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal) || type.Name == "ModAdminConfigurationDto")
            return JsonSerializer.Deserialize(value.GetRawText(), type, Wire.Json);
        throw new CommandException("unsupported", "Use a discovered object handle for " + type.FullName);
    }
}
