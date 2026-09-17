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

    private bool _adjustingSize;
    private bool _dashboardOpened;
    private bool _proxyEnabledOnce;
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
            await MaybeEnableProxyAsync();
            await MaybeOpenDashboardAsync();
            await RefreshStatusAsync();
        }
        catch
        {
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

    private async Task MaybeOpenDashboardAsync()
    {
        if (_dashboardOpened)
        {
            return;
        }

        MihomoService.SetActiveKernelPath(GetDisplayedKernelPath());
        bool opened = await Task.Run(MihomoService.TryOpenDashboardFromConfig);
        if (opened)
        {
            _dashboardOpened = true;
            Activate();
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
        if (ContentPanel.IsLoaded)
        {
            FitHeightToContent(center: false, initialWidth: false);
        }
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
            await MaybeEnableProxyAsync();
            await MaybeOpenDashboardAsync();
            await RefreshStatusAsync();
        }
        catch (OperationCanceledException)
        {
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            KernelStatusText.Text = ex.Message;
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
        await MaybeEnableProxyAsync();
        await MaybeOpenDashboardAsync();
        await RefreshStatusAsync();
    }

    private async void OnRemoveTask(object sender, RoutedEventArgs e)
    {
        PersistKernelPath();
        if (MihomoService.TryFindTaskToRemove(GetDisplayedKernelPath(), out ManagedTaskInfo? info) && info is not null)
        {
            ContentDialog dialog = new()
            {
                Title = "删除计划任务",
                Content = $"任务: {info.FullName}{Environment.NewLine}内核: {info.KernelPath}",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                XamlRoot = ((FrameworkElement)Content).XamlRoot
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            SafeRun("RemoveTaskConfirm", true);
            return;
        }

        SafeRun("RemoveTask");
    }

    private void OnProxy(object sender, RoutedEventArgs e) => SafeRun("Proxy");
    private void OnTun(object sender, RoutedEventArgs e) => SafeRun("Tun");
    private void OnOff(object sender, RoutedEventArgs e) => SafeRun("Off");
    private void OnToggle(object sender, RoutedEventArgs e) => SafeRun("Toggle");
    private void OnStop(object sender, RoutedEventArgs e) => SafeRun("Stop");
    private async void OnRefresh(object sender, RoutedEventArgs e)
    {
        PersistKernelPath();
        await RefreshUuidKernelAsync();
        await MaybeEnableProxyAsync();
        await MaybeOpenDashboardAsync();
        await RefreshStatusAsync();
    }

    private void OnInstallTask(object sender, RoutedEventArgs e) => SafeRun("InstallTask");
}
