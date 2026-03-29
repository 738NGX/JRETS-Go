using System;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using JRETS.Go.App.Services;
using JRETS.Go.Core.Configuration;
using JRETS.Go.Core.Runtime;

namespace JRETS.Go.App;

public partial class MainWindow
{
    private bool IsStopForSelectedService(StationInfo station)
    {
        return _trainRouteService.IsStopForSelectedService(_lineConfiguration, _selectedService?.Train, station);
    }

    private bool IsWithinSelectedServiceRoute(StationInfo station)
    {
        return _trainRouteService.IsWithinSelectedServiceRoute(_lineConfiguration, _selectedService?.Train, station);
    }

    private void ResetAnnouncementState(bool doorOpen)
    {
        _announcementOrchestrationService.ResetPlaybackState(_announcementState, doorOpen);
        _announcementPlayer.Stop();
    }

    private void HandleAutoAnnouncements(RealtimeSnapshot snapshot, TrainDisplayState state)
    {
        if (!_sessionRunning || _selectedService is null)
        {
            _announcementOrchestrationService.MarkPlaybackStateUnavailable(_announcementState);
            return;
        }

        var orchestrationContext = AnnouncementOrchestrationService.BuildContext(
            state,
            _lineConfiguration,
            _isMelodySelectionPanelVisible,
            _melodyCurrentStationId,
            ResolveAnnouncementTargetStationId,
            IsStopForSelectedService,
            NextAnnouncementDepartureDistanceMeters,
            ApproachAnnouncementRemainingDistanceMeters,
            TryPlayStationAnnouncement,
            HasStationAnnouncement,
            ResolveAnnouncementTriggerDistanceMeters);

        var orchestrationResult = _announcementOrchestrationService.Process(
            _announcementState,
            snapshot,
            orchestrationContext);

        ApplyMelodyPanelAction(orchestrationResult.MelodyPanelAction, state.CurrentStopStation);
    }

    private void ApplyMelodyPanelAction(MelodyPanelAction action, StationInfo? currentStopStation)
    {
        if (action == MelodyPanelAction.Open)
        {
            OpenMelodySelectionPanel(currentStopStation);
        }
        else if (action == MelodyPanelAction.Close)
        {
            CloseMelodySelectionPanel();
        }
    }

    private int? ResolveAnnouncementTargetStationId(TrainDisplayState state)
    {
        return _announcementRoutingService.ResolveAnnouncementTargetStationId(
            _lineConfiguration,
            state,
            IsWithinSelectedServiceRoute,
            IsStopForSelectedService);
    }

    private int NormalizeStationIdForScoring(int stationId)
    {
        return _announcementRoutingService.NormalizeStationIdForScoring(_lineConfiguration, stationId);
    }

    private int? ResolveNextStoppingStationFromCurrentId(int? currentStationId)
    {
        return _announcementRoutingService.ResolveNextStoppingStationFromCurrentId(
            _lineConfiguration,
            currentStationId,
            IsStopForSelectedService);
    }

    private bool HasStationAnnouncement(int stationId, int paIndex)
    {
        if (_selectedService is null)
        {
            return false;
        }

        return _announcementAudioService.HasStationAnnouncement(
            _lineConfiguration,
            _selectedService.Train.Id,
            stationId,
            paIndex);
    }

    private bool TryPlayStationAnnouncement(int stationId, int paIndex)
    {
        if (_selectedService is null)
        {
            return false;
        }

        var played = _announcementAudioService.TryPlayStationAnnouncement(
            _lineConfiguration,
            _lineConfigPath,
            _selectedService.Train.Id,
            stationId,
            paIndex,
            _announcementPlayer,
            _announcementTempDirectory,
            _announcementNormalizedPathCache,
            out var error);
        if (!played && !string.IsNullOrWhiteSpace(error))
        {
            _lastDataSourceError = error;
        }

        return played;
    }

    private double ResolveAnnouncementTriggerDistanceMeters(int stationId, int paIndex, double defaultDistanceMeters)
    {
        if (_selectedService is null)
        {
            return defaultDistanceMeters;
        }

        return _announcementAudioService.ResolveAnnouncementTriggerDistanceMeters(
            _lineConfiguration,
            _selectedService.Train.Id,
            stationId,
            paIndex,
            defaultDistanceMeters);
    }

    private Brush GetFutureBrush()
    {
        if (LineColorPreview.Background is SolidColorBrush solid)
        {
            return solid;
        }

        return new SolidColorBrush(Color.FromRgb(0, 178, 229));
    }

    private static string BuildArrowGeometry(bool isFirstToken, bool isStationToken)
    {
        var width = isStationToken ? StationArrowWidth : SegmentArrowWidth;
        var bodyEndX = width - ArrowTipWidth;
        var midY = ArrowHeight / 2;
        var notch = Math.Min(ArrowNotchDepth, width / 2 - 2);

        return isFirstToken
            ? $"M0,0 L{bodyEndX:0.##},0 L{width:0.##},{midY:0.##} L{bodyEndX:0.##},{ArrowHeight:0.##} L0,{ArrowHeight:0.##} Z"
            : $"M0,0 L{bodyEndX:0.##},0 L{width:0.##},{midY:0.##} L{bodyEndX:0.##},{ArrowHeight:0.##} L0,{ArrowHeight:0.##} L{notch:0.##},{midY:0.##} Z";
    }

}
