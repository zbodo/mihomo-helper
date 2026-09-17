using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace MihomoTray;

public sealed partial class MainWindow : Window
{
    private const double MinWindowWidthDip = 400;

    private readonly MihomoApiMonitor _apiMonitor;
    private bool _adjustingSize;
    private bool _sizeInitialized;
    private int _minClientHeight;
    private bool _proxyEnabledOnce;
    private bool _webUiChecking;
    private string? _uuidKernelPath;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets\\AppIcon.ico");
        KernelPathBox.PlaceholderText = "选择或粘贴 mihomo 内核 exe";
        string saved = MihomoService.GetSavedKernelPathOrEmpty();
        if (saved.Length > 0)
        {
            KernelPathBox.Text = saved;
        }

        MihomoService.SetActiveKernelPath(GetDisplayedKernelPath());
        WebUiButton.IsEnabled = false;
        _apiMonitor = new MihomoApiMonitor(OnApiLiveChanged);
        Closed += (_, _) => _apiMonitor.Stop();

        ApplyDefaultClientSize();
        FrameworkElement root = (FrameworkElement)Content;
        root.Loaded += OnRootLoaded;
    }

    private async void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        ((FrameworkElement)Content).Loaded -= OnRootLoaded;
        ApplyWindowSize(center: true, applyDefault: true);
        Activate();
        await InitializeStartupAsync();
    }

    private async Task InitializeStartupAsync()
    {
        try
        {
            await RefreshUuidKernelAsync();
        }
        finally
        {
            KernelPathPanel.IsEnabled = true;
        }

        try
        {
            MihomoService.SetActiveKernelPath(GetDisplayedKernelPath());
            await Task.Run(MihomoService.TryPrepareRuntimeConfig);
            SetConfigDependentEnabled(true);
            _apiMonitor.Start();
            await RefreshModeButtonsAsync();
            await MaybeEnableProxyAsync();
            await RefreshStatusAsync();
        }
        catch
        {
            SetConfigDependentEnabled(true);
            _apiMonitor.Start();
            await RefreshModeButtonsAsync();
        }
    }

    private void SetConfigDependentEnabled(bool enabled)
    {
        ProxyToggleButton.IsEnabled = enabled;
        TunToggleButton.IsEnabled = enabled;
        if (!enabled)
        {
            ApplyModeButtons(proxyOn: false, tunOn: false);
        }
    }

    private async Task RefreshUuidKernelAsync()
    {
        string saved = MihomoService.GetSavedKernelPathOrEmpty();
        string? uuid = await Task.Run(MihomoService.GetCurrentTaskKernelPath);
        _uuidKernelPath = uuid;
        KernelPathBox.PlaceholderText = string.IsNullOrEmpty(uuid)
            ? "选择或粘贴 mihomo 内核 exe"
            : uuid;

        string filled = KernelPathBox.Text.Trim();
        if (filled.Length == 0 || string.Equals(filled, saved, StringComparison.OrdinalIgnoreCase))
        {
            KernelPathBox.Text = saved.Length > 0 && !string.Equals(saved, uuid, StringComparison.OrdinalIgnoreCase)
                ? saved
                : "";
        }
    }

    private void OnApiLiveChanged(bool live)
    {
        DispatcherQueue.TryEnqueue(() => _ = HandleApiLiveChangedAsync(live));
    }

    private async Task HandleApiLiveChangedAsync(bool live)
    {
        if (!live)
        {
            WebUiButton.IsEnabled = false;
            bool proxy = false;
            try
            {
                proxy = await Task.Run(MihomoService.QuerySystemProxyEnabled);
            }
            catch
            {
            }

            ApplyModeButtons(proxy, tunOn: false);
            return;
        }

        await RefreshLiveButtonsAsync();
    }

    private async Task RefreshWebUiButtonAsync()
    {
        if (_webUiChecking)
        {
            return;
        }

        _webUiChecking = true;
        try
        {
            MihomoService.SetActiveKernelPath(GetDisplayedKernelPath());
            WebUiButton.IsEnabled = await Task.Run(MihomoService.TryValidateWebUiConfig);
        }
        catch
        {
            WebUiButton.IsEnabled = false;
        }
        finally
        {
            _webUiChecking = false;
        }
    }

    private async Task RefreshLiveButtonsAsync()
    {
        if (_webUiChecking)
        {
            return;
        }

        _webUiChecking = true;
        try
        {
            MihomoService.SetActiveKernelPath(GetDisplayedKernelPath());
            var state = await Task.Run(MihomoService.QueryLiveUiState);
            WebUiButton.IsEnabled = state.WebUiReady;
            ApplyModeButtons(state.ProxyEnabled, state.TunEnabled);
        }
        catch
        {
            WebUiButton.IsEnabled = false;
        }
        finally
        {
            _webUiChecking = false;
        }
    }

    private async Task ShowWebUiConfigErrorAsync()
    {
        WebUiButton.IsEnabled = false;
        ContentDialog dialog = new()
        {
            Title = "启动 WEBUI",
            Content = "没从config中获取到有效配置",
            CloseButtonText = "确定",
            XamlRoot = ((FrameworkElement)Content).XamlRoot
        };
        await dialog.ShowAsync();
    }

    private async Task MaybeEnableProxyAsync()
    {
        if (_proxyEnabledOnce)
        {
            return;
        }

        MihomoService.SetActiveKernelPath(GetDisplayedKernelPath());
        bool enabled = await Task.Run(MihomoService.TryEnableSystemProxyFromConfig);
        if (enabled)
        {
            _proxyEnabledOnce = true;
        }
    }

    private void ApplyDefaultClientSize()
    {
        (int minWidth, int minHeight) = MeasureMinClientSize();
        ApplyMinSizeConstraints(minWidth, minHeight);
        AppWindow.ResizeClient(new SizeInt32(minWidth, minHeight));
    }

    private void ApplyWindowSize(bool center, bool applyDefault)
    {
        if (_adjustingSize)
        {
            return;
        }

        _adjustingSize = true;
        try
        {
            (int minWidth, int minHeight) = MeasureMinClientSize();
            if (!_sizeInitialized)
            {
                _minClientHeight = minHeight;
            }

            ApplyMinSizeConstraints(minWidth, _minClientHeight);
            if (applyDefault && !_sizeInitialized)
            {
                AppWindow.ResizeClient(new SizeInt32(minWidth, _minClientHeight));
                ApplyMinSizeConstraints(minWidth, _minClientHeight);
            }

            _sizeInitialized = true;
            if (center)
            {
                DisplayArea display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
                RectInt32 work = display.WorkArea;
                SizeInt32 size = AppWindow.Size;
                AppWindow.Move(new PointInt32(
                    work.X + (work.Width - size.Width) / 2,
                    work.Y + (work.Height - size.Height) / 2));
            }
        }
        finally
        {
            _adjustingSize = false;
        }
    }

    private (int Width, int Height) MeasureMinClientSize()
    {
        double scale = GetScale();
        int minWidth = (int)Math.Ceiling(MinWindowWidthDip * scale);
        ContentPanel.Measure(new Size(MinWindowWidthDip, double.PositiveInfinity));
        double titleHeight = AppTitleBar.ActualHeight > 1 ? AppTitleBar.ActualHeight : 48;
        int minHeight = Math.Max((int)Math.Ceiling((titleHeight + ContentPanel.DesiredSize.Height) * scale), 240);
        return (minWidth, minHeight);
    }

    private void ApplyMinSizeConstraints(int minClientWidth, int minClientHeight)
    {
        if (AppWindow.Presenter is not OverlappedPresenter presenter)
        {
            return;
        }

        int frameHeight = Math.Max(0, AppWindow.Size.Height - AppWindow.ClientSize.Height);
        presenter.PreferredMinimumWidth = minClientWidth;
        presenter.PreferredMinimumHeight = minClientHeight + frameHeight;
    }

    private double GetScale()
    {
        if (Content is FrameworkElement root && root.XamlRoot is { RasterizationScale: > 0 } xaml)
        {
            return xaml.RasterizationScale;
        }

        uint dpi = NativeMethods.GetDpiForWindow(WindowNative.GetWindowHandle(this));
        return dpi > 0 ? dpi / 96.0 : 1;
    }

    private void PersistKernelPath()
    {
        string path = KernelPathBox.Text.Trim();
        if (path.Length > 0)
        {
            MihomoService.SaveKernelPath(path);
        }

        MihomoService.SetActiveKernelPath(GetDisplayedKernelPath());
    }

    private string GetDisplayedKernelPath()
    {
        string filled = KernelPathBox.Text.Trim();
        if (filled.Length > 0)
        {
            return filled;
        }

        return _uuidKernelPath ?? "";
    }

    private void ApplyStatus((string Proxy, string Tun, string Kernel) status)
    {
        ProxyStatusText.Text = status.Proxy;
        TunStatusText.Text = status.Tun;
        KernelStatusText.Text = status.Kernel;
        ApplyModeButtons(
            string.Equals(status.Proxy, "启用", StringComparison.Ordinal),
            string.Equals(status.Tun, "启用", StringComparison.Ordinal));
    }

    private async Task RefreshModeButtonsAsync()
    {
        if (!ProxyToggleButton.IsEnabled && !TunToggleButton.IsEnabled)
        {
            ApplyModeButtons(proxyOn: false, tunOn: false);
            return;
        }

        try
        {
            var modes = await Task.Run(MihomoService.QueryModeEnabled);
            ApplyModeButtons(modes.ProxyEnabled, modes.TunEnabled);
        }
        catch
        {
            ApplyModeButtons(proxyOn: false, tunOn: false);
        }
    }

    private void ApplyModeButtons(bool proxyOn, bool tunOn)
    {
        ApplyModeButton(ProxyToggleButton, proxyOn, "切换系统代理", "关闭系统代理", "开启系统代理");
        ApplyModeButton(TunToggleButton, tunOn, "切换 TUN", "关闭 TUN", "开启 TUN");
    }

    private static void ApplyModeButton(Button button, bool isOn, string idleText, string onText, string offText)
    {
        if (!button.IsEnabled)
        {
            button.Content = idleText;
            button.ClearValue(FrameworkElement.StyleProperty);
            return;
        }

        button.Content = isOn ? onText : offText;
        if (isOn && Application.Current.Resources.TryGetValue("AccentButtonStyle", out object style) && style is Style accent)
        {
            button.Style = accent;
            return;
        }

        button.ClearValue(FrameworkElement.StyleProperty);
    }

    private async Task RefreshStatusAsync()
    {
        var status = await Task.Run(MihomoService.GetStatus);
        ApplyStatus(status);
    }

    private async void SafeRun(string action, bool skipConfirm = false)
    {
        bool needsPath = action is "InstallTask" or "RemoveTask" or "RemoveTaskConfirm";
        PersistKernelPath();

        try
        {
            KernelStatusText.Text = "处理中...";
            string? path = needsPath ? GetDisplayedKernelPath() : null;
            await Task.Run(() => MihomoService.Run(action, path, skipConfirm));
            _apiMonitor.RetryNowIfDisconnected();
            await RefreshUuidKernelAsync();
            await RefreshStatusAsync();
            await RefreshWebUiButtonAsync();
        }
        catch (OperationCanceledException)
        {
            _apiMonitor.RetryNowIfDisconnected();
            await RefreshStatusAsync();
            await RefreshWebUiButtonAsync();
        }
        catch (Exception ex)
        {
            KernelStatusText.Text = ex.Message;
            _apiMonitor.RetryNowIfDisconnected();
            await RefreshModeButtonsAsync();
            await RefreshWebUiButtonAsync();
        }
    }

    private void OnOpenKernelPath(object sender, RoutedEventArgs e)
    {
        try
        {
            MihomoService.RevealInExplorer(GetDisplayedKernelPath());
        }
        catch (Exception ex)
        {
            KernelStatusText.Text = ex.Message;
        }
    }

    private async void OnBrowseKernel(object sender, RoutedEventArgs e)
    {
        FileOpenPicker picker = new();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.SuggestedStartLocation = PickerLocationId.ComputerFolder;
        picker.FileTypeFilter.Add(".exe");
        StorageFile? file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        KernelPathBox.Text = file.Path;
        MihomoService.SaveKernelPath(file.Path);
        MihomoService.SetActiveKernelPath(file.Path);
        await RefreshStatusAsync();
        await RefreshWebUiButtonAsync();
    }

    private async Task<bool> ConfirmAsync(string title, string content, string primary)
    {
        ContentDialog dialog = new()
        {
            Title = title,
            Content = content,
            PrimaryButtonText = primary,
            CloseButtonText = "取消",
            XamlRoot = ((FrameworkElement)Content).XamlRoot
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void OnRemoveTask(object sender, RoutedEventArgs e)
    {
        PersistKernelPath();
        string message = "确认删除计划任务？";
        if (MihomoService.TryFindTaskToRemove(GetDisplayedKernelPath(), out ManagedTaskInfo? info) && info is not null)
        {
            message = $"任务: {info.FullName}{Environment.NewLine}内核: {info.KernelPath}{Environment.NewLine}{Environment.NewLine}确认删除？";
        }

        if (!await ConfirmAsync("删除计划任务", message, "删除"))
        {
            return;
        }

        SafeRun("RemoveTaskConfirm", true);
    }

    private void OnToggleProxy(object sender, RoutedEventArgs e) => SafeRun("ToggleProxy");
    private void OnToggleTun(object sender, RoutedEventArgs e) => SafeRun("ToggleTun");
    private void OnStartKernel(object sender, RoutedEventArgs e) => SafeRun("StartKernel");

    private async void OnOpenWebUi(object sender, RoutedEventArgs e)
    {
        PersistKernelPath();
        try
        {
            bool ready = await Task.Run(MihomoService.TryValidateWebUiConfig);
            if (!ready)
            {
                await ShowWebUiConfigErrorAsync();
                return;
            }

            bool opened = await Task.Run(MihomoService.TryOpenDashboardFromConfig);
            if (!opened)
            {
                await ShowWebUiConfigErrorAsync();
            }
        }
        catch
        {
            await ShowWebUiConfigErrorAsync();
        }
    }

    private async void OnStopKernel(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("终止内核", "确认终止内核？", "终止"))
        {
            return;
        }

        SafeRun("Stop");
    }

    private async void OnInstallTask(object sender, RoutedEventArgs e)
    {
        PersistKernelPath();
        string kernel = GetDisplayedKernelPath();
        if (!await ConfirmAsync(
            "添加计划任务",
            string.IsNullOrEmpty(kernel) ? "确认添加计划任务？" : "内核: " + kernel + Environment.NewLine + Environment.NewLine + "确认添加？",
            "添加"))
        {
            return;
        }

        SafeRun("InstallTask");
    }

    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        PersistKernelPath();
        await RefreshUuidKernelAsync();
        await RefreshStatusAsync();
        await RefreshWebUiButtonAsync();
    }
}
