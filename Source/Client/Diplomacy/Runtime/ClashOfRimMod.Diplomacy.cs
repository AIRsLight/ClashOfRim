using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.Diplomacy;
using AIRsLight.ClashOfRim.WorldObjects;
using RimWorld;
using Verse;

namespace AIRsLight.ClashOfRim;

public sealed partial class ClashOfRimMod
{
    internal void StartRefreshPlayers()
    {
        StartRefreshPlayers("manual-refresh", requireManualGate: true);
    }

    private void StartRefreshPlayers(string reason, bool requireManualGate)
    {
        if (!CanRunManualSync(out string failureReason))
        {
            if (requireManualGate)
            {
                playerListStatus = failureReason;
                return;
            }

            if (!settings.IsConfigured)
            {
                playerListStatus = failureReason;
                return;
            }
        }

        if (requireManualGate)
        {
            manualSyncInProgress = true;
        }

        playerListStatus = ClashOfRimText.Key("ClashOfRim.Players.StatusRefreshing");
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                ClashOfRimClientNetworkResult<ModListPlayersResponseDto> result = await client.ListPlayersAsync();
                if (!result.Success || result.Response is null)
                {
                    playerListStatus = ClashOfRimText.Key(
                        "ClashOfRim.Players.StatusRefreshFailed",
                        (result.ErrorCode ?? string.Empty).Named("CODE"),
                        (result.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                if (result.Response.Result is not null && !result.Response.Result.Accepted)
                {
                    playerListStatus = ClashOfRimText.Key(
                        "ClashOfRim.Players.StatusRefreshRejected",
                        result.Response.Result.ErrorCode.Named("CODE"),
                        (result.Response.Result.Message ?? string.Empty).Named("MESSAGE"));
                    return;
                }

                lock (eventStateLock)
                {
                    lastPlayers.Clear();
                    lastPlayers.AddRange(result.Response.Players ?? new List<ModPlayerSummaryDto>());
                    playersSnapshotVersion++;
                }

                List<ModPlayerSummaryDto> proxyPlayers = (result.Response.Players ?? new List<ModPlayerSummaryDto>())
                    .Where(player => !string.Equals(player.UserId, settings.UserId, StringComparison.Ordinal))
                    .ToList();
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                    ApplyPlayerFactionProxyRelations(proxyPlayers, "player-list-sync", canSendHostilityLetter: false));

                ModPlayerSummaryDto? firstOther = result.Response.Players
                    .FirstOrDefault(player => !string.Equals(player.UserId, settings.UserId, StringComparison.Ordinal));
                if (firstOther is not null && string.IsNullOrWhiteSpace(settings.TargetUserId))
                {
                    settings.TargetUserId = firstOther.UserId;
                    settings.TargetColonyId = firstOther.ColonyId;
                    settings.TargetSnapshotId = firstOther.CurrentSnapshotId ?? string.Empty;
                    ClashOfRimGameComponent.EnqueueMainThreadAction(settings.Write);
                }

                playerListStatus = ClashOfRimText.Key(
                    "ClashOfRim.Players.StatusRefreshed",
                    FormatPlayers(result.Response.Players).Named("PLAYERS"));
            }
            catch (Exception ex)
            {
                playerListStatus = ClashOfRimText.Key(
                    "ClashOfRim.Players.StatusException",
                    ex.GetType().Name.Named("TYPE"),
                    ex.Message.Named("MESSAGE"));
                Log.Warning("[ClashOfRim] List players failed: " + ex);
            }
            finally
            {
                if (requireManualGate)
                {
                    manualSyncInProgress = false;
                }
            }
        });
    }

    private void ApplyPlayerFactionProxyRelations(
        IEnumerable<ModPlayerSummaryDto> players,
        string reason,
        bool canSendHostilityLetter)
    {
        foreach (ModPlayerSummaryDto player in players)
        {
            if (string.IsNullOrWhiteSpace(player.UserId)
                || string.Equals(player.UserId, settings.UserId, StringComparison.Ordinal))
            {
                continue;
            }

            PlayerFactionProxyUtility.EnsureProxyForUser(player.UserId);
            if (TryResolveServerRelationKind(player.RelationKind, out FactionRelationKind relationKind))
            {
                PlayerFactionProxyUtility.SetPlayerRelation(
                    player.UserId,
                    relationKind,
                    reason,
                    canSendHostilityLetter);
            }
        }
    }

    internal void SelectNextPlayerTarget()
    {
        lock (eventStateLock)
        {
            List<ModPlayerSummaryDto> candidates = lastPlayers
                .Where(player => !string.Equals(player.UserId, settings.UserId, StringComparison.Ordinal))
                .ToList();
            if (candidates.Count == 0)
            {
                playerListStatus = ClashOfRimText.Key("ClashOfRim.Players.StatusNoSelectableTargets");
                return;
            }

            int current = candidates.FindIndex(player => string.Equals(player.UserId, settings.TargetUserId, StringComparison.Ordinal));
            ModPlayerSummaryDto next = candidates[(current + 1 + candidates.Count) % candidates.Count];
            settings.TargetUserId = next.UserId;
            settings.TargetColonyId = next.ColonyId;
            settings.TargetSnapshotId = next.CurrentSnapshotId ?? string.Empty;
            settings.Write();
            playerListStatus = ClashOfRimText.Key(
                "ClashOfRim.Players.StatusSelectedTarget",
                settings.TargetUserId.Named("USER"),
                settings.TargetColonyId.Named("COLONY"));
        }
    }

    internal void SelectPlayerTarget(ModPlayerSummaryDto player)
    {
        if (player is null || string.IsNullOrWhiteSpace(player.UserId) || string.IsNullOrWhiteSpace(player.ColonyId))
        {
            return;
        }

        if (string.Equals(player.UserId, settings.UserId, StringComparison.Ordinal)
            && string.Equals(player.ColonyId, settings.ColonyId, StringComparison.Ordinal))
        {
            playerListStatus = ClashOfRimText.Key("ClashOfRim.Players.StatusCannotSelectSelf");
            return;
        }

        lock (eventStateLock)
        {
            settings.TargetUserId = player.UserId;
            settings.TargetColonyId = player.ColonyId;
            settings.TargetSnapshotId = player.CurrentSnapshotId ?? string.Empty;
            settings.Write();
            playerListStatus = ClashOfRimText.Key(
                "ClashOfRim.Players.StatusSelectedTarget",
                settings.TargetUserId.Named("USER"),
                settings.TargetColonyId.Named("COLONY"));
        }
    }

    internal void StartCreateDiplomacyEvent(string kind, ModPlayerSummaryDto target, string? customMessage = null)
    {
        if (target is null || string.IsNullOrWhiteSpace(target.UserId) || string.IsNullOrWhiteSpace(target.ColonyId))
        {
            return;
        }

        if (string.Equals(target.UserId, settings.UserId, StringComparison.Ordinal)
            && string.Equals(target.ColonyId, settings.ColonyId, StringComparison.Ordinal))
        {
            return;
        }

        lock (eventStateLock)
        {
            settings.TargetUserId = target.UserId;
            settings.TargetColonyId = target.ColonyId;
            settings.TargetSnapshotId = target.CurrentSnapshotId ?? string.Empty;
            settings.Write();
        }

        StartCreateDiplomacyEvent(kind, customMessage, DisplayNameOrUserId(target));
    }

    internal void StartCreateDiplomacyEvent(string kind, string? customMessage = null, string? targetDisplayName = null)
    {
        if (!CanRunManualSync(out string failureReason) || !HasTarget(out failureReason))
        {
            return;
        }

        manualSyncInProgress = true;
        Task.Run(async () =>
        {
            try
            {
                using var httpClient = new HttpClient();
                var client = new ClashOfRimModNetworkClient(
                    httpClient,
                    ClashOfRimClientNetworkContext.FromSettings(settings));
                string idempotencyKey = $"diplomacy:{settings.UserId}:{settings.TargetUserId}:{kind}:{DateTime.UtcNow.Ticks}";
                ClashOfRimClientNetworkResult<ModDiplomacyEventResponseDto> result =
                    await client.CreateDiplomacyEventAsync(
                        idempotencyKey,
                        settings.TargetUserId,
                        settings.TargetColonyId,
                        targetSnapshotId: null,
                        kind,
                        string.IsNullOrWhiteSpace(customMessage) ? DefaultDiplomacyMessage(kind) : customMessage,
                        kind.Equals("WarDeclaration", StringComparison.OrdinalIgnoreCase)
                            || kind.Equals("AllianceCancellation", StringComparison.OrdinalIgnoreCase)
                            || kind.Equals("SupportRequest", StringComparison.OrdinalIgnoreCase)
                            ? null
                            : DateTimeOffset.UtcNow.AddDays(3));

                if (!result.Success || result.Response is null)
                {
                    return;
                }

                ModProtocolResponseDto? serverResult = result.Response.Result;
                if (serverResult is not null && !serverResult.Accepted)
                {
                    RequestDiplomacyStateRefresh(ClashOfRimText.Key("ClashOfRim.Diplomacy.RefreshReasonRelationChanged"));
                    return;
                }

                string targetUserId = settings.TargetUserId;
                FactionRelationKind? immediateRelationKind = kind switch
                {
                    "AllianceCancellation" => FactionRelationKind.Neutral,
                    "WarDeclaration" => FactionRelationKind.Hostile,
                    _ => null
                };
                if (immediateRelationKind is FactionRelationKind relationKind)
                {
                    ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                    {
                        PlayerFactionProxyUtility.SetPlayerRelation(
                            targetUserId,
                            relationKind,
                            DiplomacyRelationReason(kind));
                    });
                }

                RequestDiplomacyStateRefresh(ClashOfRimText.Key("ClashOfRim.Diplomacy.RefreshReasonRelationChanged"));
                string targetName = string.IsNullOrWhiteSpace(targetDisplayName)
                    ? settings.TargetUserId
                    : targetDisplayName!;
                ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
                    Messages.Message(
                        FormatDiplomacySuccessMessage(kind, targetName),
                        MessageTypeDefOf.NeutralEvent,
                        historical: false));
            }
            catch (Exception ex)
            {
                Log.Warning("[ClashOfRim] Diplomacy event creation failed: " + ex);
            }
            finally
            {
                manualSyncInProgress = false;
            }
        });
    }

    private static string DisplayNameOrUserId(ModPlayerSummaryDto player)
    {
        if (!string.IsNullOrWhiteSpace(player.DisplayName))
        {
            return player.DisplayName!;
        }

        return string.IsNullOrWhiteSpace(player.UserId)
            ? ClashOfRimText.Key("ClashOfRim.UnknownPlayer")
            : player.UserId;
    }

    private static string FormatDiplomacySuccessMessage(string kind, string targetName)
    {
        return kind switch
        {
            "AllianceRequest" => ClashOfRimText.Key("ClashOfRim.Diplomacy.MessageAllianceRequestSent", targetName.Named("PLAYER")),
            "AllianceCancellation" => ClashOfRimText.Key("ClashOfRim.Diplomacy.MessageAllianceCancellationSent", targetName.Named("PLAYER")),
            "WarDeclaration" => ClashOfRimText.Key("ClashOfRim.Diplomacy.MessageWarDeclarationSent", targetName.Named("PLAYER")),
            "PeaceRequest" => ClashOfRimText.Key("ClashOfRim.Diplomacy.MessagePeaceRequestSent", targetName.Named("PLAYER")),
            "SupportRequest" => ClashOfRimText.Key("ClashOfRim.Diplomacy.MessageSupportRequestSent", targetName.Named("PLAYER")),
            _ => ClashOfRimText.Key("ClashOfRim.Diplomacy.MessageEventSent", FormatDiplomacyKind(kind).Named("KIND"), targetName.Named("PLAYER"))
        };
    }

    private void RequestDiplomacyStateRefresh(string reason)
    {
        ClashOfRimGameComponent.EnqueueMainThreadAction(() =>
        {
            StartRefreshPlayers(reason, requireManualGate: false);
            RequestWorldMapMarkerRefresh(reason);
        });
    }

}
