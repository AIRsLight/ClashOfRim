using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using AIRsLight.ClashOfRim.ClientNetwork;
using AIRsLight.ClashOfRim.ClientSnapshots;
using AIRsLight.ClashOfRim.Pawns;
using AIRsLight.ClashOfRim.Raids;
using AIRsLight.ClashOfRim.RemoteMaps;
using AIRsLight.ClashOfRim.Support;
using AIRsLight.ClashOfRim.WorldObjects;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace AIRsLight.ClashOfRim;

public sealed class ClashOfRimGameComponent : GameComponent
{
    private const float WorldMapMarkerRefreshIntervalSeconds = 30f;
    private const int RuntimeWorldObjectLeaseRefreshTicks = 2500;
    private const int RuntimeWorldObjectChangeCheckTicks = 250;
    private const int RemoteWorldObjectOrphanCleanupTicks = 250;
    private static readonly ConcurrentQueue<Action> MainThreadActions = new();
    private static readonly ConcurrentQueue<DelayedMainThreadAction> DelayedMainThreadActions = new();
    private static readonly object DelayedMainThreadActionsGate = new();
    private static readonly List<WorldObject> RuntimeSignatureWorldObjects = new();
    private static readonly StringBuilder RuntimeSignatureBuilder = new(512);
    private static readonly WorldObjectLoadIdComparer RuntimeSignatureWorldObjectComparer = new();
    private static int nextDelayedMainThreadActionTick = int.MaxValue;
    private List<string> pendingManualEventIds = new();
    private List<PendingAchievementCandidateRecord> pendingAchievementCandidates = new();
    private List<ActiveSupportPawnAssignment> activeSupportAssignments = new();
    private List<RemoteMapThingIdentityRecord> remoteMapThingIdentities = new();
    private ActiveRemoteMapSession? activeRemoteMapSession;
    private ActiveRaidBattleSession? activeRaidBattleSession;
    private string activeObservationSessionId = string.Empty;
    private string activeObservationMode = string.Empty;
    private string activeObservationTargetUserId = string.Empty;
    private string activeObservationTargetColonyId = string.Empty;
    private string activeObservationTargetSnapshotId = string.Empty;
    private string lineageSnapshotId = string.Empty;
    private string lineageToken = string.Empty;
    private int nextRuntimeWorldObjectSyncTick;
    private int nextRuntimeWorldObjectSyncCheckTick;
    private int nextPlayerColonySiteRegistrationTick;
    private int nextCaravanArrivalTargetRefreshCheckTick;
    private int nextRemoteWorldObjectOrphanCleanupTick;
    private string lastPlayerCaravanTileSignature = string.Empty;
    private string lastRuntimeWorldObjectSyncSignature = string.Empty;
    private bool automaticSessionAttempted;
    private bool worldMapWasSelected;
    private bool chatOverlayMinimized = true;
    private string chatInput = string.Empty;
    private Vector2 chatScrollPosition;
    private int lastChatOverlayMessageCount;
    private int cachedChatMessagesVersion = -1;
    private IReadOnlyList<ModChatMessageDto> cachedChatMessages = new List<ModChatMessageDto>();
    private float nextWorldMapMarkerRefreshAt;
    private float nextChatRefreshAt;

    public ClashOfRimGameComponent(Game game)
    {
    }

    public override void GameComponentOnGUI()
    {
        RunMainThreadActions();
        ClashOfRimMod? mod = LoadedModManager.GetMod<ClashOfRimMod>();
        TryFinishExpiredRaidBattle(mod);
        TryHandleRaidBattleFinalDeadline(mod);
        TryRefreshWorldMapMarkers(mod);
        DrawRemoteMapSessionOverlay(mod);
        DrawChatOverlay(mod);
    }

    public override void GameComponentUpdate()
    {
        ClashOfRimMod? mod = LoadedModManager.GetMod<ClashOfRimMod>();
        TryFinishExpiredRaidBattle(mod);
        TryHandleRaidBattleFinalDeadline(mod);
    }

    public override void GameComponentTick()
    {
        RunMainThreadActions();
        int ticks = Find.TickManager?.TicksGame ?? 0;
        ClashOfRimMod? mod = LoadedModManager.GetMod<ClashOfRimMod>();
        TryStartAutomaticRuntimeSession(mod);
        TryRegisterPlayerColonySites(mod, ticks);
        TrySyncRuntimeWorldObjects(mod, ticks);
        TryRefreshCaravanArrivalTargets(mod, ticks);
        TryCleanupRemoteWorldObjectOrphans(ticks);
        TryFinishExpiredRaidBattle(mod);
        TryHandleRaidBattleFinalDeadline(mod);

    }

    public override void ExposeData()
    {
        base.ExposeData();
        Scribe_Collections.Look(ref pendingManualEventIds, "clashOfRimPendingManualEventIds", LookMode.Value);
        Scribe_Collections.Look(ref pendingAchievementCandidates, "clashOfRimPendingAchievementCandidates", LookMode.Deep);
        Scribe_Collections.Look(ref activeSupportAssignments, "clashOfRimActiveSupportAssignments", LookMode.Deep);
        Scribe_Collections.Look(ref remoteMapThingIdentities, "clashOfRimRemoteMapThingIdentities", LookMode.Deep);
        Scribe_Deep.Look(ref activeRemoteMapSession, "clashOfRimActiveRemoteMapSession");
        Scribe_Deep.Look(ref activeRaidBattleSession, "clashOfRimActiveRaidBattleSession");
        Scribe_Values.Look(ref activeObservationSessionId, "clashOfRimActiveObservationSessionId", string.Empty);
        Scribe_Values.Look(ref activeObservationMode, "clashOfRimActiveObservationMode", string.Empty);
        Scribe_Values.Look(ref activeObservationTargetUserId, "clashOfRimActiveObservationTargetUserId", string.Empty);
        Scribe_Values.Look(ref activeObservationTargetColonyId, "clashOfRimActiveObservationTargetColonyId", string.Empty);
        Scribe_Values.Look(ref activeObservationTargetSnapshotId, "clashOfRimActiveObservationTargetSnapshotId", string.Empty);
        Scribe_Values.Look(ref lineageSnapshotId, "clashOfRimLineageSnapshotId", string.Empty);
        Scribe_Values.Look(ref lineageToken, "clashOfRimLineageToken", string.Empty);
        if (Scribe.mode == LoadSaveMode.PostLoadInit && pendingManualEventIds is null)
        {
            pendingManualEventIds = new List<string>();
        }
        if (Scribe.mode == LoadSaveMode.PostLoadInit && pendingAchievementCandidates is null)
        {
            pendingAchievementCandidates = new List<PendingAchievementCandidateRecord>();
        }
        if (Scribe.mode == LoadSaveMode.PostLoadInit && activeSupportAssignments is null)
        {
            activeSupportAssignments = new List<ActiveSupportPawnAssignment>();
        }
        if (Scribe.mode == LoadSaveMode.PostLoadInit && remoteMapThingIdentities is null)
        {
            remoteMapThingIdentities = new List<RemoteMapThingIdentityRecord>();
        }
        if (Scribe.mode == LoadSaveMode.PostLoadInit)
        {
            lineageSnapshotId ??= string.Empty;
            lineageToken ??= string.Empty;
            activeObservationSessionId ??= string.Empty;
            activeObservationMode ??= string.Empty;
            activeObservationTargetUserId ??= string.Empty;
            activeObservationTargetColonyId ??= string.Empty;
            activeObservationTargetSnapshotId ??= string.Empty;
        }
    }

    internal static void EnqueueMainThreadAction(Action action)
    {
        if (action is not null)
        {
            MainThreadActions.Enqueue(action);
        }
    }

    internal static void EnqueueMainThreadActionAfterTicks(Action action, int delayTicks)
    {
        if (action is null)
        {
            return;
        }

        if (delayTicks <= 0)
        {
            EnqueueMainThreadAction(action);
            return;
        }

        int currentTicks = Find.TickManager?.TicksGame ?? 0;
        int dueTick = currentTicks + delayTicks;
        lock (DelayedMainThreadActionsGate)
        {
            DelayedMainThreadActions.Enqueue(new DelayedMainThreadAction(dueTick, action));
            if (dueTick < nextDelayedMainThreadActionTick)
            {
                nextDelayedMainThreadActionTick = dueTick;
            }
        }
    }

    internal static void RegisterPendingManualEvent(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return;
        }

        ClashOfRimGameComponent? component = Current;
        if (component is null)
        {
            return;
        }

        bool alreadyPending = false;
        foreach (string pendingEventId in component.pendingManualEventIds)
        {
            if (string.Equals(pendingEventId, eventId, StringComparison.Ordinal))
            {
                alreadyPending = true;
                break;
            }
        }

        if (!alreadyPending)
        {
            component.pendingManualEventIds.Add(eventId);
        }
    }

    internal static void MarkManualEventHandled(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return;
        }

        ClashOfRimGameComponent? component = Current;
        component?.pendingManualEventIds.RemoveAll(id => string.Equals(id, eventId, StringComparison.Ordinal));
    }

    internal static IReadOnlyList<string> CopyPendingManualEventIds()
    {
        ClashOfRimGameComponent? component = Current;
        if (component is null || component.pendingManualEventIds.Count == 0)
        {
            return new List<string>();
        }

        var result = new List<string>(component.pendingManualEventIds.Count);
        HashSet<string>? seen = null;
        foreach (string id in component.pendingManualEventIds)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            seen ??= new HashSet<string>(StringComparer.Ordinal);
            if (seen.Add(id))
            {
                result.Add(id);
            }
        }

        return result;
    }

    internal static IReadOnlyList<ModSnapshotAchievementCandidateDto> EnqueuePendingAchievementCandidates(
        IEnumerable<ModSnapshotAchievementCandidateDto> candidates)
    {
        ClashOfRimGameComponent? component = Current;
        if (component is null)
        {
            return new List<ModSnapshotAchievementCandidateDto>();
        }

        var added = new List<ModSnapshotAchievementCandidateDto>();
        foreach (ModSnapshotAchievementCandidateDto candidate in candidates ?? Array.Empty<ModSnapshotAchievementCandidateDto>())
        {
            var record = new PendingAchievementCandidateRecord(candidate);
            if (!record.IsValid)
            {
                continue;
            }

            string key = record.StableKey;
            bool exists = component.pendingAchievementCandidates.Any(existing =>
                existing is not null
                && string.Equals(existing.StableKey, key, StringComparison.Ordinal));
            if (exists)
            {
                continue;
            }

            component.pendingAchievementCandidates.Add(record);
            added.Add(record.ToDto());
        }

        return added;
    }

    internal static IReadOnlyList<ModSnapshotAchievementCandidateDto> CopyPendingAchievementCandidates()
    {
        ClashOfRimGameComponent? component = Current;
        if (component is null || component.pendingAchievementCandidates.Count == 0)
        {
            return new List<ModSnapshotAchievementCandidateDto>();
        }

        var result = new List<ModSnapshotAchievementCandidateDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (PendingAchievementCandidateRecord record in component.pendingAchievementCandidates)
        {
            if (record is null || !record.IsValid || !seen.Add(record.StableKey))
            {
                continue;
            }

            result.Add(record.ToDto());
        }

        return result;
    }

    internal static void MarkPendingAchievementCandidatesUploaded(IEnumerable<ModSnapshotAchievementCandidateDto> candidates)
    {
        ClashOfRimGameComponent? component = Current;
        if (component is null || component.pendingAchievementCandidates.Count == 0)
        {
            return;
        }

        var uploadedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (ModSnapshotAchievementCandidateDto candidate in candidates ?? Array.Empty<ModSnapshotAchievementCandidateDto>())
        {
            if (string.IsNullOrWhiteSpace(candidate.AchievementId) || string.IsNullOrWhiteSpace(candidate.EventKey))
            {
                continue;
            }

            uploadedKeys.Add(candidate.AchievementId.Trim() + ":" + candidate.EventKey.Trim());
        }

        if (uploadedKeys.Count == 0)
        {
            return;
        }

        component.pendingAchievementCandidates.RemoveAll(record =>
            record is null
            || !record.IsValid
            || uploadedKeys.Contains(record.StableKey));
    }

    internal static void SetSnapshotLineage(string? snapshotId, string? token)
    {
        ClashOfRimGameComponent? component = Current;
        if (component is null)
        {
            return;
        }

        component.lineageSnapshotId = snapshotId ?? string.Empty;
        component.lineageToken = token ?? string.Empty;
    }

    internal static bool TrySaveCurrentGameToBytes(out byte[] saveBytes, out string failureReason)
    {
        return TrySaveCurrentGameToBytes(removeRaidBattleSessions: false, out saveBytes, out failureReason);
    }

    internal static bool TrySaveCurrentGameToBytes(
        bool removeRaidBattleSessions,
        out byte[] saveBytes,
        out string failureReason)
    {
        saveBytes = Array.Empty<byte>();
        failureReason = string.Empty;
        if (Verse.Current.Game is null)
        {
            failureReason = ClashOfRimText.Key("ClashOfRim.Status.NoRunningGame");
            return false;
        }

        try
        {
            using var stream = new MemoryStream();
            var writerSettings = new System.Xml.XmlWriterSettings
            {
                Indent = true,
                IndentChars = "\t",
                CloseOutput = false
            };
            using (System.Xml.XmlWriter writer = System.Xml.XmlWriter.Create(stream, writerSettings))
            {
                InitScribeMemorySaving(writer);
                try
                {
                    SnapshotRuntimeLoadIdSanitizer.NormalizeHediffLoadIdsForCurrentGame();
                    writer.WriteStartDocument();
                    Scribe.saver.EnterNode("savegame");
                    ScribeMetaHeaderUtility.WriteMetaHeader();
                    Game target = Verse.Current.Game;
                    Scribe_Deep.Look(ref target, "game");
                    Scribe.saver.FinalizeSaving();
                }
                catch
                {
                    Scribe.saver.ForceStop();
                    throw;
                }
            }

            saveBytes = stream.ToArray();
            if (!removeRaidBattleSessions)
            {
                IReadOnlyList<RemoteMapThingIdentityRecord> identities = CopyRemoteMapThingIdentities();
                saveBytes = RemoteMapThingIdentitySnapshotInjector.Inject(saveBytes, identities, out int injectedIdentities);
                if (injectedIdentities > 0 && Prefs.DevMode)
                {
                    ClashLog.Message("[ClashOfRim][RemoteMapIdentity] Injected "
                        + injectedIdentities
                        + " remote thing identities into snapshot save.");
                }
            }

            saveBytes = SnapshotSaveSanitizer.RemoveTransientObservationState(
                saveBytes,
                out _,
                removeRaidBattleSessions);
            (string snapshotId, string token) = CopySnapshotLineage();
            saveBytes = SnapshotSaveSanitizer.EnsureLineageMarker(saveBytes, snapshotId, token);

            return true;
        }
        catch (Exception ex)
        {
            failureReason = $"{ex.GetType().Name} {ex.Message}";
            Log.Warning("[ClashOfRim] Failed to save current game to memory: " + ex);
            return false;
        }
    }

    private static void InitScribeMemorySaving(System.Xml.XmlWriter writer)
    {
        if (Scribe.mode != LoadSaveMode.Inactive)
        {
            Log.Error("Called ClashOfRim memory InitSaving() but current mode is " + Scribe.mode);
            Scribe.ForceStop();
        }

        const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
        Type saverType = Scribe.saver.GetType();
        saverType.GetField("writer", flags)?.SetValue(Scribe.saver, writer);
        saverType.GetField("saveStream", flags)?.SetValue(Scribe.saver, null);
        saverType.GetField("curPath", flags)?.SetValue(Scribe.saver, null);
        saverType.GetField("nextListElementTemporaryId", flags)?.SetValue(Scribe.saver, 0);
        saverType.GetField("anyInternalException", flags)?.SetValue(Scribe.saver, false);
        saverType.GetField("savingForDebug", BindingFlags.Public | BindingFlags.Instance)?.SetValue(Scribe.saver, false);
        object? savedNodes = saverType.GetField("savedNodes", flags)?.GetValue(Scribe.saver);
        savedNodes?.GetType().GetMethod("Clear", Type.EmptyTypes)?.Invoke(savedNodes, null);

        Scribe.saver.loadIDsErrorsChecker.Clear();
        Scribe.mode = LoadSaveMode.Saving;
    }

    internal static (string SnapshotId, string Token) CopySnapshotLineage()
    {
        ClashOfRimGameComponent? component = Current;
        return component is null
            ? (string.Empty, string.Empty)
            : (component.lineageSnapshotId ?? string.Empty, component.lineageToken ?? string.Empty);
    }

    internal static bool HasActiveRaidBattleSession =>
        Current?.activeRaidBattleSession is not null;

    internal static ActiveRaidBattleSession? ActiveRaidBattleSession =>
        Current?.activeRaidBattleSession;

    internal static ActiveRemoteMapSession? ActiveRemoteMapSession =>
        Current?.activeRemoteMapSession;

    internal static bool HasActiveRemoteMapSession =>
        Current?.activeRemoteMapSession?.IsActive == true;

    internal static bool HasActiveObservationSession =>
        Current?.activeRemoteMapSession?.IsObservation == true
        || !string.IsNullOrWhiteSpace(Current?.activeObservationSessionId);

    internal static void SetActiveObservationSession(
        string sessionId,
        string mode,
        string targetUserId,
        string targetColonyId,
        string targetSnapshotId,
        string? relatedEventId = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        ClashOfRimGameComponent? component = Current;
        if (component is null)
        {
            return;
        }

        component.activeObservationSessionId = sessionId;
        component.activeObservationMode = mode ?? string.Empty;
        component.activeObservationTargetUserId = targetUserId ?? string.Empty;
        component.activeObservationTargetColonyId = targetColonyId ?? string.Empty;
        component.activeObservationTargetSnapshotId = targetSnapshotId ?? string.Empty;
        component.activeRemoteMapSession = ActiveRemoteMapSession.FromObservation(
            sessionId,
            component.activeObservationMode,
            component.activeObservationTargetUserId,
            component.activeObservationTargetColonyId,
            component.activeObservationTargetSnapshotId,
            relatedEventId);
        component.activeRaidBattleSession = null;
    }

    internal static void SetActiveObservationSession(ActiveRemoteMapSession session)
    {
        if (session is null || !session.IsObservation || string.IsNullOrWhiteSpace(session.SessionId))
        {
            return;
        }

        ClashOfRimGameComponent? component = Current;
        if (component is null)
        {
            return;
        }

        component.activeObservationSessionId = session.SessionId;
        component.activeObservationMode = session.Mode ?? string.Empty;
        component.activeObservationTargetUserId = session.TargetUserId ?? string.Empty;
        component.activeObservationTargetColonyId = session.TargetColonyId ?? string.Empty;
        component.activeObservationTargetSnapshotId = session.TargetSnapshotId ?? string.Empty;
        component.activeRemoteMapSession = session;
        component.activeRaidBattleSession = null;
    }

    internal static void ClearActiveObservationSession(string? sessionId = null)
    {
        ClashOfRimGameComponent? component = Current;
        if (component is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(sessionId)
            && !string.Equals(component.activeObservationSessionId, sessionId, StringComparison.Ordinal))
        {
            return;
        }

        string previousSessionId = component.activeObservationSessionId ?? string.Empty;
        string previousRelatedEventId = component.activeRemoteMapSession?.RelatedEventId ?? string.Empty;
        component.activeObservationSessionId = string.Empty;
        component.activeObservationMode = string.Empty;
        component.activeObservationTargetUserId = string.Empty;
        component.activeObservationTargetColonyId = string.Empty;
        component.activeObservationTargetSnapshotId = string.Empty;
        if (component.activeRemoteMapSession?.IsObservation == true
            && (string.IsNullOrWhiteSpace(sessionId)
                || string.Equals(component.activeRemoteMapSession.SessionId, sessionId, StringComparison.Ordinal)))
        {
            component.activeRemoteMapSession = null;
        }

        RemoveRemoteMapThingIdentities(
            sessionId: string.IsNullOrWhiteSpace(sessionId) ? previousSessionId : sessionId,
            relatedEventId: previousRelatedEventId);
    }

    internal static void SetActiveRaidBattleSession(ActiveRaidBattleSession session)
    {
        if (session is null || string.IsNullOrWhiteSpace(session.EventId))
        {
            return;
        }

        ClashOfRimGameComponent? component = Current;
        if (component is not null)
        {
            component.activeRaidBattleSession = session;
            component.activeRemoteMapSession = ActiveRemoteMapSession.FromRaidBattle(session);
            component.activeObservationSessionId = string.Empty;
            component.activeObservationMode = string.Empty;
            component.activeObservationTargetUserId = string.Empty;
            component.activeObservationTargetColonyId = string.Empty;
            component.activeObservationTargetSnapshotId = string.Empty;
        }
    }

    internal static void ClearActiveRaidBattleSession(string eventId)
    {
        ClashOfRimGameComponent? component = Current;
        if (component?.activeRaidBattleSession is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(eventId)
            || string.Equals(component.activeRaidBattleSession.EventId, eventId, StringComparison.Ordinal))
        {
            string previousEventId = component.activeRaidBattleSession.EventId ?? string.Empty;
            component.activeRaidBattleSession = null;
            if (component.activeRemoteMapSession?.IsRaidBattle == true
                && (string.IsNullOrWhiteSpace(eventId)
                    || string.Equals(component.activeRemoteMapSession.RelatedEventId, eventId, StringComparison.Ordinal)))
            {
                component.activeRemoteMapSession = null;
            }

            RemoveRemoteMapThingIdentities(
                relatedEventId: string.IsNullOrWhiteSpace(eventId) ? previousEventId : eventId);
        }
    }

    internal static void RegisterRemoteMapThingIdentities(
        RemoteSessionMapParent? parent,
        string? mapUniqueId,
        IEnumerable<RemoteMapProjectedThingIdentity>? identities)
    {
        if (parent is null || identities is null)
        {
            return;
        }

        string normalizedMapId = NormalizeRemoteMapId(mapUniqueId);
        if (string.IsNullOrWhiteSpace(normalizedMapId))
        {
            return;
        }

        ClashOfRimGameComponent? component = Current;
        if (component is null)
        {
            return;
        }

        component.remoteMapThingIdentities.RemoveAll(record =>
            string.Equals(NormalizeRemoteMapId(record.MapUniqueId), normalizedMapId, StringComparison.Ordinal)
            && ((string.IsNullOrWhiteSpace(parent.SessionId) || string.Equals(record.SessionId, parent.SessionId, StringComparison.Ordinal))
                || (string.IsNullOrWhiteSpace(parent.RelatedEventId) || string.Equals(record.RelatedEventId, parent.RelatedEventId, StringComparison.Ordinal))));

        int added = 0;
        foreach (RemoteMapProjectedThingIdentity identity in identities)
        {
            string projectedThingId = NormalizeRemoteThingId(identity.ProjectedThingId);
            string originalThingId = NormalizeRemoteThingId(identity.OriginalThingId);
            if (string.IsNullOrWhiteSpace(projectedThingId) || string.IsNullOrWhiteSpace(originalThingId))
            {
                continue;
            }

            component.remoteMapThingIdentities.Add(new RemoteMapThingIdentityRecord
            {
                SessionId = parent.SessionId ?? string.Empty,
                RelatedEventId = parent.RelatedEventId ?? string.Empty,
                Mode = parent.Mode ?? string.Empty,
                SourceMapId = NormalizeRemoteMapId(parent.SourceMapId),
                MapUniqueId = normalizedMapId,
                ProjectedThingId = projectedThingId,
                OriginalThingId = originalThingId
            });
            added++;
        }

        if (added > 0 && Prefs.DevMode)
        {
            ClashLog.Message("[ClashOfRim][RemoteMapIdentity] Registered "
                + added
                + " remote thing identities for map "
                + normalizedMapId
                + ", session="
                + (parent.SessionId ?? string.Empty)
                + ", event="
                + (parent.RelatedEventId ?? string.Empty)
                + ".");
        }
    }

    internal static void RemoveRemoteMapThingIdentities(
        string? sessionId = null,
        string? relatedEventId = null,
        string? mapUniqueId = null)
    {
        ClashOfRimGameComponent? component = Current;
        if (component is null || component.remoteMapThingIdentities.Count == 0)
        {
            return;
        }

        string normalizedMapId = NormalizeRemoteMapId(mapUniqueId);
        if (string.IsNullOrWhiteSpace(sessionId)
            && string.IsNullOrWhiteSpace(relatedEventId)
            && string.IsNullOrWhiteSpace(normalizedMapId))
        {
            return;
        }

        component.remoteMapThingIdentities.RemoveAll(record =>
            (!string.IsNullOrWhiteSpace(sessionId) && string.Equals(record.SessionId, sessionId, StringComparison.Ordinal))
            || (!string.IsNullOrWhiteSpace(relatedEventId) && string.Equals(record.RelatedEventId, relatedEventId, StringComparison.Ordinal))
            || (!string.IsNullOrWhiteSpace(normalizedMapId) && string.Equals(NormalizeRemoteMapId(record.MapUniqueId), normalizedMapId, StringComparison.Ordinal)));
    }

    internal static IReadOnlyList<RemoteMapThingIdentityRecord> CopyRemoteMapThingIdentities()
    {
        ClashOfRimGameComponent? component = Current;
        if (component is null || component.remoteMapThingIdentities.Count == 0)
        {
            return Array.Empty<RemoteMapThingIdentityRecord>();
        }

        return component.remoteMapThingIdentities
            .Where(record => !string.IsNullOrWhiteSpace(record.MapUniqueId)
                && !string.IsNullOrWhiteSpace(record.ProjectedThingId)
                && !string.IsNullOrWhiteSpace(record.OriginalThingId))
            .Select(record => record.Clone())
            .ToList();
    }

    private static string NormalizeRemoteMapId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string trimmed = value!.Trim();
        return trimmed.StartsWith("Map_", StringComparison.Ordinal)
            ? trimmed.Substring("Map_".Length)
            : trimmed;
    }

    private static string NormalizeRemoteThingId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string trimmed = value!.Trim();
        return trimmed.StartsWith("Thing_", StringComparison.Ordinal)
            ? trimmed.Substring("Thing_".Length)
            : trimmed;
    }

    internal static void MarkRaidBattleFinishInProgress(string eventId, bool inProgress)
    {
        ClashOfRimGameComponent? component = Current;
        if (component?.activeRaidBattleSession is null)
        {
            return;
        }

        if (string.Equals(component.activeRaidBattleSession.EventId, eventId, StringComparison.Ordinal))
        {
            component.activeRaidBattleSession.FinishInProgress = inProgress;
        }
    }

    internal static bool TryStartFinishActiveRaidBattleFromUi(ClashOfRimMod? mod, string finishReason)
    {
        return RemoteMapSessionController.TryRequestRaidBattleFinish(
            mod,
            ActiveRaidBattleSession,
            finishReason,
            requireConfirmation: true);
    }

    internal static bool TryHandleRaidBattleMapRemovalCheck(MapParent? mapParent)
    {
        if (mapParent is null || !mapParent.HasMap)
        {
            return false;
        }

        if (!TryGetActiveRaidBattleSessionForMap(mapParent.Map, out ActiveRaidBattleSession? session)
            || session is null)
        {
            return false;
        }

        if (session.FinishInProgress)
        {
            return true;
        }

        if (!mapParent.ShouldRemoveMapNow(out _))
        {
            return true;
        }

        return TryStartRaidBattleFinishFromMapRemoval(session, "MapRemoval", "vanilla map removal");
    }

    internal static bool TryHandleRaidBattleDirectMapRemoval(Map? map)
    {
        if (map?.Parent is not RemoteSessionMapParent parent || !parent.IsRaidBattle)
        {
            return false;
        }

        if (RemoteSessionMapUtility.ExplicitRaidBattleMapCloseInProgress)
        {
            return false;
        }

        if (!TryGetActiveRaidBattleSessionForMap(map, out ActiveRaidBattleSession? session)
            || session is null)
        {
            return false;
        }

        if (session.FinishInProgress)
        {
            ClashLog.Message("[ClashOfRim][Raid] Blocked duplicate direct raid map removal while settlement upload is already in progress: "
                + session.EventId);
            return true;
        }

        return TryStartRaidBattleFinishFromMapRemoval(session, "DirectMapRemoval", "direct map removal");
    }

    private static bool TryGetActiveRaidBattleSessionForMap(Map? map, out ActiveRaidBattleSession? session)
    {
        session = null;
        if (map is null)
        {
            return false;
        }

        ClashOfRimGameComponent? component = Current;
        session = component?.activeRaidBattleSession;
        if (session is null)
        {
            return false;
        }

        string runtimeMapId = string.IsNullOrWhiteSpace(session.ClientMapId)
            ? session.TargetMapId
            : session.ClientMapId;
        if (!MapIdsMatch("Map_" + map.uniqueID, runtimeMapId))
        {
            session = null;
            return false;
        }

        return true;
    }

    private static bool TryStartRaidBattleFinishFromMapRemoval(
        ActiveRaidBattleSession session,
        string finishReason,
        string source)
    {
        ClashOfRimMod? mod = LoadedModManager.GetMod<ClashOfRimMod>();
        if (mod is null)
        {
            Log.Warning("[ClashOfRim][Raid] "
                + source
                + " reached active raid map but mod instance was unavailable: "
                + session.EventId);
            return true;
        }

        Messages.Message(
            "ClashOfRim.Raid.AutoFinishNoControllableUnits".Translate(),
            MessageTypeDefOf.NeutralEvent,
            historical: false);
        ClashLog.Message("[ClashOfRim][Raid] Intercepted "
            + source
            + " and started raid settlement upload: "
            + session.EventId);
        RemoteMapSessionController.TryRequestRaidBattleFinish(mod, session, finishReason);
        return true;
    }

    internal static void RegisterSupportAssignment(ActiveSupportPawnAssignment assignment)
    {
        if (assignment is null || string.IsNullOrWhiteSpace(assignment.EventId) || string.IsNullOrWhiteSpace(assignment.PawnThingId))
        {
            return;
        }

        ClashOfRimGameComponent? component = Current;
        if (component is null)
        {
            return;
        }

        component.activeSupportAssignments.RemoveAll(existing =>
            string.Equals(existing.EventId, assignment.EventId, StringComparison.Ordinal)
            || string.Equals(existing.PawnThingId, assignment.PawnThingId, StringComparison.Ordinal));
        component.activeSupportAssignments.Add(assignment);
    }

    internal static ActiveSupportPawnAssignment? FindSupportAssignment(Pawn pawn)
    {
        if (pawn is null)
        {
            return null;
        }

        ClashOfRimGameComponent? component = Current;
        if (component is null)
        {
            return null;
        }

        foreach (ActiveSupportPawnAssignment assignment in component.activeSupportAssignments)
        {
            if (string.Equals(assignment.PawnThingId, pawn.ThingID, StringComparison.Ordinal))
            {
                return assignment;
            }
        }

        return null;
    }

    internal ActiveSupportPawnAssignment? FindSupportAssignmentByEventId(string eventId)
    {
        if (string.IsNullOrWhiteSpace(eventId))
        {
            return null;
        }

        foreach (ActiveSupportPawnAssignment assignment in activeSupportAssignments)
        {
            if (string.Equals(assignment.EventId, eventId, StringComparison.Ordinal))
            {
                return assignment;
            }
        }

        return null;
    }

    internal bool HasSupportAssignment(string eventId)
    {
        return FindSupportAssignmentByEventId(eventId) is not null;
    }

    internal IReadOnlyList<ActiveSupportPawnAssignment> CopyActiveSupportAssignments()
    {
        return new List<ActiveSupportPawnAssignment>(activeSupportAssignments);
    }

    internal void MarkSupportAssignmentFinished(string eventId)
    {
        activeSupportAssignments.RemoveAll(assignment => string.Equals(assignment.EventId, eventId, StringComparison.Ordinal));
    }

    internal void MarkSupportAssignmentInProgress(string eventId, bool inProgress)
    {
        ActiveSupportPawnAssignment? assignment = FindSupportAssignmentByEventId(eventId);
        if (assignment is not null)
        {
            assignment.FinishInProgress = inProgress;
        }
    }

    private static ClashOfRimGameComponent? Current => Verse.Current.Game?.GetComponent<ClashOfRimGameComponent>();

    private void TryStartAutomaticRuntimeSession(ClashOfRimMod? mod)
    {
        if (automaticSessionAttempted
            || mod?.CanStartAutomaticMapServerSession != true)
        {
            return;
        }

        automaticSessionAttempted = true;
        mod.StartMapServerSession();
    }

    private void TrySyncRuntimeWorldObjects(ClashOfRimMod? mod, int ticks)
    {
        if (ticks < nextRuntimeWorldObjectSyncCheckTick && ticks < nextRuntimeWorldObjectSyncTick)
        {
            return;
        }

        nextRuntimeWorldObjectSyncCheckTick = ticks + RuntimeWorldObjectChangeCheckTicks;
        if (mod?.CanSyncRuntimeWorldObjects != true)
        {
            return;
        }

        string signature = BuildRuntimeWorldObjectSyncSignature();
        bool changed = !string.Equals(signature, lastRuntimeWorldObjectSyncSignature, StringComparison.Ordinal);
        bool hasRuntimeObjects = !string.IsNullOrWhiteSpace(signature);
        if (!changed && (!hasRuntimeObjects || ticks < nextRuntimeWorldObjectSyncTick))
        {
            return;
        }

        mod.StartSyncRuntimeWorldObjects();
        lastRuntimeWorldObjectSyncSignature = signature;
        nextRuntimeWorldObjectSyncTick = ticks + RuntimeWorldObjectLeaseRefreshTicks;
    }

    private void TryRegisterPlayerColonySites(ClashOfRimMod? mod, int ticks)
    {
        if (ticks < nextPlayerColonySiteRegistrationTick)
        {
            return;
        }

        nextPlayerColonySiteRegistrationTick = ticks + 10000;
        if (mod?.CanRegisterPlayerColonySites == true)
        {
            mod.StartRegisterPlayerColonySites();
        }
    }

    private void TryRefreshCaravanArrivalTargets(ClashOfRimMod? mod, int ticks)
    {
        if (ticks < nextCaravanArrivalTargetRefreshCheckTick)
        {
            return;
        }

        nextCaravanArrivalTargetRefreshCheckTick = ticks + 250;
        string signature = BuildPlayerCaravanTileSignature();
        if (string.Equals(signature, lastPlayerCaravanTileSignature, StringComparison.Ordinal))
        {
            return;
        }

        lastPlayerCaravanTileSignature = signature;
        if (!string.IsNullOrWhiteSpace(signature) && mod?.CanRefreshCaravanArrivalTargets == true)
        {
            mod.StartRefreshCaravanArrivalTargets(signature);
        }
    }

    private void TryRefreshWorldMapMarkers(ClashOfRimMod? mod)
    {
        bool worldSelected = Verse.Current.ProgramState == ProgramState.Playing
            && WorldRendererUtility.WorldSelected
            && Find.WorldObjects is not null;
        if (!worldSelected)
        {
            worldMapWasSelected = false;
            return;
        }

        if (!worldMapWasSelected)
        {
            worldMapWasSelected = true;
            nextWorldMapMarkerRefreshAt = Time.realtimeSinceStartup + WorldMapMarkerRefreshIntervalSeconds;
            mod?.RequestWorldMapMarkerRefresh(ClashOfRimText.Key("ClashOfRim.WorldMap.ReasonEnteredWorldMap"));
            return;
        }

        if (Time.realtimeSinceStartup < nextWorldMapMarkerRefreshAt)
        {
            return;
        }

        nextWorldMapMarkerRefreshAt = Time.realtimeSinceStartup + WorldMapMarkerRefreshIntervalSeconds;
        mod?.RequestWorldMapMarkerRefresh(ClashOfRimText.Key("ClashOfRim.WorldMap.ReasonStayedWorldMap"));
    }

    private void DrawRemoteMapSessionOverlay(ClashOfRimMod? mod)
    {
        if (activeRemoteMapSession?.IsRaidBattle != true
            || activeRaidBattleSession is null
            || Verse.Current.ProgramState != ProgramState.Playing
            || WorldRendererUtility.WorldSelected)
        {
            return;
        }

        Rect rect = new(UI.screenWidth - 328f, 12f, 316f, 108f);
        Widgets.DrawWindowBackground(rect);
        Rect inner = rect.ContractedBy(10f);
        Text.Font = GameFont.Small;
        Text.Anchor = TextAnchor.UpperLeft;
        bool expired = activeRaidBattleSession.IsExpired;
        bool finishing = activeRaidBattleSession.FinishInProgress;
        string status = string.Empty;
        if (expired || finishing)
        {
            status = mod?.WorldMapStatus ?? string.Empty;
            if (string.IsNullOrWhiteSpace(status))
            {
                status = "ClashOfRim.Raid.StatusSettlementUploading".Translate().ToString();
            }
        }
        else
        {
            string remaining = FormatRemaining(activeRaidBattleSession.Remaining);
            status = "ClashOfRim.Raid.BattleCountdown".Translate(remaining.Named("TIME")).ToString();
        }

        Widgets.Label(
            new Rect(inner.x, inner.y, inner.width, 24f),
            status);

        string target = string.IsNullOrWhiteSpace(activeRaidBattleSession.DefenderUserId)
            ? activeRaidBattleSession.DefenderColonyId
            : activeRaidBattleSession.DefenderUserId;
        Widgets.Label(
            new Rect(inner.x, inner.y + 26f, inner.width, 24f),
            "ClashOfRim.Raid.BattleTarget".Translate(target.Named("TARGET")));

        bool disabled = expired || finishing || mod is null;
        Rect buttonRect = new(inner.x, inner.y + 58f, inner.width, 30f);
        if (disabled)
        {
            GUI.color = Color.gray;
        }

        if (Widgets.ButtonText(buttonRect, "ClashOfRim.Raid.FinishBattle".Translate()) && !disabled)
        {
            TryStartFinishActiveRaidBattleFromUi(mod, "ManualFinish");
        }

        GUI.color = Color.white;
        Text.Anchor = TextAnchor.UpperLeft;
    }

    private void DrawChatOverlay(ClashOfRimMod? mod)
    {
        if (mod?.IsInActiveMultiplayerSession != true
            || Verse.Current.ProgramState != ProgramState.Playing
            || WorldRendererUtility.WorldSelected)
        {
            return;
        }

        if (Time.realtimeSinceStartup >= nextChatRefreshAt)
        {
            nextChatRefreshAt = Time.realtimeSinceStartup + 10f;
            mod.StartRefreshChatMessages();
        }

        float top = activeRaidBattleSession is null ? 12f : 128f;
        int unread = mod.UnreadPrivateChatCount;
        if (chatOverlayMinimized)
        {
            Rect minimizedRect = new(UI.screenWidth - 100f, top, 88f, 28f);
            string label = unread > 0
                ? ClashOfRimText.Key("ClashOfRim.Chat.MinimizedWithUnread", unread.Named("COUNT"))
                : ClashOfRimText.Key("ClashOfRim.Chat.Minimized");
            if (Widgets.ButtonText(minimizedRect, label))
            {
                chatOverlayMinimized = false;
            }

            return;
        }

        mod.MarkPrivateChatRead();
        Rect rect = new(UI.screenWidth - 388f, top, 376f, 316f);
        Widgets.DrawWindowBackground(rect);
        Rect inner = rect.ContractedBy(10f);
        Text.Font = GameFont.Small;
        Text.Anchor = TextAnchor.UpperLeft;
        Widgets.Label(new Rect(inner.x, inner.y, inner.width - 72f, 24f), ClashOfRimText.Key("ClashOfRim.Chat.Title"));
        if (Widgets.ButtonText(new Rect(inner.xMax - 66f, inner.y, 66f, 24f), ClashOfRimText.Key("ClashOfRim.Chat.Minimize")))
        {
            chatOverlayMinimized = true;
            return;
        }

        IReadOnlyList<ModChatMessageDto> messages = ChatMessages(mod);
        Rect outRect = new(inner.x, inner.y + 30f, inner.width, 214f);
        Rect viewRect = new(0f, 0f, outRect.width - 16f, Math.Max(outRect.height, messages.Count * 44f + 8f));
        if (messages.Count != lastChatOverlayMessageCount)
        {
            lastChatOverlayMessageCount = messages.Count;
            chatScrollPosition.y = viewRect.height;
        }

        Widgets.BeginScrollView(outRect, ref chatScrollPosition, viewRect);
        float y = 0f;
        Text.Font = GameFont.Tiny;
        foreach (ModChatMessageDto message in messages)
        {
            bool incomingPrivate = string.Equals(message.Channel, "Private", StringComparison.Ordinal)
                && string.Equals(message.TargetUserId, mod.CurrentUserId, StringComparison.Ordinal);
            string channel = string.Equals(message.Channel, "Private", StringComparison.Ordinal)
                ? ClashOfRimText.Key("ClashOfRim.Chat.PrivateChannel")
                : ClashOfRimText.Key("ClashOfRim.Chat.PublicChannel");
            string header = string.Equals(message.Channel, "Private", StringComparison.Ordinal)
                ? ClashOfRimText.Key(
                    "ClashOfRim.Chat.PrivateHeader",
                    message.FromUserId.Named("FROM"),
                    (message.TargetUserId ?? string.Empty).Named("TO"))
                : ClashOfRimText.Key("ClashOfRim.Chat.PublicHeader", message.FromUserId.Named("FROM"));
            if (incomingPrivate)
            {
                GUI.color = new Color(1f, 0.86f, 0.58f);
            }

            Widgets.Label(new Rect(0f, y, viewRect.width, 18f), channel + " " + header);
            GUI.color = Color.white;
            Widgets.Label(new Rect(0f, y + 18f, viewRect.width, 34f), message.Text ?? string.Empty);
            y += 44f;
        }

        Text.Font = GameFont.Small;
        Widgets.EndScrollView();

        Rect inputRect = new(inner.x, inner.yMax - 64f, inner.width - 72f, 30f);
        chatInput = Widgets.TextField(inputRect, chatInput ?? string.Empty);
        bool canSend = !mod.ChatInProgress && !string.IsNullOrWhiteSpace(chatInput);
        if (!canSend)
        {
            GUI.color = Color.gray;
        }

        if (Widgets.ButtonText(new Rect(inner.xMax - 64f, inner.yMax - 64f, 64f, 30f), ClashOfRimText.Key("ClashOfRim.Chat.Send")) && canSend)
        {
            mod.StartSendChatMessage(chatInput);
            chatInput = string.Empty;
        }

        GUI.color = Color.white;
        Text.Font = GameFont.Tiny;
        Widgets.Label(new Rect(inner.x, inner.yMax - 30f, inner.width, 28f), mod.ChatStatus);
        Text.Font = GameFont.Small;
        Text.Anchor = TextAnchor.UpperLeft;
    }

    private IReadOnlyList<ModChatMessageDto> ChatMessages(ClashOfRimMod mod)
    {
        int version = mod.ChatMessagesSnapshotVersion;
        if (version != cachedChatMessagesVersion)
        {
            cachedChatMessages = mod.ChatMessagesSnapshot;
            cachedChatMessagesVersion = version;
        }

        return cachedChatMessages;
    }

    private void TryFinishExpiredRaidBattle(ClashOfRimMod? mod)
    {
        if (activeRaidBattleSession is null
            || activeRaidBattleSession.FinishInProgress
            || !activeRaidBattleSession.IsExpired
            || mod is null
            || mod.SnapshotUploadInProgress)
        {
            return;
        }

        RemoteMapSessionController.TryRequestRaidBattleFinish(mod, activeRaidBattleSession, "Expired");
    }

    private void TryHandleRaidBattleFinalDeadline(ClashOfRimMod? mod)
    {
        if (activeRaidBattleSession is null
            || !activeRaidBattleSession.IsFinalDeadlineExpired
            || mod is null)
        {
            return;
        }

        mod.HandleRaidBattleFinalDeadlineExpired(activeRaidBattleSession);
    }

    private void TryCleanupRemoteWorldObjectOrphans(int ticks)
    {
        if (ticks < nextRemoteWorldObjectOrphanCleanupTick)
        {
            return;
        }

        nextRemoteWorldObjectOrphanCleanupTick = ticks + RemoteWorldObjectOrphanCleanupTicks;
        RemoteRuntimeWorldObjectRegistry.CleanupOrphans();
    }

    private static bool MapIdsMatch(string currentMapId, string? targetMapId)
    {
        if (string.IsNullOrWhiteSpace(targetMapId))
        {
            return false;
        }

        string normalizedTarget = targetMapId!.StartsWith("Map_", StringComparison.Ordinal)
            ? targetMapId
            : "Map_" + targetMapId;
        return string.Equals(currentMapId, normalizedTarget, StringComparison.Ordinal);
    }

    private static string FormatRemaining(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
        {
            return "00:00";
        }

        int totalSeconds = Math.Max(0, (int)Math.Ceiling(remaining.TotalSeconds));
        int hours = totalSeconds / 3600;
        int minutes = (totalSeconds % 3600) / 60;
        int seconds = totalSeconds % 60;
        return hours > 0
            ? $"{hours:00}:{minutes:00}:{seconds:00}"
            : $"{minutes:00}:{seconds:00}";
    }

    private static string BuildPlayerCaravanTileSignature()
    {
        List<WorldObject>? worldObjects = Find.WorldObjects?.AllWorldObjects;
        if (worldObjects is null)
        {
            return string.Empty;
        }

        try
        {
            for (int index = 0; index < worldObjects.Count; index++)
            {
                if (worldObjects[index] is Caravan caravan
                    && !caravan.Destroyed
                    && caravan.Spawned
                    && caravan.IsPlayerControlled)
                {
                    RuntimeSignatureWorldObjects.Add(caravan);
                }
            }

            return BuildRuntimeWorldObjectSignatureFromBuffer(includePath: false);
        }
        finally
        {
            RuntimeSignatureWorldObjects.Clear();
            RuntimeSignatureBuilder.Clear();
        }
    }

    private static string BuildRuntimeWorldObjectSyncSignature()
    {
        List<WorldObject>? worldObjects = Find.WorldObjects?.AllWorldObjects;
        if (worldObjects is null)
        {
            return string.Empty;
        }

        try
        {
            for (int index = 0; index < worldObjects.Count; index++)
            {
                WorldObject worldObject = worldObjects[index];
                if (IsRuntimeWorldObjectForSync(worldObject))
                {
                    RuntimeSignatureWorldObjects.Add(worldObject);
                }
            }

            return BuildRuntimeWorldObjectSignatureFromBuffer(includePath: true);
        }
        finally
        {
            RuntimeSignatureWorldObjects.Clear();
            RuntimeSignatureBuilder.Clear();
        }
    }

    private static string BuildRuntimeWorldObjectSignatureFromBuffer(bool includePath)
    {
        if (RuntimeSignatureWorldObjects.Count == 0)
        {
            return string.Empty;
        }

        RuntimeSignatureWorldObjects.Sort(RuntimeSignatureWorldObjectComparer);
        for (int index = 0; index < RuntimeSignatureWorldObjects.Count; index++)
        {
            if (index > 0)
            {
                RuntimeSignatureBuilder.Append('|');
            }

            WorldObject worldObject = RuntimeSignatureWorldObjects[index];
            RuntimeSignatureBuilder.Append(worldObject.GetUniqueLoadID());
            RuntimeSignatureBuilder.Append(':');
            RuntimeSignatureBuilder.Append(worldObject.Tile);
            if (includePath)
            {
                RuntimeSignatureBuilder.Append(':');
                AppendRuntimeWorldObjectPathSignature(RuntimeSignatureBuilder, worldObject);
            }
        }

        return RuntimeSignatureBuilder.ToString();
    }

    private static bool IsRuntimeWorldObjectForSync(WorldObject worldObject)
    {
        if (worldObject is null
            || worldObject.Destroyed
            || !worldObject.Spawned
            || worldObject is RemoteRuntimeWorldObject
            || worldObject is RemoteColonyMapParent
            || worldObject is RemoteSessionMapParent
            || worldObject is Settlement
            || worldObject.def is null
            || worldObject.Faction != Faction.OfPlayer)
        {
            return false;
        }

        string className = worldObject.GetType().Name;
        string defName = worldObject.def.defName ?? string.Empty;
        return worldObject is Caravan
            || ContainsRuntimeWorldObjectToken(className)
            || ContainsRuntimeWorldObjectToken(defName);
    }

    private static void AppendRuntimeWorldObjectPathSignature(StringBuilder builder, WorldObject worldObject)
    {
        if (worldObject is not Caravan caravan
            || caravan.pather is null
            || !caravan.pather.Moving
            || caravan.pather.curPath is null
            || !caravan.pather.curPath.Found
            || caravan.pather.curPath.NodesLeftCount < 2)
        {
            return;
        }

        for (int i = 0; i < caravan.pather.curPath.NodesLeftCount; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(caravan.pather.curPath.Peek(i));
        }
    }

    private static bool ContainsRuntimeWorldObjectToken(string value)
    {
        return ContainsToken(value, "Caravan")
            || ContainsToken(value, "Shuttle")
            || ContainsToken(value, "TransportPod")
            || ContainsToken(value, "Traveling");
    }

    private static bool ContainsToken(string value, string token)
    {
        return value.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static void RunMainThreadActions()
    {
        RunDueDelayedMainThreadActions();
        while (MainThreadActions.TryDequeue(out Action action))
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                Log.Warning("[ClashOfRim] Main thread action failed: " + ex);
            }
        }
    }

    private static void RunDueDelayedMainThreadActions()
    {
        if (DelayedMainThreadActions.IsEmpty)
        {
            return;
        }

        int currentTicks = Find.TickManager?.TicksGame ?? 0;
        if (currentTicks < nextDelayedMainThreadActionTick)
        {
            return;
        }

        var deferred = new List<DelayedMainThreadAction>();
        int nextDueTick = int.MaxValue;
        lock (DelayedMainThreadActionsGate)
        {
            if (currentTicks < nextDelayedMainThreadActionTick)
            {
                return;
            }

            while (DelayedMainThreadActions.TryDequeue(out DelayedMainThreadAction delayed))
            {
                if (delayed.DueTick <= currentTicks)
                {
                    MainThreadActions.Enqueue(delayed.Action);
                }
                else
                {
                    deferred.Add(delayed);
                    if (delayed.DueTick < nextDueTick)
                    {
                        nextDueTick = delayed.DueTick;
                    }
                }
            }

            foreach (DelayedMainThreadAction delayed in deferred)
            {
                DelayedMainThreadActions.Enqueue(delayed);
            }

            nextDelayedMainThreadActionTick = nextDueTick;
        }
    }

    private sealed class DelayedMainThreadAction
    {
        public DelayedMainThreadAction(int dueTick, Action action)
        {
            DueTick = dueTick;
            Action = action;
        }

        public int DueTick { get; }

        public Action Action { get; }
    }

    private sealed class WorldObjectLoadIdComparer : IComparer<WorldObject>
    {
        public int Compare(WorldObject? x, WorldObject? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            return string.Compare(x.GetUniqueLoadID(), y.GetUniqueLoadID(), StringComparison.Ordinal);
        }
    }

}
