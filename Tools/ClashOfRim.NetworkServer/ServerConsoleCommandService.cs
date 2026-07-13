using AIRsLight.ClashOfRim.Events;
using AIRsLight.ClashOfRim.Network;
using AIRsLight.ClashOfRim.Protocol;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AIRsLight.ClashOfRim.NetworkServer;

internal static class ServerConsoleCommandService
{
    private const string ActorUserId = "server-cli";
    private const int ShutdownDelaySeconds = 10;
    private static int shutdownScheduled;

    public static void Start(WebApplication app, ILogger logger)
    {
        if (!Environment.UserInteractive || Console.IsInputRedirected)
        {
            logger.LogInformation(T("Cli.ConsoleDisabled"));
            return;
        }

        ClashOfRimNetworkState state = app.Services.GetRequiredService<ClashOfRimNetworkState>();
        IHostApplicationLifetime lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        CancellationToken stopping = lifetime.ApplicationStopping;
        _ = Task.Run(
            () => RunLoop(state, lifetime, logger, stopping),
            CancellationToken.None);
    }

    private static void RunLoop(
        ClashOfRimNetworkState state,
        IHostApplicationLifetime lifetime,
        ILogger logger,
        CancellationToken stopping)
    {
        Console.WriteLine(T("Cli.Prompt"));
        while (!stopping.IsCancellationRequested)
        {
            string? line;
            try
            {
                line = Console.ReadLine();
            }
            catch (IOException)
            {
                return;
            }
            catch (InvalidOperationException)
            {
                return;
            }

            if (line is null)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                Execute(line, state, lifetime, logger);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, T("Cli.CommandFailedLog"), line);
                Console.WriteLine(T("Cli.CommandFailed", ("MESSAGE", ex.Message)));
            }
        }
    }

    private static void Execute(
        string line,
        ClashOfRimNetworkState state,
        IHostApplicationLifetime lifetime,
        ILogger logger)
    {
        IReadOnlyList<string> args = SplitCommandLine(line);
        if (args.Count == 0)
        {
            return;
        }

        string command = args[0].ToLowerInvariant();
        switch (command)
        {
            case "help":
            case "?":
                PrintHelp();
                break;
            case "status":
                PrintStatus(state);
                break;
            case "players":
                PrintPlayers(state);
                break;
            case "admins":
                PrintAdmins(state);
                break;
            case "bans":
            case "banned":
                PrintBannedUsers(state);
                break;
            case "lock":
                LockMaintenance(state, JoinArgs(args, 1));
                break;
            case "unlock":
                UnlockMaintenance(state);
                break;
            case "kick":
                RequireArgs(args, 2, T("Cli.UsageKick"));
                KickUser(state, args[1]);
                break;
            case "ban":
                RequireArgs(args, 2, T("Cli.UsageBan"));
                BanUser(state, args[1]);
                break;
            case "unban":
                RequireArgs(args, 2, T("Cli.UsageUnban"));
                UnbanUser(state, args[1]);
                break;
            case "promote":
                RequireArgs(args, 2, T("Cli.UsagePromote"));
                PromoteAdmin(state, args[1]);
                break;
            case "revoke":
                RequireArgs(args, 2, T("Cli.UsageRevoke"));
                RevokeAdmin(state, args[1]);
                break;
            case "reset-password":
                RequireArgs(args, 2, T("Cli.UsageResetPassword"));
                ResetPassword(state, args[1], args.Count >= 3 ? args[2] : string.Empty);
                break;
            case "post-upload-jobs":
                PrintPostUploadJobs(state);
                break;
            case "retry-post-upload":
                RequireArgs(args, 2, T("Cli.UsageRetryPostUpload"));
                RetryPostUploadJob(state, args[1]);
                break;
            case "setcooldown":
                RequireArgs(args, 3, T("Cli.UsageSetCooldown"));
                SetRaidCooldown(state, args[1], args[2]);
                break;
            case "broadcast":
                Broadcast(state, args.Skip(1).ToList());
                break;
            case "stop":
            case "shutdown":
                ScheduleShutdown(state, lifetime, logger);
                break;
            default:
                Console.WriteLine(T("Cli.UnknownCommand"));
                break;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine(T("Cli.Help"));
    }

    private static void PrintStatus(ClashOfRimNetworkState state)
    {
        IReadOnlyList<PlayerSessionRecord> players = state.Players.List();
        int onlineCount = players.Count(player => state.OnlinePresence.IsUserOnline(player.UserId));
        Console.WriteLine(T(
            "Cli.StatusSummary",
            ("PLAYERS", players.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            ("ONLINE", onlineCount.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        Console.WriteLine(T(
            "Cli.MaintenanceLockStatus",
            ("STATE", state.AdminControl.MaintenanceLoginLocked ? T("Cli.Enabled") : T("Cli.Disabled"))));
        if (!string.IsNullOrWhiteSpace(state.AdminControl.MaintenanceReason))
        {
            Console.WriteLine(T("Cli.MaintenanceReason", ("REASON", state.AdminControl.MaintenanceReason)));
        }

        Console.WriteLine(T("Cli.Admins", ("ADMINS", string.Join(", ", state.WorldConfiguration.ListAdministrators()))));
        Console.WriteLine(T(
            "Cli.BannedCount",
            ("COUNT", state.AdminControl.ListBannedUsers().Count.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }

    private static void PrintPlayers(ClashOfRimNetworkState state)
    {
        IReadOnlyList<PlayerSessionRecord> players = state.Players.List();
        if (players.Count == 0)
        {
            Console.WriteLine(T("Cli.NoPlayers"));
            return;
        }

        foreach (PlayerSessionRecord player in players)
        {
            string online = state.OnlinePresence.IsUserOnline(player.UserId) ? T("Cli.Online") : T("Cli.Offline");
            string admin = state.WorldConfiguration.IsAdministrator(player.UserId) ? " " + T("Cli.AdminFlag") : string.Empty;
            string banned = state.AdminControl.IsBanned(player.UserId) ? " " + T("Cli.BannedFlag") : string.Empty;
            string displayName = string.IsNullOrWhiteSpace(player.DisplayName) ? player.UserId : player.DisplayName!;
            string wealth = player.LatestSnapshotWealth.HasValue
                ? player.LatestSnapshotWealth.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : T("Cli.Unknown");
            Console.WriteLine(T(
                "Cli.PlayerLine",
                ("USER", player.UserId),
                ("DISPLAY", displayName),
                ("COLONY", player.ColonyId),
                ("SNAPSHOT", player.CurrentSnapshotId ?? "-"),
                ("WEALTH", wealth),
                ("ONLINE", online),
                ("FLAGS", (admin + banned).Trim())));
        }
    }

    private static void PrintAdmins(ClashOfRimNetworkState state)
    {
        IReadOnlyList<string> admins = state.WorldConfiguration.ListAdministrators();
        Console.WriteLine(admins.Count == 0 ? T("Cli.NoAdmins") : string.Join(Environment.NewLine, admins));
    }

    private static void PrintBannedUsers(ClashOfRimNetworkState state)
    {
        IReadOnlyList<string> banned = state.AdminControl.ListBannedUsers();
        Console.WriteLine(banned.Count == 0 ? T("Cli.NoBannedUsers") : string.Join(Environment.NewLine, banned));
    }

    private static void LockMaintenance(ClashOfRimNetworkState state, string? reason)
    {
        state.AdminControl.SetMaintenanceLoginLocked(true, reason);
        state.AdminControl.AddAudit("LockMaintenance", ActorUserId, null, reason, DateTimeOffset.UtcNow);
        Console.WriteLine(T("Cli.Locked"));
    }

    private static void UnlockMaintenance(ClashOfRimNetworkState state)
    {
        state.AdminControl.SetMaintenanceLoginLocked(false, null);
        state.AdminControl.AddAudit("UnlockMaintenance", ActorUserId, null, null, DateTimeOffset.UtcNow);
        Console.WriteLine(T("Cli.Unlocked"));
    }

    private static void KickUser(ClashOfRimNetworkState state, string userId)
    {
        int affected = EndUserSession(state, userId);
        state.AdminControl.AddAudit("Kick", ActorUserId, userId, null, DateTimeOffset.UtcNow);
        Console.WriteLine(T("Cli.Kicked", ("USER", userId), ("AFFECTED", affected.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }

    private static void BanUser(ClashOfRimNetworkState state, string userId)
    {
        state.AdminControl.Ban(userId);
        int affected = EndUserSession(state, userId);
        state.AdminControl.AddAudit("Ban", ActorUserId, userId, null, DateTimeOffset.UtcNow);
        Console.WriteLine(T("Cli.Banned", ("USER", userId), ("AFFECTED", affected.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }

    private static void UnbanUser(ClashOfRimNetworkState state, string userId)
    {
        bool removed = state.AdminControl.Unban(userId);
        state.AdminControl.AddAudit("Unban", ActorUserId, userId, null, DateTimeOffset.UtcNow);
        Console.WriteLine(removed ? T("Cli.Unbanned", ("USER", userId)) : T("Cli.NotBanned", ("USER", userId)));
    }

    private static void PromoteAdmin(ClashOfRimNetworkState state, string userId)
    {
        bool added = state.WorldConfiguration.PromoteAdministrator(userId);
        state.AdminControl.AddAudit("PromoteAdmin", ActorUserId, userId, null, DateTimeOffset.UtcNow);
        SignalWorldConfigurationUsers(state);
        Console.WriteLine(added ? T("Cli.Promoted", ("USER", userId)) : T("Cli.AlreadyAdmin", ("USER", userId)));
    }

    private static void RevokeAdmin(ClashOfRimNetworkState state, string userId)
    {
        bool removed = state.WorldConfiguration.RevokeAdministrator(userId);
        if (removed)
        {
            state.AdminControl.AddAudit("RevokeAdmin", ActorUserId, userId, null, DateTimeOffset.UtcNow);
            SignalWorldConfigurationUsers(state);
            Console.WriteLine(T("Cli.AdminRevoked", ("USER", userId)));
        }
        else
        {
            Console.WriteLine(T("Cli.AdminRevokeFailed"));
        }
    }

    private static void ResetPassword(ClashOfRimNetworkState state, string userId, string password)
    {
        bool success = state.OfflineAccounts.ResetPassword(userId, password, DateTimeOffset.UtcNow, out string failure);
        if (!success)
        {
            Console.WriteLine(T("Cli.PasswordResetFailed", ("REASON", failure)));
            return;
        }

        state.AdminControl.AddAudit("ResetOfflinePassword", ActorUserId, userId, null, DateTimeOffset.UtcNow);
        Console.WriteLine(T("Cli.PasswordReset", ("USER", userId)));
    }

    private static void PrintPostUploadJobs(ClashOfRimNetworkState state)
    {
        IReadOnlyList<SnapshotPostUploadJobRecord> jobs = state.SnapshotPostUploadJobs.ListManualReview();
        if (jobs.Count == 0)
        {
            Console.WriteLine(T("Cli.NoPostUploadJobs"));
            return;
        }

        foreach (SnapshotPostUploadJobRecord job in jobs)
        {
            Console.WriteLine(T(
                "Cli.PostUploadJobLine",
                ("JOB", job.JobId),
                ("PROCESSOR", job.ProcessorId),
                ("SNAPSHOT", job.SnapshotId),
                ("ATTEMPTS", job.AttemptCount.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("ERROR", job.LastError ?? "-")));
        }
    }

    private static void RetryPostUploadJob(ClashOfRimNetworkState state, string jobId)
    {
        SnapshotPostUploadJobRecord? job = state.SnapshotPostUploadJobs.Find(jobId);
        if (job is null || job.State != SnapshotPostUploadJobState.ManualReview)
        {
            Console.WriteLine(T("Cli.PostUploadJobNotFound", ("JOB", jobId)));
            return;
        }

        state.SnapshotPostUploadJobs.MarkReady(job.JobId, DateTimeOffset.UtcNow);
        state.AdminControl.AddAudit("RetrySnapshotPostUploadJob", ActorUserId, null, job.JobId, DateTimeOffset.UtcNow);
        Console.WriteLine(T("Cli.PostUploadJobRetried", ("JOB", job.JobId)));
    }

    private static void SetRaidCooldown(
        ClashOfRimNetworkState state,
        string userId,
        string hoursText)
    {
        PlayerSessionRecord? player = state.Players.FindByUserId(userId);
        if (player is null || string.IsNullOrWhiteSpace(player.ColonyId))
        {
            Console.WriteLine(T("Cli.PlayerColonyNotFound", ("USER", userId)));
            return;
        }

        if (!double.TryParse(
                hoursText,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out double hours)
            || !double.IsFinite(hours)
            || hours < 0d)
        {
            Console.WriteLine(T("Cli.UsageSetCooldown"));
            return;
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        RaidCooldownOverrideRecord record;
        try
        {
            record = RaidCooldownRuntimeService.SetCurrent(
                state,
                player.UserId,
                player.ColonyId,
                hours,
                nowUtc);
        }
        catch (ArgumentOutOfRangeException)
        {
            Console.WriteLine(T("Cli.UsageSetCooldown"));
            return;
        }

        state.AdminControl.AddAudit(
            "SetRaidCooldown",
            ActorUserId,
            player.UserId,
            $"colony={player.ColonyId};hours={hours.ToString(System.Globalization.CultureInfo.InvariantCulture)};until={record.CooldownUntilUtc:O}",
            nowUtc);
        SignalWorldConfigurationUsers(state);
        Console.WriteLine(hours == 0d
            ? T("Cli.RaidCooldownCleared", ("USER", player.UserId), ("COLONY", player.ColonyId))
            : T(
                "Cli.RaidCooldownSet",
                ("USER", player.UserId),
                ("COLONY", player.ColonyId),
                ("HOURS", hours.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                ("UNTIL", record.CooldownUntilUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture))));
    }

    private static void Broadcast(ClashOfRimNetworkState state, IReadOnlyList<string> args)
    {
        string? targetUserId = null;
        ServerNotificationSeverity severity = ServerNotificationSeverity.Info;
        bool persistent = false;
        var messageParts = new List<string>();
        for (int index = 0; index < args.Count; index++)
        {
            string arg = args[index];
            if (string.Equals(arg, "--target", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Count)
            {
                targetUserId = args[++index];
            }
            else if (string.Equals(arg, "--severity", StringComparison.OrdinalIgnoreCase) && index + 1 < args.Count)
            {
                severity = ParseSeverity(args[++index]);
            }
            else if (string.Equals(arg, "--persistent", StringComparison.OrdinalIgnoreCase))
            {
                persistent = true;
            }
            else
            {
                messageParts.Add(arg);
            }
        }

        string message = string.Join(' ', messageParts).Trim();
        if (string.IsNullOrWhiteSpace(message))
        {
            Console.WriteLine(T("Cli.UsageBroadcast"));
            return;
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        IReadOnlyList<PlayerSessionRecord> targets = state.Players.List()
            .Where(player => string.IsNullOrWhiteSpace(targetUserId)
                || string.Equals(player.UserId, targetUserId, StringComparison.Ordinal))
            .Where(player => persistent || state.OnlinePresence.IsUserOnline(player.UserId))
            .ToList();
        foreach (PlayerSessionRecord player in targets)
        {
            string idempotencyKey = $"cli-broadcast:{player.UserId}:{nowUtc:O}";
            AuthoritativeEvent notification = AuthoritativeEventFactory.Create(
                ServerEventType.ServerNotification,
                new EventParty("server"),
                new EventParty(player.UserId, player.ColonyId),
                idempotencyKey,
                state.OnlinePresence.IsUserOnline(player.UserId),
                new ServerNotificationEventPayload(
                    idempotencyKey,
                    ServerLocalization.Text("Admin.Broadcast.Title"),
                    message,
                    severity,
                    FromAdministrator: true,
                    AdministratorUserId: ActorUserId,
                    OnlineOnly: !persistent),
                nowUtc);
            state.Ledger.Append(notification);
        }

        state.EventNotifications.SignalUsers(targets.Select(player => player.UserId));
        state.AdminControl.AddAudit("Broadcast", ActorUserId, targetUserId, message, nowUtc);
        Console.WriteLine(T("Cli.BroadcastSent", ("COUNT", targets.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))));
    }

    private static void ScheduleShutdown(
        ClashOfRimNetworkState state,
        IHostApplicationLifetime lifetime,
        ILogger logger)
    {
        if (System.Threading.Interlocked.Exchange(ref shutdownScheduled, 1) == 1)
        {
            Console.WriteLine(T("Cli.ShutdownAlreadyScheduled"));
            return;
        }

        DateTimeOffset nowUtc = DateTimeOffset.UtcNow;
        string seconds = ShutdownDelaySeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        IReadOnlyList<PlayerSessionRecord> targets = state.Players.List()
            .Where(player => state.OnlinePresence.IsUserOnline(player.UserId))
            .ToList();

        foreach (PlayerSessionRecord player in targets)
        {
            string idempotencyKey = $"cli-shutdown:{player.UserId}:{nowUtc:O}";
            AuthoritativeEvent notification = AuthoritativeEventFactory.Create(
                ServerEventType.ServerNotification,
                new EventParty("server"),
                new EventParty(player.UserId, player.ColonyId),
                idempotencyKey,
                targetOnline: true,
                new ServerNotificationEventPayload(
                    idempotencyKey,
                    T("Cli.ShutdownNoticeTitle"),
                    T("Cli.ShutdownNoticeMessage", ("SECONDS", seconds)),
                    ServerNotificationSeverity.Warning,
                    FromAdministrator: false,
                    OnlineOnly: true),
                nowUtc);
            state.Ledger.Append(notification);
        }

        state.EventNotifications.SignalUsers(targets.Select(player => player.UserId));
        state.AdminControl.AddAudit(
            "Shutdown",
            ActorUserId,
            null,
            $"delaySeconds={ShutdownDelaySeconds};onlineTargets={targets.Count}",
            nowUtc);
        Console.WriteLine(T(
            "Cli.ShutdownScheduled",
            ("SECONDS", seconds),
            ("COUNT", targets.Count.ToString(System.Globalization.CultureInfo.InvariantCulture))));

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(ShutdownDelaySeconds)).ConfigureAwait(false);
                Console.WriteLine(T("Cli.Shutdown"));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, T("Cli.CommandFailedLog"), "stop");
            }
            finally
            {
                lifetime.StopApplication();
            }
        });
    }

    private static int EndUserSession(ClashOfRimNetworkState state, string userId)
    {
        int affected = 0;
        if (state.LoginSessions.EndUser(userId))
        {
            affected++;
        }

        if (state.OnlinePresence.ForceDisconnect(userId))
        {
            affected++;
        }

        state.AuthTokens.RevokeForUser(userId);
        state.EventNotifications.SignalUser(userId);
        return affected;
    }

    private static void SignalWorldConfigurationUsers(ClashOfRimNetworkState state)
    {
        state.WorldConfigurationNotifications.SignalUsers(state.Players.List().Select(player => player.UserId));
    }

    private static ServerNotificationSeverity ParseSeverity(string value)
    {
        return string.Equals(value, "warning", StringComparison.OrdinalIgnoreCase)
            ? ServerNotificationSeverity.Warning
            : string.Equals(value, "critical", StringComparison.OrdinalIgnoreCase)
                || string.Equals(value, "error", StringComparison.OrdinalIgnoreCase)
                    ? ServerNotificationSeverity.Critical
                    : ServerNotificationSeverity.Info;
    }

    private static void RequireArgs(IReadOnlyList<string> args, int count, string usage)
    {
        if (args.Count < count)
        {
            throw new InvalidOperationException(usage);
        }
    }

    private static string T(string key)
    {
        return ServerLocalization.Text(key);
    }

    private static string T(string key, params (string Key, string? Value)[] args)
    {
        return ServerLocalization.Text(
            key,
            args.ToDictionary(arg => arg.Key, arg => arg.Value, StringComparer.Ordinal));
    }

    private static string JoinArgs(IReadOnlyList<string> args, int startIndex)
    {
        return startIndex >= args.Count ? string.Empty : string.Join(' ', args.Skip(startIndex));
    }

    private static IReadOnlyList<string> SplitCommandLine(string line)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        bool quoted = false;
        for (int index = 0; index < line.Length; index++)
        {
            char ch = line[index];
            if (ch == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts;
    }
}
