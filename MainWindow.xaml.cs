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
    private const double MinWindowWidthDip = 360;
    private const double InitialWindowWidthDip = 400;
    private const double MaxWindowWidthDip = 520;

    private readonly DispatcherTimer _webUiTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private bool _adjustingSize;
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
        _webUiTimer.Tick += OnWebUiTimerTick;
        Closed += (_, _) => _webUiTimer.Stop();

        FrameworkElement root = (FrameworkElement)Content;
        root.Loaded += OnRootLoaded;
        root.SizeChanged += OnRootSizeChanged;
    }

    private async void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        ((FrameworkElement)Content).Loaded -= OnRootLoaded;
        FitHeightToContent(center: true, initialWidth: true);
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
            await RefreshWebUiButtonAsync();
            _webUiTimer.Start();
            await MaybeEnableProxyAsync();
            await RefreshStatusAsync();
        }
        catch
        {
            SetConfigDependentEnabled(true);
            _webUiTimer.Start();
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

        if (ContentPanel.IsLoaded)
        {
            FitHeightToContent(center: false, initialWidth: false);
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

    private async void OnWebUiTimerTick(object? sender, object e)
    {
        await RefreshWebUiButtonAsync();
        await RefreshModeButtonsAsync();
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

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_adjustingSize || Math.Abs(e.NewSize.Width - e.PreviousSize.Width) < 0.5)
        {
            return;
        }

        FitHeightToContent(center: false, initialWidth: false);
    }

    private void FitHeightToContent(bool center, bool initialWidth)
    {
        if (_adjustingSize)
        {
            return;
        }

        _adjustingSize = true;
        try
        {
            FrameworkElement root = (FrameworkElement)Content;
            double scale = root.XamlRoot?.RasterizationScale ?? 1;
            DisplayArea display = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest);
            RectInt32 work = display.WorkArea;

            int minWidth = (int)Math.Ceiling(MinWindowWidthDip * scale);
            int maxWidth = Math.Min((int)Math.Ceiling(MaxWindowWidthDip * scale), Math.Max(minWidth, work.Width - 48));
            double measureWidth = initialWidth || ContentPanel.ActualWidth < 1
                ? InitialWindowWidthDip
                : Math.Clamp(ContentPanel.ActualWidth, MinWindowWidthDip, MaxWindowWidthDip);
            ContentPanel.Measure(new Size(measureWidth, double.PositiveInfinity));
            double titleHeight = AppTitleBar.ActualHeight > 1 ? AppTitleBar.ActualHeight : 48;
            double contentHeight = ContentPanel.DesiredSize.Height;
            int clientHeight = Math.Clamp((int)Math.Ceiling((titleHeight + contentHeight) * scale), 240, work.Height - 48);
            int clientWidth = initialWidth
                ? (int)Math.Ceiling(InitialWindowWidthDip * scale)
                : Math.Clamp(AppWindow.ClientSize.Width, minWidth, maxWidth);
            clientWidth = Math.Clamp(clientWidth, minWidth, maxWidth);

            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsMaximizable = false;
                presenter.PreferredMinimumWidth = minWidth;
                presenter.PreferredMaximumWidth = maxWidth;
                presenter.PreferredMaximumHeight = work.Height;
            }

            AppWindow.ResizeClient(new SizeInt32(clientWidth, clientHeight));

            if (AppWindow.Presenter is OverlappedPresenter locked)
            {
                locked.PreferredMinimumWidth = minWidth;
                locked.PreferredMaximumWidth = maxWidth;
                locked.PreferredMinimumHeight = AppWindow.Size.Height;
                locked.PreferredMaximumHeight = AppWindow.Size.Height;
            }

            if (center)
            {
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
        if (ContentPanel.IsLoaded)
        {
            FitHeightToContent(center: false, initialWidth: false);
        }
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
            await RefreshUuidKernelAsync();
            await RefreshStatusAsync();
        }
        catch (OperationCanceledException)
        {
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            KernelStatusText.Text = ex.Message;
            await RefreshModeButtonsAsync();
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
    }
}
