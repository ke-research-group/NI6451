using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Ni6451.Core;
using Ni6451.Daq;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace Ni6451.App;

/// <summary>
/// Assembles the UI and wires user actions to <see cref="DaqAcquisition"/> and
/// <see cref="FinalizeJob"/>. Port of the Python <c>main_window.py</c>, re-laid-out for
/// Windows 11: a Mica window with a fixed settings rail on the left, the live experiment on
/// the right, and a telemetry status bar along the bottom.
///
/// Errors surface as a dismissible <see cref="InfoBar"/> rather than a modal dialog. During a
/// run a modal dialog is actively harmful -- it steals focus from the Stop button while the
/// hardware keeps streaming -- and the Fluent pattern for "something needs your attention but
/// the app still works" is an inline notification.
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>10 fps -- deliberately slower than the plot's own refresh rate.</summary>
    private const int ReadoutIntervalMs = 100;

    // Fixed sensor-to-channel wiring for the live readout.
    private const int ChNormalStress = 0;   // ai0
    private const int ChShearStress = 1;    // ai1
    private const int ChLvdt = 2;           // ai2

    // ai0 normal-stress setup: fault type -> available thicknesses, in (label, metres) pairs.
    // 2D is fixed at 50 cm inside UnitConversion itself, so the value passed for it doesn't matter.
    private static readonly (string Label, double Metres)[] Thickness1DOptions =
        [("5 cm", 0.05), ("10 cm", 0.10)];

    private static readonly (string Label, double Metres)[] Thickness2DOptions =
        [("50 cm (fixed, 9 pistons)", 0.50)];

    private readonly DaqAcquisition _daq = new();
    private readonly DispatcherTimer _readoutTimer = new();

    private string? _saveDir;
    private bool _isFinalizing;
    private bool _isClosing;
    private string? _lastAlert;
    private IReadOnlyList<OrphanedSpool> _orphans = [];

    /// <summary>The off-thread merge itself, so shutdown can wait on it without needing the UI thread.</summary>
    private Task _finalizeWork = Task.CompletedTask;

    private string _currentSerial = "0000";
    private int _currentRn;

    /// <summary>Remembered across launches: last experiment serial, run number and date.</summary>
    private readonly NamingState _naming;
    private readonly string _namingPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ni6451", NamingState.FileName);

    public MainWindow()
    {
        StartupLog.Write("MainWindow: InitializeComponent");
        InitializeComponent();
        StartupLog.Write("MainWindow: XAML loaded");

        Title = "USB-6451 Continuous Acquisition";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.Resize(new SizeInt32(1480, 1000));

        foreach (int rate in AppConfig.SupportedRates)
            RateCombo.Items.Add(AppConfig.FormatRate(rate));
        RateCombo.SelectedIndex = Array.IndexOf(AppConfig.SupportedRates, AppConfig.DefaultRate);
        UpdateRateCaption();

        CaptureTriggerToggle.IsOn = AppConfig.DefaultCaptureTrigger;
        TriggerLineBox.Text = AppConfig.DefaultTriggerLine;
        TriggerLineBox.IsEnabled = CaptureTriggerToggle.IsOn;

        Fault1DRadio.IsChecked = true;   // also populates the thickness list

        RnBox.Minimum = 0;
        RnBox.Maximum = 999_999;
        _naming = NamingState.Load(_namingPath);
        ApplyNextNaming();

        _daq.MonitorChunkReady += OnMonitorChunk;
        _daq.Error += OnDaqError;
        _daq.DataLoss += OnDataLoss;

        _readoutTimer.Interval = TimeSpan.FromMilliseconds(ReadoutIntervalMs);
        _readoutTimer.Tick += OnReadoutTick;

        Closed += OnClosed;

        SetStatus("Idle", StatusKind.Idle);
        StartupLog.Write("MainWindow: querying NI-DAQmx devices");
        RefreshDevices();
        StartupLog.Write("MainWindow: ready");
    }

    private FrameworkElement Root => (FrameworkElement)Content;

    // ---------- sampling rate ----------

    /// <summary>The rate selected in the menu; falls back to the default if nothing is selected.</summary>
    private int SelectedRate =>
        RateCombo.SelectedIndex >= 0 && RateCombo.SelectedIndex < AppConfig.SupportedRates.Length
            ? AppConfig.SupportedRates[RateCombo.SelectedIndex]
            : AppConfig.DefaultRate;

    private void OnRateChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateRateCaption();
        UpdateDiskFree();
    }

    private void UpdateRateCaption()
    {
        if (FixedParamsText is null) return;
        int rate = SelectedRate;
        FixedParamsText.Text =
            $"{rate:N0} S/s per channel · {AppConfig.ChunkFor(rate):N0}-sample chunks ({AppConfig.ChunkMilliseconds} ms) · "
            + $"spooled continuously, flushed every {AppConfig.FlushIntervalSec}s";
    }

    // ---------- device list ----------

    private void OnRefreshDevices(object sender, RoutedEventArgs e) => RefreshDevices();

    private void RefreshDevices()
    {
        string current = DeviceCombo.Text;
        IReadOnlyList<string> devices;
        try
        {
            devices = DeviceEnumerator.ListDevices();
        }
        catch (Exception ex) when (ex is DaqmxException or DllNotFoundException or BadImageFormatException)
        {
            ShowAlert(InfoBarSeverity.Warning, "NI-DAQmx unavailable", ex.Message);
            return;
        }

        DeviceCombo.Items.Clear();
        if (devices.Count == 0)
        {
            ShowAlert(InfoBarSeverity.Warning, "No devices found",
                "NI-DAQmx reports no connected devices. Check the driver installation and the USB connection, "
                + "or type the device name in manually.");
            return;
        }

        foreach (string d in devices) DeviceCombo.Items.Add(d);
        DeviceCombo.SelectedItem = devices.Contains(current) ? current : devices[0];
        DeviceCombo.Text = DeviceCombo.SelectedItem as string ?? string.Empty;
        ClearAlert();
    }

    private async void OnChooseFolder(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));

        StorageFolder? folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        _saveDir = folder.Path;
        OutputFolderText.Text = folder.Path;
        StartButton.IsEnabled = !_isFinalizing && !_daq.IsRunning;

        UpdateDiskFree();
        ScanForInterruptedRuns();
    }

    private void UpdateDiskFree()
    {
        if (_saveDir is null) return;

        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(_saveDir));
            if (root is null) return;

            long free = new DriveInfo(root).AvailableFreeSpace;

            // Every active channel costs rate × 8 bytes/s (4 MB/s at 500 kS/s). Turning free space
            // into minutes of recording is the number that actually matters before pressing Start.
            int channels = Math.Max(1, TraceView.EnabledChannels().Length);
            double bytesPerSecond = (double)SelectedRate * channels * sizeof(double);

            // The merge writes a second full copy alongside the spool before the spool is removed.
            double minutes = free / bytesPerSecond / 2.0 / 60.0;
            DiskFreeText.Text =
                $"{free / (1024.0 * 1024 * 1024):F1} GB free · about {minutes:F0} min at {channels} channel(s), {AppConfig.FormatRate(SelectedRate)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            DiskFreeText.Text = string.Empty;
        }
    }

    // ---------- crash recovery ----------

    private void ScanForInterruptedRuns()
    {
        if (_saveDir is null) return;

        _orphans = SpoolRecovery.FindOrphans(_saveDir);
        if (_orphans.Count == 0)
        {
            RecoveryBar.IsOpen = false;
            return;
        }

        double seconds = _orphans.Sum(o => o.DurationSeconds);
        RecoveryBar.Message =
            $"{_orphans.Count} acquisition(s) in this folder were interrupted before being saved, "
            + $"holding about {seconds:F0}s of data. The samples are still on disk and can be recovered.";
        RecoveryBar.IsOpen = true;
    }

    private async void OnRecover(object sender, RoutedEventArgs e)
    {
        if (_saveDir is null || _orphans.Count == 0) return;

        IReadOnlyList<OrphanedSpool> toRecover = _orphans;
        RecoverButton.IsEnabled = false;
        RecoveryBar.IsOpen = false;
        SetStatus($"Recovering {toRecover.Count} interrupted acquisition(s)…", StatusKind.Busy);
        ShowSaveProgress(true);

        var progress = new Progress<FinalizeProgress>(p => SaveProgress.Value = p.Fraction * 100);
        var recovered = new List<string>();
        string? failure = null;

        try
        {
            await Task.Run(() =>
            {
                foreach (OrphanedSpool orphan in toRecover)
                    recovered.Add(SpoolRecovery.Recover(orphan, _saveDir, progress));
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            failure = ex.Message;
        }
        finally
        {
            ShowSaveProgress(false);
            RecoverButton.IsEnabled = true;
        }

        if (failure is not null)
        {
            SetStatus("Recovery failed", StatusKind.Error);
            ShowAlert(InfoBarSeverity.Error, "Recovery failed",
                $"{failure} The raw spool files were left in place, so nothing was lost — you can retry.");
        }
        else
        {
            SetStatus($"Recovered {recovered.Count} recording(s)", StatusKind.Idle);
            ShowAlert(InfoBarSeverity.Success, "Recovery complete",
                string.Join("\n", recovered.Select(Path.GetFileName)));
        }

        ScanForInterruptedRuns();
        UpdateDiskFree();
    }

    // ---------- fault setup / naming ----------

    private void OnFaultTypeChanged(object sender, RoutedEventArgs e) => PopulateThicknessOptions();

    private void OnCaptureTriggerToggled(object sender, RoutedEventArgs e)
    {
        if (TriggerLineBox is not null)
            TriggerLineBox.IsEnabled = CaptureTriggerToggle.IsOn && !_daq.IsRunning && !_isFinalizing;
    }

    private void PopulateThicknessOptions()
    {
        if (FaultThicknessCombo is null) return;

        bool isOneD = CurrentFaultType == FaultType.OneD;
        (string Label, double Metres)[] options = isOneD ? Thickness1DOptions : Thickness2DOptions;

        FaultThicknessCombo.Items.Clear();
        foreach ((string label, _) in options) FaultThicknessCombo.Items.Add(label);
        FaultThicknessCombo.SelectedIndex = 0;
        FaultThicknessCombo.IsEnabled = isOneD && !_daq.IsRunning && !_isFinalizing;
    }

    private FaultType CurrentFaultType => Fault2DRadio.IsChecked == true ? FaultType.TwoD : FaultType.OneD;

    private double CurrentFaultThicknessM
    {
        get
        {
            (string Label, double Metres)[] options =
                CurrentFaultType == FaultType.OneD ? Thickness1DOptions : Thickness2DOptions;
            int index = Math.Clamp(FaultThicknessCombo.SelectedIndex, 0, options.Length - 1);
            return options[index].Metres;
        }
    }

    private void OnNamingChanged(object sender, TextChangedEventArgs e)
    {
        // Stand-in for the Qt QIntValidator(0, 9999): keep the field digits-only.
        string digits = new(SerialBox.Text.Where(char.IsAsciiDigit).ToArray());
        if (digits != SerialBox.Text)
        {
            int caret = Math.Max(0, SerialBox.SelectionStart - (SerialBox.Text.Length - digits.Length));
            SerialBox.Text = digits;
            SerialBox.SelectionStart = Math.Min(caret, digits.Length);
            return;   // the assignment re-enters this handler
        }

        UpdateFileNamePreview();
    }

    private void OnRunNumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsNaN(args.NewValue)) sender.Value = 0;
        UpdateFileNamePreview();
    }

    private string CurrentSerialPadded => SerialBox.Text.Trim().PadLeft(4, '0');

    /// <summary>
    /// Seed the naming fields from the remembered state: same day as the last run keeps the
    /// serial and advances the run number; a new day advances the serial and starts at run 1.
    /// The operator can still overtype either field before pressing Start.
    /// </summary>
    private void ApplyNextNaming()
    {
        (string serial, int run) = _naming.NextFor(DateOnly.FromDateTime(DateTime.Now));
        SerialBox.Text = serial;
        RnBox.Value = run;
        UpdateFileNamePreview();

        NamingHintText.Text = string.IsNullOrEmpty(_naming.LastRunDate)
            ? "No previous run recorded on this machine."
            : $"Last run: T{NamingState.Pad(_naming.ExperimentSerial)} run {_naming.RunNumber} on {_naming.LastRunDate}. "
              + (_naming.LastRunDate == NamingState.Format(DateOnly.FromDateTime(DateTime.Now))
                  ? "Same day, so the run number advanced."
                  : "New day, so the serial advanced and the run number reset.");
    }

    private int CurrentRn => double.IsNaN(RnBox.Value) ? 0 : (int)RnBox.Value;

    private void UpdateFileNamePreview()
    {
        if (FileNamePreviewText is null) return;
        FileNamePreviewText.Text = $"T{CurrentSerialPadded}-raw-run{CurrentRn}-<timestamp>.npz";
    }

    // ---------- acquisition control ----------

    private void OnStart(object sender, RoutedEventArgs e)
    {
        string device = DeviceCombo.Text.Trim();
        if (string.IsNullOrEmpty(device))
        {
            ShowAlert(InfoBarSeverity.Warning, "No device", "Select or type a device name first.");
            return;
        }

        if (_saveDir is null)
        {
            ShowAlert(InfoBarSeverity.Warning, "No output folder", "Choose a folder for the recording first.");
            return;
        }

        int[] enabled = TraceView.EnabledChannels();
        if (enabled.Length == 0)
        {
            ShowAlert(InfoBarSeverity.Warning, "No channels", "Tick at least one channel to acquire.");
            return;
        }

        ClearAlert();
        RecoveryBar.IsOpen = false;
        SetControlsLocked(true);

        // Capture the naming/fault settings now, so later edits don't retroactively affect
        // the file this run is about to produce.
        _currentSerial = CurrentSerialPadded;
        _currentRn = CurrentRn;

        // Remember it immediately -- before the hardware starts -- so even a run that ends in
        // a crash still advances the numbering next time.
        _naming.RecordRun(_currentSerial, _currentRn, DateOnly.FromDateTime(DateTime.Now));
        _naming.Save(_namingPath);

        SetTriggerTile(triggered: false);
        NormalStressText.Text = "—";
        ShearStressText.Text = "—";
        LvdtText.Text = "—";

        int rate = SelectedRate;
        var manifest = new SpoolManifest { ExperimentSerial = _currentSerial, Rn = _currentRn };
        _daq.Start(device, _saveDir, enabled, rate, CaptureTriggerToggle.IsOn, TriggerLineBox.Text.Trim(), manifest);

        if (_daq.IsRunning)
        {
            StopButton.IsEnabled = true;
            SetStatus($"Acquiring · {AppConfig.FormatRate(rate)} × {enabled.Length} channels", StatusKind.Recording);
            StatsPanel.Visibility = Visibility.Visible;
            TraceView.Start(enabled);
            _readoutTimer.Start();
        }
        else
        {
            SetControlsLocked(false);
        }
    }

    private void OnStop(object sender, RoutedEventArgs e) => StopAcquisition();

    private void StopAcquisition()
    {
        StopButton.IsEnabled = false;
        TraceView.Stop();
        _readoutTimer.Stop();
        SetTriggerTile(triggered: false);
        StatsPanel.Visibility = Visibility.Collapsed;

        AcquisitionResult? result = _daq.StopAcquisition();
        if (result is null)
        {
            SetStatus("Idle", StatusKind.Idle);
            SetControlsLocked(false);
            ShowAlert(InfoBarSeverity.Informational, "Nothing recorded", "The run produced no samples.");
            return;
        }

        SetStatus($"Saving {result.SamplesPerChannel:N0} samples/channel…", StatusKind.Busy);
        ShowSaveProgress(true);

        // Controls stay locked until the background save finishes, so a new acquisition
        // can't be started while the previous one is still being written.
        _isFinalizing = true;
        var request = new FinalizeRequest(
            result.TempDir, result.SamplesPerChannel, _saveDir!, result.Channels,
            result.TriggerSampleIndex, _currentSerial, _currentRn, result.SampleRate);

        // Deliberately not awaited: Stop returns immediately and the status bar is updated
        // when the merge lands, which is how the Qt version behaved with its FinalizeWorker.
        _ = FinalizeAsync(request, result);
    }

    private async Task FinalizeAsync(FinalizeRequest request, AcquisitionResult result)
    {
        var progress = new Progress<FinalizeProgress>(p => SaveProgress.Value = p.Fraction * 100);

        try
        {
            Task<string> work = Task.Run(() => FinalizeJob.Run(request, progress));
            _finalizeWork = work;
            string outPath = await work;

            string trigger = result.TriggerSampleIndex is { } idx ? $" · trigger at sample {idx:N0}" : string.Empty;
            SetStatus($"Saved {Path.GetFileName(outPath)} · {result.SamplesPerChannel:N0} samples/channel{trigger}",
                      StatusKind.Idle);

            ReportRunQuality(result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetStatus("Save failed", StatusKind.Error);
            ShowAlert(InfoBarSeverity.Error, "Save failed",
                $"{ex.Message} The raw spool files were kept, so the data is still recoverable — "
                + "reopen the output folder to be offered a recovery.");
        }
        finally
        {
            ShowSaveProgress(false);
            _isFinalizing = false;
            if (!_isClosing)
            {
                SetControlsLocked(false);
                ApplyNextNaming();   // the run just completed is now "the last run"
                UpdateDiskFree();
                ScanForInterruptedRuns();
            }
        }
    }

    /// <summary>Summarise how the pipeline coped, so a marginal setup is noticed before the next run.</summary>
    private void ReportRunQuality(AcquisitionResult result)
    {
        AcquisitionSnapshot s = result.Stats;
        if (s.HasDataLoss)
        {
            ShowAlert(InfoBarSeverity.Error, "Samples were lost during this run",
                $"{s.Overruns} driver overrun(s) and {s.DroppedChunks} dropped chunk(s). "
                + "Everything that reached the disk was saved. Use fewer channels, or a faster output drive.");
            return;
        }

        if (s.PeakQueueDepth > s.QueueCapacity * 0.75)
        {
            ShowAlert(InfoBarSeverity.Warning, "The disk only just kept up",
                $"The write queue reached {s.PeakQueueDepth} of {s.QueueCapacity} at {s.MegabytesPerSecond:F0} MB/s. "
                + "No data was lost, but a longer run on this drive may not be safe.");
        }
    }

    private void SetControlsLocked(bool locked)
    {
        ChooseFolderButton.IsEnabled = !locked;
        StartButton.IsEnabled = !locked && _saveDir is not null;
        DeviceCombo.IsEnabled = !locked;
        RateCombo.IsEnabled = !locked;
        RefreshButton.IsEnabled = !locked;
        CaptureTriggerToggle.IsEnabled = !locked;
        TriggerLineBox.IsEnabled = !locked && CaptureTriggerToggle.IsOn;
        Fault1DRadio.IsEnabled = !locked;
        Fault2DRadio.IsEnabled = !locked;
        FaultThicknessCombo.IsEnabled = !locked && CurrentFaultType == FaultType.OneD;
        SerialBox.IsEnabled = !locked;
        RnBox.IsEnabled = !locked;
        TraceView.SetLocked(locked);
        if (!locked) StopButton.IsEnabled = false;
    }

    // ---------- live data ----------

    /// <summary>
    /// Runs on the acquisition's monitor thread, never on the DAQmx callback thread, so the
    /// rolling buffer's lock can never be contended by the hardware path.
    /// </summary>
    private void OnMonitorChunk(ReadOnlyMemory<double> chunk, int nSamples)
        => TraceView.PushChunk(chunk.Span, nSamples);

    private void OnReadoutTick(object? sender, object e)
    {
        SetTriggerTile(_daq.TriggerSampleIndex is not null);

        // Read the fault setup once per tick: the Python version only evaluated it inside
        // the ai0 branch, so a run with ai0 deselected but ai1 selected raised NameError
        // when it came to format the shear stress.
        FaultType faultType = CurrentFaultType;
        double thicknessM = CurrentFaultThicknessM;

        NormalStressText.Text = _daq.TryGetLatestVoltage(ChNormalStress, out double v0)
            ? $"{UnitConversion.GetNormalStress(v0, faultType, thicknessM) / 1e6:F1}"
            : "—";

        ShearStressText.Text = _daq.TryGetLatestVoltage(ChShearStress, out double v1)
            ? $"{UnitConversion.GetShearStress(v1, faultType, thicknessM) / 1e6:F1}"
            : "—";

        LvdtText.Text = _daq.TryGetLatestVoltage(ChLvdt, out double v2)
            ? $"{UnitConversion.GetLvdtDisplacement(v2) * 1000:F1}"
            : "—";

        UpdateStats(_daq.Stats.Snapshot());
    }

    private void UpdateStats(AcquisitionSnapshot s)
    {
        StatElapsed.Text = s.Elapsed.TotalHours >= 1
            ? $"{(int)s.Elapsed.TotalHours}:{s.Elapsed.Minutes:00}:{s.Elapsed.Seconds:00}"
            : $"{s.Elapsed.Minutes}:{s.Elapsed.Seconds:00}";

        double mb = s.BytesSpooled / (1024.0 * 1024.0);
        StatWritten.Text = mb >= 1024 ? $"{mb / 1024:F2} GB" : $"{mb:F0} MB";
        StatThroughput.Text = $"{s.MegabytesPerSecond:F0} MB/s";

        StatQueueBar.Value = s.QueuePressure;
        StatQueue.Text = $"{s.QueuePressure * 100:F0}%";
        StatQueueBar.ShowError = s.QueuePressure > 0.75;
    }

    private void SetTriggerTile(bool triggered)
    {
        TriggerValueText.Text = triggered ? "Fired" : "Waiting";
        TriggerIcon.Glyph = triggered ? "" : "";

        Brush brush = triggered
            ? ThemeBrush("SystemFillColorSuccessBrush", 0x0F, 0x7B, 0x0F)
            : ThemeBrush("TextFillColorTertiaryBrush", 0x8A, 0x8A, 0x8A);
        TriggerIcon.Foreground = brush;
        TriggerValueText.Foreground = brush;
    }

    // ---------- status and alerts ----------

    private enum StatusKind { Idle, Recording, Busy, Error }

    private void SetStatus(string text, StatusKind kind)
    {
        StatusText.Text = text;
        StatusDot.Fill = kind switch
        {
            StatusKind.Recording => ThemeBrush("SystemFillColorCriticalBrush", 0xC4, 0x2B, 0x1C),
            StatusKind.Busy => ThemeBrush("SystemFillColorCautionBrush", 0x9D, 0x5D, 0x00),
            StatusKind.Error => ThemeBrush("SystemFillColorCriticalBrush", 0xC4, 0x2B, 0x1C),
            _ => ThemeBrush("TextFillColorTertiaryBrush", 0x8A, 0x8A, 0x8A),
        };
    }

    private readonly Dictionary<string, Brush> _brushCache = new();

    /// <summary>
    /// Resolve a theme brush by key, falling back to a fixed colour if the lookup fails.
    /// Indexing <c>Application.Current.Resources</c> directly throws on a missing key, and
    /// the constructor is not the place to find out a resource name was wrong.
    /// </summary>
    private Brush ThemeBrush(string key, byte r, byte g, byte b)
    {
        if (_brushCache.TryGetValue(key, out Brush? cached)) return cached;

        Brush brush;
        try
        {
            brush = Application.Current.Resources.TryGetValue(key, out object? value) && value is Brush themed
                ? themed
                : new SolidColorBrush(Color.FromArgb(255, r, g, b));
        }
        catch (Exception e) when (e is InvalidCastException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            brush = new SolidColorBrush(Color.FromArgb(255, r, g, b));
        }

        _brushCache[key] = brush;
        return brush;
    }

    private void ShowSaveProgress(bool visible)
    {
        SaveProgress.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible) SaveProgress.Value = 0;
    }

    private void ShowAlert(InfoBarSeverity severity, string title, string message)
    {
        if (_isClosing) return;

        string key = severity + title + message;
        if (key == _lastAlert && AlertBar.IsOpen) return;   // don't restate an error already on screen
        _lastAlert = key;

        AlertBar.Severity = severity;
        AlertBar.Title = title;
        AlertBar.Message = message;
        AlertBar.IsOpen = true;
    }

    private void ClearAlert()
    {
        AlertBar.IsOpen = false;
        _lastAlert = null;
    }

    // ---------- errors ----------

    /// <summary>May be raised on a background thread, so it hops to the UI thread first.</summary>
    private void OnDaqError(string message) => OnUiThread(() => HandleDaqError(message));

    private void OnDataLoss(string message) =>
        OnUiThread(() => ShowAlert(InfoBarSeverity.Error, "Data loss", message));

    private void HandleDaqError(string message)
    {
        // Treat a hardware error the same as pressing Stop: properly close the task(s) and
        // salvage whatever was already captured, instead of leaving an orphaned task running
        // in the background with the Stop button disabled.
        if (_daq.IsRunning)
        {
            StopAcquisition();
        }
        else
        {
            TraceView.Stop();
            _readoutTimer.Stop();
            StatsPanel.Visibility = Visibility.Collapsed;
            if (!_isFinalizing) SetControlsLocked(false);
        }

        ShowAlert(InfoBarSeverity.Error, "DAQ error", message);
    }

    private void OnUiThread(Action action)
    {
        if (DispatcherQueue.HasThreadAccess) action();
        else DispatcherQueue.TryEnqueue(() => action());
    }

    // ---------- shutdown ----------

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _isClosing = true;
        _readoutTimer.Stop();
        TraceView.Stop();

        // Stop the hardware first so nothing keeps writing, then let any in-flight merge
        // finish -- abandoning it would leave a half-written .npz next to spool files.
        if (_daq.IsRunning) _daq.StopAcquisition();
        _daq.Dispose();

        // _finalizeWork is the Task.Run body, which needs no UI thread, so blocking the
        // closing UI thread on it cannot deadlock the way waiting on the async wrapper would.
        try { _finalizeWork.Wait(TimeSpan.FromMinutes(10)); }
        catch (AggregateException) { /* already surfaced through FinalizeAsync */ }

        TraceView.Dispose();
    }
}
