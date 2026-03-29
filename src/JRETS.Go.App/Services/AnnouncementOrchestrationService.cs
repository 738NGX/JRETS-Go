using System;
using System.Linq;
using JRETS.Go.Core.Configuration;
using JRETS.Go.Core.Runtime;

namespace JRETS.Go.App.Services;

/// <summary>
/// Orchestrates the complete announcement lifecycle: state management, workflow triggers, and UI actions.
/// Consolidates five related announcement service components into a single cohesive unit.
/// </summary>
public sealed class AnnouncementOrchestrationService
{
    private readonly StateManager _stateManager = new();
    private readonly WorkflowProcessor _workflowProcessor = new();
    private readonly UiActionResolver _uiActionResolver = new();

    public AnnouncementOrchestrationResult Process(
        AnnouncementPlaybackState playbackState,
        RealtimeSnapshot snapshot,
        AnnouncementOrchestrationContext context)
    {
        _stateManager.EnsureInitialized(playbackState, snapshot);
        var (doorCloseTransition, doorOpenTransition) = _stateManager.GetDoorTransitions(
            playbackState,
            snapshot.DoorOpen);

        if (doorCloseTransition)
        {
            _stateManager.SetDepartureTarget(playbackState, context.ResolveAnnouncementTargetStationId());
        }

        if (doorOpenTransition)
        {
            _stateManager.ClearDepartureTarget(playbackState);
        }

        var melodyPanelAction = _uiActionResolver.ResolveMelodyPanelAction(
            snapshot.DoorOpen,
            doorCloseTransition,
            doorOpenTransition,
            context.CurrentStopStation,
            context.IsMelodySelectionPanelVisible,
            context.MelodyCurrentStationId);

        _workflowProcessor.MaintainDepartureTarget(
            playbackState,
            snapshot,
            context.ResolveAnnouncementTargetStationId,
            context.IsTargetStationValidForService);

        _workflowProcessor.ProcessPlaybackTriggers(
            playbackState,
            snapshot,
            context.NextAnnouncementDepartureDistanceMeters,
            context.DefaultApproachAnnouncementDistanceMeters,
            context.TryPlayStationAnnouncement,
            context.HasStationAnnouncement,
            context.ResolveAnnouncementTriggerDistanceMeters);

        _stateManager.SyncPreviousSnapshot(playbackState, snapshot);

        return new AnnouncementOrchestrationResult
        {
            MelodyPanelAction = melodyPanelAction
        };
    }

    /// <summary>
    /// Resets announcement playback state (initialization point for session start).
    /// </summary>
    public void ResetPlaybackState(AnnouncementPlaybackState state, bool doorOpen)
    {
        _stateManager.Reset(state, doorOpen);
    }

    /// <summary>
    /// Marks announcement state as unavailable (used when session is not running).
    /// </summary>
    public void MarkPlaybackStateUnavailable(AnnouncementPlaybackState state)
    {
        _stateManager.MarkUnavailable(state);
    }

    /// <summary>
    /// Creates an orchestration context from MainWindow state and configuration.
    /// Static builder encapsulating the 11-parameter initialization logic.
    /// </summary>
    public static AnnouncementOrchestrationContext BuildContext(
        TrainDisplayState state,
        LineConfiguration lineConfiguration,
        bool isMelodySelectionPanelVisible,
        int? melodyCurrentStationId,
        Func<TrainDisplayState, int?> resolveAnnouncementTargetStationId,
        Func<StationInfo, bool> isStopForSelectedService,
        double nextAnnouncementDepartureDistanceMeters,
        double defaultApproachAnnouncementDistanceMeters,
        Func<int, int, bool> tryPlayStationAnnouncement,
        Func<int, int, bool> hasStationAnnouncement,
        Func<int, int, double, double> resolveAnnouncementTriggerDistanceMeters)
    {
        return new AnnouncementOrchestrationContext
        {
            CurrentStopStation = state.CurrentStopStation,
            IsMelodySelectionPanelVisible = isMelodySelectionPanelVisible,
            MelodyCurrentStationId = melodyCurrentStationId,
            ResolveAnnouncementTargetStationId = () => resolveAnnouncementTargetStationId(state),
            IsTargetStationValidForService = stationId =>
            {
                var targetStation = lineConfiguration.Stations.FirstOrDefault(x => x.Id == stationId);
                return targetStation is not null && isStopForSelectedService(targetStation);
            },
            NextAnnouncementDepartureDistanceMeters = nextAnnouncementDepartureDistanceMeters,
            DefaultApproachAnnouncementDistanceMeters = defaultApproachAnnouncementDistanceMeters,
            TryPlayStationAnnouncement = tryPlayStationAnnouncement,
            HasStationAnnouncement = hasStationAnnouncement,
            ResolveAnnouncementTriggerDistanceMeters = resolveAnnouncementTriggerDistanceMeters
        };
    }

    #region Inner Classes

    /// <summary>
    /// Manages playback state: initialization, door transitions, target station tracking.
    /// Replaces the former AnnouncementStateService.
    /// </summary>
    private sealed class StateManager
    {
        public void Reset(AnnouncementPlaybackState state, bool doorOpen)
        {
            state.DoorStateInitialized = true;
            state.PreviousDoorOpen = doorOpen;
            state.TargetStationId = null;
            state.DepartureStartDistanceMeters = null;
            state.PreviousDistanceMeters = 0;
            state.LastNextAnnouncementStationId = null;
            state.LastApproachAnnouncementStationId = null;
        }

        public void MarkUnavailable(AnnouncementPlaybackState state)
        {
            state.DoorStateInitialized = false;
        }

        public void EnsureInitialized(AnnouncementPlaybackState state, RealtimeSnapshot snapshot)
        {
            if (state.DoorStateInitialized)
            {
                return;
            }

            state.PreviousDoorOpen = snapshot.DoorOpen;
            state.PreviousDistanceMeters = snapshot.CurrentDistanceMeters;
            state.DoorStateInitialized = true;
        }

        public (bool DoorCloseTransition, bool DoorOpenTransition) GetDoorTransitions(
            AnnouncementPlaybackState state,
            bool currentDoorOpen)
        {
            var doorCloseTransition = state.PreviousDoorOpen && !currentDoorOpen;
            var doorOpenTransition = !state.PreviousDoorOpen && currentDoorOpen;
            return (doorCloseTransition, doorOpenTransition);
        }

        public void SetDepartureTarget(AnnouncementPlaybackState state, int? targetStationId)
        {
            state.TargetStationId = targetStationId;
            state.DepartureStartDistanceMeters = state.PreviousDistanceMeters;
        }

        public void ClearDepartureTarget(AnnouncementPlaybackState state)
        {
            state.TargetStationId = null;
            state.DepartureStartDistanceMeters = null;
        }

        public void SyncPreviousSnapshot(AnnouncementPlaybackState state, RealtimeSnapshot snapshot)
        {
            state.PreviousDoorOpen = snapshot.DoorOpen;
            state.PreviousDistanceMeters = snapshot.CurrentDistanceMeters;
        }
    }

    /// <summary>
    /// Processes playback triggers: next-announcement and approach-announcement detection.
    /// Replaces the former AnnouncementWorkflowService.
    /// </summary>
    private sealed class WorkflowProcessor
    {
        public void MaintainDepartureTarget(
            AnnouncementPlaybackState state,
            RealtimeSnapshot snapshot,
            Func<int?> resolveTargetStationId,
            Func<int, bool> isTargetStationValidForService)
        {
            if (snapshot.DoorOpen)
            {
                return;
            }

            if (state.TargetStationId is int staleTargetStationId)
            {
                if (!isTargetStationValidForService(staleTargetStationId))
                {
                    state.TargetStationId = resolveTargetStationId();
                    state.DepartureStartDistanceMeters = state.PreviousDistanceMeters;
                    return;
                }
            }

            if (state.TargetStationId is null)
            {
                state.TargetStationId = resolveTargetStationId();
                state.DepartureStartDistanceMeters = state.PreviousDistanceMeters;
            }
        }

        public void ProcessPlaybackTriggers(
            AnnouncementPlaybackState state,
            RealtimeSnapshot snapshot,
            double nextAnnouncementDepartureDistanceMeters,
            double defaultApproachAnnouncementDistanceMeters,
            Func<int, int, bool> tryPlayStationAnnouncement,
            Func<int, int, bool> hasStationAnnouncement,
            Func<int, int, double, double> resolveAnnouncementTriggerDistanceMeters)
        {
            if (snapshot.DoorOpen || state.TargetStationId is not int stationId)
            {
                return;
            }

            var departureStartDistance = state.DepartureStartDistanceMeters ?? snapshot.CurrentDistanceMeters;
            var traveledDistance = Math.Abs(snapshot.CurrentDistanceMeters - departureStartDistance);
            if (traveledDistance >= nextAnnouncementDepartureDistanceMeters && state.LastNextAnnouncementStationId != stationId)
            {
                if (tryPlayStationAnnouncement(stationId, 0))
                {
                    state.LastNextAnnouncementStationId = stationId;
                }
            }

            var remainingDistance = snapshot.TargetStopDistanceMeters - snapshot.CurrentDistanceMeters;
            var approachTriggerDistance = resolveAnnouncementTriggerDistanceMeters(
                stationId,
                1,
                defaultApproachAnnouncementDistanceMeters);
            if (remainingDistance < approachTriggerDistance && state.LastApproachAnnouncementStationId != stationId)
            {
                var played = tryPlayStationAnnouncement(stationId, 1);
                if (played || !hasStationAnnouncement(stationId, 1))
                {
                    state.LastApproachAnnouncementStationId = stationId;
                }
            }
        }
    }

    /// <summary>
    /// Resolves UI actions (melody panel open/close) based on door state and station changes.
    /// Replaces the former AnnouncementUiWorkflowService.
    /// </summary>
    private sealed class UiActionResolver
    {
        public MelodyPanelAction ResolveMelodyPanelAction(
            bool doorOpen,
            bool doorCloseTransition,
            bool doorOpenTransition,
            StationInfo? currentStopStation,
            bool isMelodySelectionPanelVisible,
            int? melodyCurrentStationId)
        {
            if (doorCloseTransition)
            {
                return MelodyPanelAction.Close;
            }

            if (doorOpenTransition && currentStopStation is not null)
            {
                return MelodyPanelAction.Open;
            }

            // In debug mode, session often starts with doors already open; ensure panel is shown in that state too.
            if (doorOpen && currentStopStation is not null)
            {
                var stationChanged = melodyCurrentStationId != currentStopStation.Id;
                if (!isMelodySelectionPanelVisible || stationChanged)
                {
                    return MelodyPanelAction.Open;
                }
            }
            else if (!doorOpen && isMelodySelectionPanelVisible)
            {
                return MelodyPanelAction.Close;
            }

            return MelodyPanelAction.None;
        }
    }

    #endregion
}

/// <summary>
/// Result of announcement orchestration processing.
/// Contains the UI action to be executed (none, open melody panel, or close melody panel).
/// </summary>
public sealed class AnnouncementOrchestrationResult
{
    public MelodyPanelAction MelodyPanelAction { get; init; } = MelodyPanelAction.None;
}

/// <summary>
/// Grouped context for announcement orchestration.
/// Encapsulates 11 parameters and their associated resolution callbacks.
/// Built by AnnouncementOrchestrationService.BuildContext().
/// </summary>
public sealed class AnnouncementOrchestrationContext
{
    public StationInfo? CurrentStopStation { get; init; }

    public bool IsMelodySelectionPanelVisible { get; init; }

    public int? MelodyCurrentStationId { get; init; }

    public Func<int?> ResolveAnnouncementTargetStationId { get; init; } = default!;

    public Func<int, bool> IsTargetStationValidForService { get; init; } = default!;

    public double NextAnnouncementDepartureDistanceMeters { get; init; }

    public double DefaultApproachAnnouncementDistanceMeters { get; init; }

    public Func<int, int, bool> TryPlayStationAnnouncement { get; init; } = default!;

    public Func<int, int, bool> HasStationAnnouncement { get; init; } = default!;

    public Func<int, int, double, double> ResolveAnnouncementTriggerDistanceMeters { get; init; } = default!;
}

/// <summary>
/// Playback state for announcement system.
/// Tracks door transitions, target stations, and playback history.
/// </summary>
public sealed class AnnouncementPlaybackState
{
    public bool DoorStateInitialized { get; set; }

    public bool PreviousDoorOpen { get; set; }

    public int? TargetStationId { get; set; }

    public int? LastNextAnnouncementStationId { get; set; }

    public int? LastApproachAnnouncementStationId { get; set; }

    public double? DepartureStartDistanceMeters { get; set; }

    public double PreviousDistanceMeters { get; set; }
}

/// <summary>
/// UI action for melody panel state.
/// </summary>
public enum MelodyPanelAction
{
    None,
    Open,
    Close
}
