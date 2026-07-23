using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using SteamVRTranslator.App.Configuration;
using SteamVRTranslator.App.Localization;
using SteamVRTranslator.Core.Selection;
using Valve.VR;

namespace SteamVRTranslator.App.SteamVR;

internal partial class VrControlPanelWindow : Window,
    IWpfOverlayHoldingHandAware,
    IWpfOverlayInteractionHighlightAware,
    IWpfOverlayInvalidationSource,
    IWpfOverlayPointerHoverAware
{
    private bool _applyingState;
    private string _displayMode = VoiceTranslationDisplayModes.TranslationOnly;
    private string _targetLanguage = "en-US";
    private string _captureEye = "left-eye";
    private int _pointerSmoothingStrength = AppConfiguration.DefaultPointerSmoothingStrength;
    private bool _subtitlesAvailable = true;
    private bool _androidMirrorAvailable = true;
    private ETrackedControllerRole? _holdingHand;
    private VrAndroidMirrorControlState _mirrorState = VrAndroidMirrorControlState.Empty;
    private VrSubtitleControlState _subtitleState = VrSubtitleControlState.Empty;
    private ButtonBase? _hoveredButton;
    private NormalizedPoint? _lastPointerPoint;

    public VrControlPanelWindow(VrControlPanelState state)
    {
        InitializeComponent();
        ApplyLocalization();
        ApplyState(state);
    }

    public event EventHandler? CloseRequested;

    public event EventHandler? CaptureRequested;

    public event EventHandler<VrSubtitleControlRequestEventArgs>? SubtitleRequested;

    public event EventHandler? SubtitlesRequested;

    public event EventHandler<VrAndroidMirrorControlRequestEventArgs>? AndroidMirrorRequested;

    public event EventHandler<VrControlPanelStateChangedEventArgs>? SettingsChanged;

    public event EventHandler? OverlayInvalidated;

    public void ApplyState(VrControlPanelState state)
    {
        _applyingState = true;
        try
        {
            VoiceEnabledToggle.IsChecked = state.VoiceEnabled;
            TranslationEnabledToggle.IsChecked = state.TranslationEnabled;
            SendImmediatelyToggle.IsChecked = state.SendImmediately;
            PointerRayButton.IsChecked = state.PointerRayEnabled;
            _displayMode = VoiceTranslationDisplayModes.Normalize(state.DisplayMode);
            _targetLanguage = NormalizeTargetLanguage(state.TargetLanguage);
            _captureEye = NormalizeCaptureEye(state.CaptureEye);
            _pointerSmoothingStrength = PointerSmoothingPreset(state.PointerSmoothingStrength);
            _subtitlesAvailable = state.SubtitlesAvailable;
            _androidMirrorAvailable = state.AndroidMirrorAvailable;
            _mirrorState = state.AndroidMirror ?? VrAndroidMirrorControlState.Empty;
            _subtitleState = state.Subtitles ?? VrSubtitleControlState.Empty;
            ChunkIntervalSlider.Value = Math.Clamp(
                state.ChunkIntervalMilliseconds,
                VrChatVoiceInputConfiguration.MinimumStreamingChunkIntervalMilliseconds,
                VrChatVoiceInputConfiguration.MaximumStreamingChunkIntervalMilliseconds);
            UpdateVisualState();
            UpdateFeatureVisibility();
        }
        finally
        {
            _applyingState = false;
        }
        InvalidateOverlay();
    }

    public void ApplyAndroidMirrorState(VrAndroidMirrorControlState state)
    {
        _mirrorState = state with
        {
            MaximumSize = AndroidMirrorConfiguration.NormalizeMaximumSize(state.MaximumSize),
            MaximumFramesPerSecond = AndroidMirrorConfiguration.NormalizeMaximumFramesPerSecond(
                state.MaximumFramesPerSecond),
            VideoBitRateMbps = NormalizeMirrorBitRate(state.VideoBitRateMbps),
            WindowScale = AndroidMirrorConfiguration.NormalizeWindowScale(state.WindowScale)
        };
        UpdateMirrorVisualState();
        InvalidateOverlay();
    }

    public void ApplySubtitleState(VrSubtitleControlState state)
    {
        _subtitleState = state with
        {
            AsrBackend = SubtitleAsrBackends.Normalize(state.AsrBackend),
            TargetLanguage = NormalizeTargetLanguage(state.TargetLanguage),
            DiarizationCpuThreadCount = NormalizeSubtitleThreads(
                state.DiarizationCpuThreadCount)
        };
        UpdateSubtitleVisualState();
        InvalidateOverlay();
    }

    public void SetHoldingHand(ETrackedControllerRole? hand)
    {
        if (_holdingHand == hand)
        {
            return;
        }

        _holdingHand = hand;
        var heldByRight = hand == ETrackedControllerRole.RightHand;
        UtilityButtons.HorizontalAlignment = heldByRight
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Right;
        UtilityButtons.Children.Clear();
        if (heldByRight)
        {
            UtilityButtons.Children.Add(CloseButton);
            UtilityButtons.Children.Add(HomeButton);
        }
        else
        {
            UtilityButtons.Children.Add(HomeButton);
            UtilityButtons.Children.Add(CloseButton);
        }
        InvalidateOverlay();
    }

    public void SetInteractionHighlighted(bool highlighted)
    {
        WindowFrame.BorderBrush = highlighted
            ? new SolidColorBrush(Color.FromRgb(46, 229, 140))
            : Brushes.Transparent;
        InvalidateOverlay();
    }

    public bool SetPointerHover(NormalizedPoint? point)
    {
        _lastPointerPoint = point;
        return ApplyPointerHover(point);
    }

    private bool ApplyPointerHover(NormalizedPoint? point)
    {
        ButtonBase? hovered = null;
        if (point is { } normalized &&
            OverlayCanvas.ActualWidth > 1 &&
            OverlayCanvas.ActualHeight > 1)
        {
            var hit = OverlayCanvas.InputHitTest(new Point(
                normalized.X * OverlayCanvas.ActualWidth,
                normalized.Y * OverlayCanvas.ActualHeight)) as DependencyObject;
            hovered = FindAncestor<ButtonBase>(hit);
        }

        if (ReferenceEquals(_hoveredButton, hovered))
        {
            if (hovered is not null && !VrPointerHover.GetIsHovered(hovered))
            {
                VrPointerHover.SetExclusiveHoveredButton(OverlayCanvas, hovered);
                InvalidateOverlay();
                return true;
            }
            return false;
        }

        _hoveredButton = hovered;
        VrPointerHover.SetExclusiveHoveredButton(OverlayCanvas, _hoveredButton);
        InvalidateOverlay();
        return true;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }
            current = current is Visual
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }

    private void VoiceMenuButton_Click(object sender, RoutedEventArgs e)
    {
        ShowPage(VoicePage);
    }

    private void SettingsMenuButton_Click(object sender, RoutedEventArgs e) =>
        ShowPage(SettingsPage);

    private void MirrorMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (_androidMirrorAvailable)
        {
            ShowPage(MirrorPage);
        }
    }

    private void HomeButton_Click(object sender, RoutedEventArgs e) => ShowPage(MainPage);

    private void ShowPage(FrameworkElement page)
    {
        ApplyPointerHover(null);
        MainPage.Visibility = ReferenceEquals(page, MainPage) ? Visibility.Visible : Visibility.Collapsed;
        VoicePage.Visibility = ReferenceEquals(page, VoicePage) ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = ReferenceEquals(page, SettingsPage) ? Visibility.Visible : Visibility.Collapsed;
        SubtitlePage.Visibility = ReferenceEquals(page, SubtitlePage) ? Visibility.Visible : Visibility.Collapsed;
        MirrorPage.Visibility = ReferenceEquals(page, MirrorPage) ? Visibility.Visible : Visibility.Collapsed;
        HomeButton.IsEnabled = !ReferenceEquals(page, MainPage);
        InvalidateOverlay();
        _ = Dispatcher.BeginInvoke(
            () => ApplyPointerHover(_lastPointerPoint),
            DispatcherPriority.Loaded);
    }

    private void CaptureMenuButton_Click(object sender, RoutedEventArgs e) =>
        CaptureRequested?.Invoke(this, EventArgs.Empty);

    private void SubtitleMenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (_subtitlesAvailable)
        {
            ShowPage(SubtitlePage);
            SubtitlesRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SubtitleBackendButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string backend })
        {
            _subtitleState = _subtitleState with
            {
                AsrBackend = SubtitleAsrBackends.Normalize(backend)
            };
            UpdateSubtitleVisualState();
            RaiseSubtitleRequest(VrSubtitleControlRequestKind.ConfigurationChanged);
        }
    }

    private void SubtitleLanguageButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string language })
        {
            _subtitleState = _subtitleState with
            {
                TargetLanguage = NormalizeTargetLanguage(language)
            };
            UpdateSubtitleVisualState();
            RaiseSubtitleRequest(VrSubtitleControlRequestKind.ConfigurationChanged);
        }
    }

    private void SubtitleThreadButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string value } && int.TryParse(value, out var threads))
        {
            _subtitleState = _subtitleState with
            {
                DiarizationCpuThreadCount = NormalizeSubtitleThreads(threads)
            };
            UpdateSubtitleVisualState();
            RaiseSubtitleRequest(VrSubtitleControlRequestKind.ConfigurationChanged);
        }
    }

    private void SubtitleControl_Changed(object sender, RoutedEventArgs e)
    {
        if (_applyingState)
        {
            return;
        }
        _subtitleState = _subtitleState with
        {
            ShowOriginalText = SubtitleShowOriginalButton.IsChecked == true,
            TranslateText = SubtitleTranslateButton.IsChecked == true,
            UseSpeakerColors = SubtitleSpeakerColorsButton.IsChecked == true,
            DiarizationEnabled = SubtitleDiarizationButton.IsChecked == true
        };
        UpdateSubtitleVisualState();
        RaiseSubtitleRequest(VrSubtitleControlRequestKind.ConfigurationChanged);
    }

    private void SubtitleOpenWindowButton_Click(object sender, RoutedEventArgs e) =>
        RaiseSubtitleRequest(VrSubtitleControlRequestKind.OpenWindow);

    private void RaiseSubtitleRequest(VrSubtitleControlRequestKind kind)
    {
        if (_applyingState)
        {
            return;
        }
        SubtitleRequested?.Invoke(
            this,
            new VrSubtitleControlRequestEventArgs(kind, _subtitleState));
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);

    private void MirrorPreviousDeviceButton_Click(object sender, RoutedEventArgs e) =>
        SelectMirrorDevice(-1);

    private void MirrorNextDeviceButton_Click(object sender, RoutedEventArgs e) =>
        SelectMirrorDevice(1);

    private void MirrorRefreshButton_Click(object sender, RoutedEventArgs e) =>
        RaiseAndroidMirrorRequest(VrAndroidMirrorControlRequestKind.RefreshDevices);

    private void MirrorStartStopButton_Click(object sender, RoutedEventArgs e) =>
        RaiseAndroidMirrorRequest(VrAndroidMirrorControlRequestKind.ToggleConnection);

    private void MirrorDownloadButton_Click(object sender, RoutedEventArgs e) =>
        RaiseAndroidMirrorRequest(VrAndroidMirrorControlRequestKind.DownloadRuntime);

    private void MirrorSizeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string value } && int.TryParse(value, out var size))
        {
            _mirrorState = _mirrorState with
            {
                MaximumSize = AndroidMirrorConfiguration.NormalizeMaximumSize(size)
            };
            UpdateMirrorVisualState();
            RaiseAndroidMirrorRequest(VrAndroidMirrorControlRequestKind.ConfigurationChanged);
        }
    }

    private void MirrorFpsButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string value } && int.TryParse(value, out var fps))
        {
            _mirrorState = _mirrorState with
            {
                MaximumFramesPerSecond =
                    AndroidMirrorConfiguration.NormalizeMaximumFramesPerSecond(fps)
            };
            UpdateMirrorVisualState();
            RaiseAndroidMirrorRequest(VrAndroidMirrorControlRequestKind.ConfigurationChanged);
        }
    }

    private void MirrorBitRateButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string value } && int.TryParse(value, out var bitRate))
        {
            _mirrorState = _mirrorState with { VideoBitRateMbps = NormalizeMirrorBitRate(bitRate) };
            UpdateMirrorVisualState();
            RaiseAndroidMirrorRequest(VrAndroidMirrorControlRequestKind.ConfigurationChanged);
        }
    }

    private void MirrorScaleButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string value } &&
            double.TryParse(
                value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var scale))
        {
            _mirrorState = _mirrorState with
            {
                WindowScale = AndroidMirrorConfiguration.NormalizeWindowScale(scale)
            };
            UpdateMirrorVisualState();
            RaiseAndroidMirrorRequest(VrAndroidMirrorControlRequestKind.ConfigurationChanged);
        }
    }

    private void Control_Changed(object sender, RoutedEventArgs e)
    {
        UpdateVisualState();
        RaiseSettingsChanged();
    }

    private void DisplayModeButton_Click(object sender, RoutedEventArgs e)
    {
        _displayMode = ReferenceEquals(sender, OriginalThenTranslationButton)
            ? VoiceTranslationDisplayModes.OriginalThenTranslation
            : VoiceTranslationDisplayModes.TranslationOnly;
        UpdateVisualState();
        RaiseSettingsChanged();
    }

    private void TargetLanguageButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string language })
        {
            _targetLanguage = NormalizeTargetLanguage(language);
            UpdateVisualState();
            RaiseSettingsChanged();
        }
    }

    private void CaptureEyeButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string captureEye })
        {
            _captureEye = NormalizeCaptureEye(captureEye);
            UpdateVisualState();
            RaiseSettingsChanged();
        }
    }

    private void PointerSmoothingButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string value } && int.TryParse(value, out var strength))
        {
            _pointerSmoothingStrength = AppConfiguration.NormalizePointerSmoothingStrength(strength);
            UpdateVisualState();
            RaiseSettingsChanged();
        }
    }

    private void RaiseSettingsChanged()
    {
        if (_applyingState || !IsInitialized)
        {
            return;
        }

        SettingsChanged?.Invoke(this, new VrControlPanelStateChangedEventArgs(ReadState()));
    }

    private VrControlPanelState ReadState() => new(
        VoiceEnabledToggle.IsChecked == true,
        TranslationEnabledToggle.IsChecked == true,
        SendImmediatelyToggle.IsChecked == true,
        _displayMode,
        _targetLanguage,
        (int)Math.Round(ChunkIntervalSlider.Value),
        _captureEye,
        _mirrorState,
        _subtitlesAvailable,
        _androidMirrorAvailable,
        PointerRayButton.IsChecked == true,
        _pointerSmoothingStrength,
        _subtitleState);

    private void UpdateFeatureVisibility()
    {
        SubtitleMenuButton.Visibility = _subtitlesAvailable
            ? Visibility.Visible
            : Visibility.Collapsed;
        MirrorMenuButton.Visibility = _androidMirrorAvailable
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (!_androidMirrorAvailable && MirrorPage.Visibility == Visibility.Visible)
        {
            ShowPage(MainPage);
        }
        if (!_subtitlesAvailable && SubtitlePage.Visibility == Visibility.Visible)
        {
            ShowPage(MainPage);
        }
    }

    private void ApplyLocalization()
    {
        CaptureMenuButton.ToolTip = T("VrPanel.Capture");
        VoiceMenuButton.ToolTip = T("VrPanel.Voice");
        SubtitleMenuButton.ToolTip = T("VrPanel.Subtitles");
        SettingsMenuButton.ToolTip = T("VrPanel.Settings");
        MirrorMenuButton.ToolTip = T("VrPanel.Mirror");
        VoiceEnabledToggle.ToolTip = T("Voice.Enable");
        TranslationEnabledToggle.ToolTip = T("Voice.Translation.Enable");
        SendImmediatelyToggle.ToolTip = T("Voice.SendImmediately");
        TranslationOnlyButton.Content = T("VrPanel.TranslationShort");
        TranslationOnlyButton.ToolTip = T("Voice.Translation.Only");
        OriginalThenTranslationButton.Content = T("VrPanel.OriginalTranslationShort");
        OriginalThenTranslationButton.ToolTip = T("Voice.Translation.OriginalThenTranslation");
        ChineseButton.Content = "中";
        ChineseButton.ToolTip = T("Language.Chinese");
        JapaneseButton.Content = "日";
        JapaneseButton.ToolTip = T("Language.Japanese");
        EnglishButton.Content = "EN";
        EnglishButton.ToolTip = T("Language.English");
        ChunkIntervalSlider.ToolTip = T("Voice.ChunkInterval.Tooltip");
        PointerRayButton.ToolTip = T("Capture.PointerRay.Tooltip");
        PointerSmoothing0Button.ToolTip = T("Capture.PointerSmoothing.Tooltip");
        PointerSmoothing25Button.ToolTip = T("Capture.PointerSmoothing.Tooltip");
        PointerSmoothing50Button.ToolTip = T("Capture.PointerSmoothing.Tooltip");
        PointerSmoothing75Button.ToolTip = T("Capture.PointerSmoothing.Tooltip");
        PointerSmoothing100Button.ToolTip = T("Capture.PointerSmoothing.Tooltip");
        LeftEyeButton.ToolTip = T("Capture.Eye.Left");
        RightEyeButton.ToolTip = T("Capture.Eye.Right");
        HomeButton.ToolTip = T("VrPanel.Home");
        CloseButton.ToolTip = T("Overlay.Toolbar.Close");
        MirrorPreviousDeviceButton.ToolTip = T("VrPanel.Mirror.PreviousDevice");
        MirrorNextDeviceButton.ToolTip = T("VrPanel.Mirror.NextDevice");
        MirrorRefreshButton.ToolTip = T("AndroidMirror.Refresh.Tooltip");
        MirrorStartStopButton.ToolTip = T("VrPanel.Mirror.Connect");
        MirrorDownloadButton.ToolTip = T("AndroidMirror.Download");
        MirrorDownloadText.Text = T("AndroidMirror.Download");
        SubtitleShowOriginalButton.ToolTip = T("Subtitle.ShowOriginal");
        SubtitleTranslateButton.ToolTip = T("Subtitle.Translate");
        SubtitleSpeakerColorsButton.ToolTip = T("Subtitle.SpeakerColors");
        SubtitleDiarizationButton.ToolTip = T("Subtitle.Diarization.Enable");
        SubtitleOpenWindowText.Text = T("Subtitle.OpenHistory");
        SubtitleOpenWindowButton.ToolTip = T("Subtitle.OpenHistoryVr");
        Mirror075ScaleButton.ToolTip = "21 cm";
        Mirror100ScaleButton.ToolTip = "28 cm";
        Mirror125ScaleButton.ToolTip = "35 cm";
        Mirror150ScaleButton.ToolTip = "42 cm";
        HomeButton.IsEnabled = false;
    }

    private void UpdateVisualState()
    {
        TranslationOnlyButton.IsChecked = string.Equals(
            _displayMode,
            VoiceTranslationDisplayModes.TranslationOnly,
            StringComparison.OrdinalIgnoreCase);
        OriginalThenTranslationButton.IsChecked = TranslationOnlyButton.IsChecked != true;
        ChineseButton.IsChecked = string.Equals(_targetLanguage, "zh-CN", StringComparison.OrdinalIgnoreCase);
        JapaneseButton.IsChecked = string.Equals(_targetLanguage, "ja-JP", StringComparison.OrdinalIgnoreCase);
        EnglishButton.IsChecked = ChineseButton.IsChecked != true && JapaneseButton.IsChecked != true;
        UpdateSubtitleVisualState();
        LeftEyeButton.IsChecked = string.Equals(_captureEye, "left-eye", StringComparison.OrdinalIgnoreCase);
        RightEyeButton.IsChecked = LeftEyeButton.IsChecked != true;
        PointerSmoothing0Button.IsChecked = _pointerSmoothingStrength == 0;
        PointerSmoothing25Button.IsChecked = _pointerSmoothingStrength == 25;
        PointerSmoothing50Button.IsChecked = _pointerSmoothingStrength == 50;
        PointerSmoothing75Button.IsChecked = _pointerSmoothingStrength == 75;
        PointerSmoothing100Button.IsChecked = _pointerSmoothingStrength == 100;
        if (ChunkIntervalValue is not null)
        {
            ChunkIntervalValue.Text = $"{ChunkIntervalSlider.Value:F0} ms";
        }
        UpdateMirrorVisualState();
        InvalidateOverlay();
    }

    private void SelectMirrorDevice(int direction)
    {
        if (_mirrorState.IsRunning || _mirrorState.IsBusy || _mirrorState.Devices.Count == 0)
        {
            return;
        }

        var index = _mirrorState.Devices
            .Select((device, position) => (device, position))
            .FirstOrDefault(item => string.Equals(
                item.device.Serial,
                _mirrorState.DeviceSerial,
                StringComparison.OrdinalIgnoreCase)).position;
        if (!_mirrorState.Devices.Any(device => string.Equals(
                device.Serial,
                _mirrorState.DeviceSerial,
                StringComparison.OrdinalIgnoreCase)))
        {
            index = direction > 0 ? -1 : 0;
        }
        index = (index + direction + _mirrorState.Devices.Count) % _mirrorState.Devices.Count;
        _mirrorState = _mirrorState with { DeviceSerial = _mirrorState.Devices[index].Serial };
        UpdateMirrorVisualState();
        RaiseAndroidMirrorRequest(VrAndroidMirrorControlRequestKind.ConfigurationChanged);
    }

    private void RaiseAndroidMirrorRequest(VrAndroidMirrorControlRequestKind kind)
    {
        if (_applyingState)
        {
            return;
        }

        AndroidMirrorRequested?.Invoke(
            this,
            new VrAndroidMirrorControlRequestEventArgs(kind, _mirrorState));
    }

    private void UpdateMirrorVisualState()
    {
        if (MirrorDeviceText is null)
        {
            return;
        }

        var selectedDevice = _mirrorState.Devices.FirstOrDefault(device => string.Equals(
            device.Serial,
            _mirrorState.DeviceSerial,
            StringComparison.OrdinalIgnoreCase));
        MirrorDeviceText.Text = selectedDevice?.DisplayName ?? T("VrPanel.Mirror.NoDevice");
        MirrorStatusText.Text = _mirrorState.Status;
        Mirror720Button.IsChecked = _mirrorState.MaximumSize == 720;
        Mirror900Button.IsChecked = _mirrorState.MaximumSize == 900;
        Mirror1080Button.IsChecked = _mirrorState.MaximumSize == 1080;
        Mirror30FpsButton.IsChecked = _mirrorState.MaximumFramesPerSecond == 30;
        Mirror60FpsButton.IsChecked = _mirrorState.MaximumFramesPerSecond == 60;
        Mirror90FpsButton.IsChecked = _mirrorState.MaximumFramesPerSecond == 90;
        Mirror120FpsButton.IsChecked = _mirrorState.MaximumFramesPerSecond == 120;
        Mirror4MbpsButton.IsChecked = _mirrorState.VideoBitRateMbps == 4;
        Mirror8MbpsButton.IsChecked = _mirrorState.VideoBitRateMbps == 8;
        Mirror12MbpsButton.IsChecked = _mirrorState.VideoBitRateMbps == 12;
        Mirror075ScaleButton.IsChecked = Math.Abs(_mirrorState.WindowScale - 0.75) < 0.01;
        Mirror100ScaleButton.IsChecked = Math.Abs(_mirrorState.WindowScale - 1.0) < 0.01;
        Mirror125ScaleButton.IsChecked = Math.Abs(_mirrorState.WindowScale - 1.25) < 0.01;
        Mirror150ScaleButton.IsChecked = Math.Abs(_mirrorState.WindowScale - 1.5) < 0.01;

        var canEdit = !_mirrorState.IsRunning && !_mirrorState.IsBusy;
        MirrorPreviousDeviceButton.IsEnabled = canEdit && _mirrorState.Devices.Count > 1;
        MirrorNextDeviceButton.IsEnabled = canEdit && _mirrorState.Devices.Count > 1;
        MirrorRefreshButton.IsEnabled = canEdit;
        Mirror720Button.IsEnabled = canEdit;
        Mirror900Button.IsEnabled = canEdit;
        Mirror1080Button.IsEnabled = canEdit;
        Mirror30FpsButton.IsEnabled = canEdit;
        Mirror60FpsButton.IsEnabled = canEdit;
        Mirror90FpsButton.IsEnabled = canEdit;
        Mirror120FpsButton.IsEnabled = canEdit;
        Mirror4MbpsButton.IsEnabled = canEdit;
        Mirror8MbpsButton.IsEnabled = canEdit;
        Mirror12MbpsButton.IsEnabled = canEdit;
        Mirror075ScaleButton.IsEnabled = !_mirrorState.IsBusy;
        Mirror100ScaleButton.IsEnabled = !_mirrorState.IsBusy;
        Mirror125ScaleButton.IsEnabled = !_mirrorState.IsBusy;
        Mirror150ScaleButton.IsEnabled = !_mirrorState.IsBusy;
        MirrorStartStopButton.IsEnabled = !_mirrorState.IsBusy &&
            (_mirrorState.IsRunning ||
             (_mirrorState.RuntimeInstalled && selectedDevice is { IsOnline: true }));
        MirrorStartStopIcon.Data = _mirrorState.IsRunning
            ? MaterialIconPaths.Stop
            : MaterialIconPaths.Play;
        MirrorStartStopText.Text = T(_mirrorState.IsRunning
            ? "AndroidMirror.Stop"
            : "AndroidMirror.Start");
        MirrorDownloadButton.Visibility = _mirrorState.RuntimeInstalled
            ? Visibility.Collapsed
            : Visibility.Visible;
        MirrorDownloadButton.IsEnabled = !_mirrorState.IsBusy;
        InvalidateOverlay();
    }

    private static int NormalizeMirrorBitRate(int value) => value switch
    {
        >= 10 => 12,
        >= 6 => 8,
        _ => 4
    };

    private void InvalidateOverlay() => OverlayInvalidated?.Invoke(this, EventArgs.Empty);

    private static string NormalizeTargetLanguage(string? language) => language switch
    {
        "zh-CN" => "zh-CN",
        "ja-JP" => "ja-JP",
        _ => "en-US"
    };

    private static string NormalizeCaptureEye(string? captureEye) =>
        string.Equals(captureEye, "right-eye", StringComparison.OrdinalIgnoreCase)
            ? "right-eye"
            : "left-eye";

    private static int PointerSmoothingPreset(int strength) =>
        new[] { 0, 25, 50, 75, 100 }
            .OrderBy(value => Math.Abs(value - strength))
            .First();

    private void UpdateSubtitleVisualState()
    {
        var wasApplyingState = _applyingState;
        _applyingState = true;
        try
        {
            SubtitleSenseVoiceButton.IsChecked = string.Equals(
                _subtitleState.AsrBackend,
                SubtitleAsrBackends.SenseVoice,
                StringComparison.OrdinalIgnoreCase);
            SubtitleVibeVoiceButton.IsChecked = SubtitleSenseVoiceButton.IsChecked != true;
            SubtitleShowOriginalButton.IsChecked = _subtitleState.ShowOriginalText;
            SubtitleTranslateButton.IsChecked = _subtitleState.TranslateText;
            SubtitleSpeakerColorsButton.IsChecked = _subtitleState.UseSpeakerColors;
            SubtitleChineseButton.IsChecked = string.Equals(
                _subtitleState.TargetLanguage,
                "zh-CN",
                StringComparison.OrdinalIgnoreCase);
            SubtitleJapaneseButton.IsChecked = string.Equals(
                _subtitleState.TargetLanguage,
                "ja-JP",
                StringComparison.OrdinalIgnoreCase);
            SubtitleEnglishButton.IsChecked =
                SubtitleChineseButton.IsChecked != true && SubtitleJapaneseButton.IsChecked != true;
            SubtitleDiarizationButton.IsChecked = _subtitleState.DiarizationEnabled;
            var threads = NormalizeSubtitleThreads(_subtitleState.DiarizationCpuThreadCount);
            SubtitleThread1Button.IsChecked = threads == 1;
            SubtitleThread2Button.IsChecked = threads == 2;
            SubtitleThread4Button.IsChecked = threads == 4;
            SubtitleStatusText.Text = _subtitleState.Status;
        }
        finally
        {
            _applyingState = wasApplyingState;
        }
    }

    private static int NormalizeSubtitleThreads(int threads) => threads switch
    {
        >= 3 => 4,
        2 => 2,
        _ => 1
    };

    private static string T(string key) => AppLocalization.Text(key);
}

public sealed record VrControlPanelState(
    bool VoiceEnabled,
    bool TranslationEnabled,
    bool SendImmediately,
    string DisplayMode,
    string TargetLanguage,
    int ChunkIntervalMilliseconds,
    string CaptureEye,
    VrAndroidMirrorControlState? AndroidMirror = null,
    bool SubtitlesAvailable = true,
    bool AndroidMirrorAvailable = true,
    bool PointerRayEnabled = false,
    int PointerSmoothingStrength = AppConfiguration.DefaultPointerSmoothingStrength,
    VrSubtitleControlState? Subtitles = null);

public sealed record VrSubtitleControlState(
    string AsrBackend,
    bool ShowOriginalText,
    bool TranslateText,
    string TargetLanguage,
    bool UseSpeakerColors,
    bool DiarizationEnabled,
    int DiarizationCpuThreadCount,
    bool IsWindowOpen,
    bool IsListening,
    string Status)
{
    public static VrSubtitleControlState Empty { get; } = new(
        SubtitleAsrBackends.SenseVoice,
        true,
        true,
        "zh-CN",
        true,
        false,
        1,
        false,
        false,
        string.Empty);
}

public enum VrSubtitleControlRequestKind
{
    ConfigurationChanged,
    OpenWindow
}

public sealed class VrSubtitleControlRequestEventArgs(
    VrSubtitleControlRequestKind kind,
    VrSubtitleControlState state) : EventArgs
{
    public VrSubtitleControlRequestKind Kind { get; } = kind;

    public VrSubtitleControlState State { get; } = state;
}

public sealed record VrAndroidMirrorDeviceOption(
    string Serial,
    string DisplayName,
    bool IsOnline);

public sealed record VrAndroidMirrorControlState(
    bool RuntimeInstalled,
    IReadOnlyList<VrAndroidMirrorDeviceOption> Devices,
    string DeviceSerial,
    int MaximumSize,
    int MaximumFramesPerSecond,
    int VideoBitRateMbps,
    double WindowScale,
    bool IsRunning,
    bool IsBusy,
    string Status)
{
    public static VrAndroidMirrorControlState Empty { get; } = new(
        false,
        [],
        string.Empty,
        AndroidMirrorConfiguration.MinimumSize,
        30,
        4,
        AndroidMirrorConfiguration.DefaultWindowScale,
        false,
        false,
        string.Empty);
}

public enum VrAndroidMirrorControlRequestKind
{
    RefreshDevices,
    ConfigurationChanged,
    ToggleConnection,
    DownloadRuntime
}

public sealed class VrAndroidMirrorControlRequestEventArgs(
    VrAndroidMirrorControlRequestKind kind,
    VrAndroidMirrorControlState state) : EventArgs
{
    public VrAndroidMirrorControlRequestKind Kind { get; } = kind;

    public VrAndroidMirrorControlState State { get; } = state;
}

public sealed class VrControlPanelStateChangedEventArgs(VrControlPanelState state) : EventArgs
{
    public VrControlPanelState State { get; } = state;
}
