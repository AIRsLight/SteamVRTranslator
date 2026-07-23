using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace SteamVRTranslator.VibeVoice.Manager;

public partial class MainWindow : Window
{
    private static readonly SolidColorBrush NeutralBrush = new(Color.FromRgb(149, 160, 178));
    private static readonly SolidColorBrush SuccessBrush = new(Color.FromRgb(14, 154, 117));
    private static readonly SolidColorBrush DangerBrush = new(Color.FromRgb(209, 76, 88));
    private readonly ManagerStartupOptions _options;
    private readonly ServiceApiClient _api;
    private readonly ServiceProcessController _processController;
    private readonly DispatcherTimer _refreshTimer;
    private CancellationTokenSource? _operationCancellation;
    private ServiceStatusDto? _status;
    private bool _updatingControls;
    private bool _settingsDirty;

    public MainWindow()
        : this(ManagerStartupOptions.Parse(Array.Empty<string>()))
    {
    }

    public MainWindow(ManagerStartupOptions options)
    {
        InitializeComponent();
        _options = options;
        _api = new ServiceApiClient(options.ServiceUri, options.ApiKey);
        _processController = new ServiceProcessController(options);
        _refreshTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, RefreshTimer_Tick, Dispatcher);
        EndpointText.Text = options.ServiceUri.ToString().TrimEnd('/');
        BackendComboBox.SelectedIndex = 0;
        DownloadSourceComboBox.SelectedIndex = 0;
        RuntimeStatusText.Text = ManagerLocalization.Text("Runtime.Missing");
        ModelStatusText.Text = ManagerLocalization.Text("Model.Missing");
        Loaded += MainWindow_Loaded;
        Closed += MainWindow_Closed;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _refreshTimer.Start();
        await RunOperationAsync(async cancellationToken =>
        {
            if (!await _api.IsReachableAsync(cancellationToken) && _processController.CanStartLocalService)
            {
                _processController.Start().Dispose();
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
                while (DateTime.UtcNow < deadline &&
                       !await _api.IsReachableAsync(cancellationToken))
                {
                    await Task.Delay(350, cancellationToken);
                }
            }

            await RefreshStatusAsync(cancellationToken, forceSettings: true);
        });
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        _refreshTimer.Stop();
        _operationCancellation?.Cancel();
        _operationCancellation?.Dispose();
        _api.Dispose();
    }

    private async void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        if (_operationCancellation is not null)
        {
            return;
        }

        try
        {
            await RefreshStatusAsync(CancellationToken.None, forceSettings: false);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            SetOffline(exception.Message);
        }
    }

    private async Task RefreshStatusAsync(CancellationToken cancellationToken, bool forceSettings)
    {
        var status = await _api.GetStatusAsync(cancellationToken);
        _status = status;
        UpdateStatus(status, forceSettings);
    }

    private void UpdateStatus(ServiceStatusDto status, bool forceSettings)
    {
        ServiceStatusDot.Fill = SuccessBrush;
        ServiceStatusText.Text = status.RuntimeRunning
            ? ManagerLocalization.Text("Status.Running")
            : status.CurrentOperation is not null
                ? ManagerLocalization.Text("Status.Installing")
                : ManagerLocalization.Text("Status.Online");

        RuntimeStatusDot.Fill = status.RuntimeInstalled ? SuccessBrush : NeutralBrush;
        RuntimeStatusText.Text = ManagerLocalization.Text(
            status.RuntimeInstalled ? "Runtime.Ready" : "Runtime.Missing");
        ModelStatusDot.Fill = status.ModelInstalled ? SuccessBrush : NeutralBrush;
        ModelStatusText.Text = ManagerLocalization.Text(
            status.ModelInstalled ? "Model.Ready" : "Model.Missing");
        RuntimePathText.Text = status.RuntimePath ?? status.ModelPath;
        OpenDataButton.IsEnabled = Directory.Exists(status.DataDirectory);

        if (forceSettings || !_settingsDirty)
        {
            _updatingControls = true;
            SelectComboItem(BackendComboBox, status.Backend);
            SelectComboItem(DownloadSourceComboBox, status.DownloadSource);
            DeviceIndexTextBox.Text = status.DeviceIndex.ToString();
            ThreadCountTextBox.Text = status.ThreadCount.ToString();
            _settingsDirty = false;
            _updatingControls = false;
        }

        var installing = status.CurrentOperation is not null;
        DownloadProgressPanel.Visibility = installing ? Visibility.Visible : Visibility.Collapsed;
        if (installing)
        {
            var verifying = status.CurrentOperation!.StartsWith("verify:", StringComparison.OrdinalIgnoreCase);
            DownloadProgressText.Text = verifying
                ? ManagerLocalization.Text("Progress.Verifying")
                : FormatProgress(status);
            DownloadProgressBar.IsIndeterminate = status.TotalBytes is null || status.TotalBytes <= 0;
            DownloadProgressBar.Value = status.TotalBytes is > 0
                ? Math.Clamp((double)status.DownloadedBytes / status.TotalBytes.Value, 0, 1)
                : 0;
        }

        SetActionAvailability(installing);
        if (string.IsNullOrWhiteSpace(status.Error))
        {
            ErrorPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            ShowError(status.Error);
        }
    }

    private void SetActionAvailability(bool installing)
    {
        InstallRuntimeButton.IsEnabled = !installing;
        InstallModelButton.IsEnabled = !installing;
        InstallAllButton.IsEnabled = !installing;
        ApplySettingsButton.IsEnabled = !installing;
        StartButton.IsEnabled = !installing && _status is { RuntimeInstalled: true, ModelInstalled: true, RuntimeRunning: false };
        StopButton.IsEnabled = !installing && _status is { RuntimeRunning: true };
    }

    private void SetOffline(string? error)
    {
        ServiceStatusDot.Fill = DangerBrush;
        ServiceStatusText.Text = ManagerLocalization.Text("Status.Offline");
        RuntimeStatusDot.Fill = NeutralBrush;
        ModelStatusDot.Fill = NeutralBrush;
        StartButton.IsEnabled = false;
        StopButton.IsEnabled = false;
        if (!string.IsNullOrWhiteSpace(error))
        {
            ShowError(error);
        }
    }

    private RuntimeSettingsDto ReadSettings()
    {
        var backend = (BackendComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "cpu";
        var source = (DownloadSourceComboBox.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "official";
        var device = int.TryParse(DeviceIndexTextBox.Text, out var parsedDevice)
            ? Math.Max(0, parsedDevice)
            : 0;
        var threads = int.TryParse(ThreadCountTextBox.Text, out var parsedThreads)
            ? Math.Clamp(parsedThreads, 1, Math.Max(1, Environment.ProcessorCount))
            : Math.Min(4, Math.Max(1, Environment.ProcessorCount));
        return new RuntimeSettingsDto(backend, device, threads, source);
    }

    private async Task ApplySettingsAsync(CancellationToken cancellationToken)
    {
        var status = await _api.ConfigureAsync(ReadSettings(), cancellationToken);
        _settingsDirty = false;
        _status = status;
        UpdateStatus(status, forceSettings: true);
    }

    private async Task InstallAsync(string target)
    {
        await RunOperationAsync(async cancellationToken =>
        {
            var status = await _api.InstallAsync(target, ReadSettings(), cancellationToken);
            _settingsDirty = false;
            _status = status;
            UpdateStatus(status, forceSettings: true);
        });
    }

    private async void ApplySettingsButton_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(ApplySettingsAsync);

    private async void InstallRuntimeButton_Click(object sender, RoutedEventArgs e) =>
        await InstallAsync("runtime");

    private async void InstallModelButton_Click(object sender, RoutedEventArgs e) =>
        await InstallAsync("model");

    private async void InstallAllButton_Click(object sender, RoutedEventArgs e) =>
        await InstallAsync("all");

    private async void StartButton_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(async cancellationToken =>
        {
            await ApplySettingsAsync(cancellationToken);
            var status = await _api.StartRuntimeAsync(cancellationToken);
            _status = status;
            UpdateStatus(status, forceSettings: false);
        });

    private async void StopButton_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(async cancellationToken =>
        {
            var status = await _api.StopRuntimeAsync(cancellationToken);
            _status = status;
            UpdateStatus(status, forceSettings: false);
        });

    private async void CancelInstallButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _api.CancelInstallAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            ShowError(exception.Message);
        }
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(cancellationToken => RefreshStatusAsync(cancellationToken, forceSettings: false));

    private void OpenWebButton_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo
        {
            FileName = _options.ServiceUri.ToString(),
            UseShellExecute = true
        });

    private void OpenDataButton_Click(object sender, RoutedEventArgs e)
    {
        var status = _status;
        if (status is null || !Directory.Exists(status.DataDirectory))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = status.DataDirectory,
            UseShellExecute = true
        });
    }

    private void SettingControl_Changed(object sender, EventArgs e)
    {
        if (!_updatingControls)
        {
            _settingsDirty = true;
        }
    }

    private async Task RunOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (_operationCancellation is not null)
        {
            return;
        }

        ErrorPanel.Visibility = Visibility.Collapsed;
        _operationCancellation = new CancellationTokenSource();
        try
        {
            await operation(_operationCancellation.Token);
        }
        catch (OperationCanceledException) when (_operationCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (_status is null &&
                exception is HttpRequestException or TaskCanceledException)
            {
                SetOffline(exception.Message);
            }
            else
            {
                ShowError(exception.Message);
            }
        }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private static void SelectComboItem(ComboBox comboBox, string tag)
    {
        foreach (var item in comboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = item;
                return;
            }
        }
    }

    private static string FormatProgress(ServiceStatusDto status)
    {
        var operation = status.CurrentOperation?.Replace(':', ' ') ?? "download";
        if (status.TotalBytes is not > 0)
        {
            return $"{operation} · {FormatBytes(status.DownloadedBytes)}";
        }

        var percent = Math.Clamp((double)status.DownloadedBytes / status.TotalBytes.Value * 100, 0, 100);
        return $"{operation} · {percent:0.0}% · {FormatBytes(status.DownloadedBytes)} / {FormatBytes(status.TotalBytes.Value)}";
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var amount = (double)Math.Max(0, value);
        var unit = 0;
        while (amount >= 1024 && unit < units.Length - 1)
        {
            amount /= 1024;
            unit++;
        }

        return $"{amount:0.0} {units[unit]}";
    }
}
