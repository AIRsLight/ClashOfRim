using System;
using System.Collections.Generic;
using System.Linq;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Modules.Administration;
using AIRsLight.ClashOfRim.Modules.Sessions;

namespace AIRsLight.ClashOfRim.Modules.Multiplayer;

internal sealed class ClientAdminAuditPanel
{
    private readonly ClientSessionLifecycle session;
    private readonly ClientAdminAccess access;
    private readonly ClientAdministration administration;
    private ModAdminStatusResponseDto? observed;
    private IReadOnlyList<Row> rows = Array.Empty<Row>();
    internal ClientAdminAuditPanel(ClientSessionLifecycle session, ClientAdminAccess access, ClientAdministration administration)
    { this.session = session; this.access = access; this.administration = administration; }
    internal object Ownership => access.CurrentPermission;
    internal void Close() { observed = null; rows = Array.Empty<Row>(); }
    internal IReadOnlyList<Row> Read() => access.Read(() =>
    {
        var source = access.IsAdministrator && !session.IsUnavailable ? administration.Snapshot : null;
        if (ReferenceEquals(source, observed)) return rows;
        observed = source;
        return rows = (source?.AuditRecords ?? Enumerable.Empty<ModAdminAuditRecordDto>())
            .Select(item => new Row(item.CreatedAtUtc, item.ActorUserId, item.ActionKind, item.TargetUserId, item.Message)).ToList().AsReadOnly();
    });
    // Preserve server order and protocol text; no culture-dependent parsing or mutable DTO escapes.
    internal sealed record Row(string? CreatedAtUtc, string ActorUserId, string ActionKind, string? TargetUserId, string? Message);
}
