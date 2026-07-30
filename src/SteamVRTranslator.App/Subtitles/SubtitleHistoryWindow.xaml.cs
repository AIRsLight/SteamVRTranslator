using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SteamVRTranslator.App.Localization;
using SteamVRTranslator.App.SteamVR;
using SteamVRTranslator.Core.Selection;

namespace SteamVRTranslator.App.Subtitles;

public partial class SubtitleHistoryWindow : Window,
    IWpfOverlayInteractionHighlightAware,
    IWpfOverlayInvalidationSource,
    IWpfOverlayPointerHoverAware,
    IWpfOverlayPointerControlResolver
{
    private const double VrButtonHitPadding = 10;
    private readonly SubtitleHistoryViewModel _viewModel;
    private ButtonBase? _hoveredButton;
    private bool _allowClose;

    public SubtitleHistoryWindow(SubtitleHistoryViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        DataContext = viewModel;
        viewModel.Entries.CollectionChanged += OnEntriesChanged;
        foreach (var entry in viewModel.Entries)
        {
            entry.PropertyChanged += OnEntryPropertyChanged;
        }
    }

    public event EventHandler? CloseRequested;

    public event EventHandler? StartStopListeningRequested;

    public event EventHandler? OverlayInvalidated;

    public void ClosePermanently()
    {
        _allowClose = true;
        Close();
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
        var hovered = point is { } normalized
            ? ResolvePointerControl(normalized) as ButtonBase
            : null;
        if (ReferenceEquals(_hoveredButton, hovered))
        {
            if (hovered is null || VrPointerHover.GetIsHovered(hovered))
            {
                return false;
            }
        }

        _hoveredButton = hovered;
        VrPointerHover.SetExclusiveHoveredButton(WindowFrame, hovered);
        InvalidateOverlay();
        return true;
    }

    public FrameworkElement? ResolvePointerControl(NormalizedPoint point)
    {
        if (WindowFrame.ActualWidth <= 1 || WindowFrame.ActualHeight <= 1)
        {
            return null;
        }

        var clamped = point.Clamp();
        var localPoint = new Point(
            clamped.X * WindowFrame.ActualWidth,
            clamped.Y * WindowFrame.ActualHeight);
        ButtonBase? closest = null;
        var closestDistanceSquared = double.MaxValue;
        foreach (var button in new ButtonBase[] { ListenButton, ClearButton, CloseButton })
        {
            if (!button.IsVisible || !button.IsEnabled || button.ActualWidth <= 1 || button.ActualHeight <= 1)
            {
                continue;
            }

            var topLeft = button.TranslatePoint(new Point(0, 0), WindowFrame);
            var bounds = new Rect(topLeft, new Size(button.ActualWidth, button.ActualHeight));
            if (bounds.Contains(localPoint))
            {
                return button;
            }

            bounds.Inflate(VrButtonHitPadding, VrButtonHitPadding);
            if (!bounds.Contains(localPoint))
            {
                continue;
            }

            var center = new Point(
                topLeft.X + (button.ActualWidth / 2),
                topLeft.Y + (button.ActualHeight / 2));
            var x = localPoint.X - center.X;
            var y = localPoint.Y - center.Y;
            var distanceSquared = (x * x) + (y * y);
            if (distanceSquared < closestDistanceSquared)
            {
                closest = button;
                closestDistanceSquared = distanceSquared;
            }
        }

        return closest;
    }

    public void ApplyListeningState(SubtitleListeningState state, string? message = null)
    {
        var active = state != SubtitleListeningState.Stopped;
        ListenButtonIcon.Data = active ? MaterialIconPaths.Stop : MaterialIconPaths.Play;
        ListenButtonText.Text = AppLocalization.Text(
            active ? "Subtitle.Window.StopListening" : "Subtitle.Window.StartListening");
        ListenButton.ToolTip = string.IsNullOrWhiteSpace(message)
            ? ListenButtonText.Text
            : message;
        ListenButton.Foreground = state == SubtitleListeningState.Error
            ? new SolidColorBrush(Color.FromRgb(255, 139, 139))
            : new SolidColorBrush(Color.FromRgb(244, 248, 246));
        InvalidateOverlay();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            Hide();
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.Entries.CollectionChanged -= OnEntriesChanged;
        foreach (var entry in _viewModel.Entries)
        {
            entry.PropertyChanged -= OnEntryPropertyChanged;
        }
        base.OnClosed(e);
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (SubtitleHistoryEntry entry in e.OldItems)
            {
                entry.PropertyChanged -= OnEntryPropertyChanged;
            }
        }
        if (e.NewItems is not null)
        {
            foreach (SubtitleHistoryEntry entry in e.NewItems)
            {
                entry.PropertyChanged += OnEntryPropertyChanged;
            }
        }
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var entry in _viewModel.Entries)
            {
                entry.PropertyChanged -= OnEntryPropertyChanged;
                entry.PropertyChanged += OnEntryPropertyChanged;
            }
        }

        if (_viewModel.Entries.Count > 0)
        {
            Dispatcher.BeginInvoke(() =>
            {
                HistoryListBox.ScrollIntoView(_viewModel.Entries[^1]);
                InvalidateOverlay();
            });
            return;
        }
        InvalidateOverlay();
    }

    private void OnEntryPropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        InvalidateOverlay();

    private void InvalidateOverlay() => OverlayInvalidated?.Invoke(this, EventArgs.Empty);

    private void ClearButton_Click(object sender, RoutedEventArgs e) => _viewModel.Clear();

    private void ListenButton_Click(object sender, RoutedEventArgs e) =>
        StartStopListeningRequested?.Invoke(this, EventArgs.Empty);

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        CloseRequested?.Invoke(this, EventArgs.Empty);
}
