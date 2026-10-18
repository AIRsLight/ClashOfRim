using System;
using System.IO;
using System.Text.Json;
using AIRsLight.ClashOfRim.ClientNetwork;

namespace AIRsLight.ClashOfRim.Modules.Presence;

internal enum ClientSessionNotificationKind { Presence, Ledger, Chat, World, Player, Error }
internal sealed record ClientSessionNotification(ClientSessionNotificationKind Kind, string User, long Version,
    bool? Online = null, string? Player = null, string? Message = null)
{
    internal static ClientSessionNotification? Decode(ModSessionStreamEvent item)
    {
        ClientSessionNotificationKind? kind = item.EventName switch
        {
            "presence" => ClientSessionNotificationKind.Presence, "ledgerChanged" => ClientSessionNotificationKind.Ledger,
            "chatChanged" => ClientSessionNotificationKind.Chat, "worldConfigurationChanged" => ClientSessionNotificationKind.World,
            "playerOnline" => ClientSessionNotificationKind.Player, "error" => ClientSessionNotificationKind.Error, _ => null
        };
        if (kind is null) return null;
        using var document = JsonDocument.Parse(item.Data);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw Invalid();
        string user = Text("UserId", required: true)!;
        string versionName = kind switch
        {
            ClientSessionNotificationKind.Chat => "ChatVersion", ClientSessionNotificationKind.World => "WorldConfigurationVersion",
            ClientSessionNotificationKind.Player => "PlayerOnlineVersion", _ => "NotificationVersion"
        };
        if (!root.TryGetProperty(versionName, out var number) || number.ValueKind != JsonValueKind.Number || !number.TryGetInt64(out long version) || version < 0) throw Invalid();
        bool? online = null;
        if (kind is ClientSessionNotificationKind.Presence or ClientSessionNotificationKind.Ledger or ClientSessionNotificationKind.Error)
        {
            if (!root.TryGetProperty("Online", out var field) || field.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid();
            online = field.GetBoolean();
        }
        return new(kind.Value, user, version, online, kind == ClientSessionNotificationKind.Player ? Text("OnlineUserId", true) : null, Text("Message", false));
        string? Text(string name, bool required)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            { if (required) throw Invalid(); return null; }
            if (value.ValueKind != JsonValueKind.String) throw Invalid();
            string? result = value.GetString(); if (required && string.IsNullOrWhiteSpace(result)) throw Invalid(); return result;
        }
        static InvalidDataException Invalid() => new("Incomplete session stream payload.");
    }
}
