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
    private bool _adjustingSize;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets\\AppIcon.ico");
        RefreshKernelPlaceholder();
        string saved = MihomoService.GetSavedKernelPathOrEmpty();
        string? current = MihomoService.GetCurrentTaskKernelPath();
        if (saved.Length > 0 && !string.Equals(saved, current, StringComparison.OrdinalIgnoreCase))
        {
            KernelPathBox.Text = saved;
        }
        RefreshStatus();

        FrameworkElement root = (FrameworkElement)Content;
        root.Loaded += OnRootLoaded;
        root.SizeChanged += OnRootSizeChanged;
    }

    private void OnRootLoaded(object sender, RoutedEventArgs e)
    {
        ((FrameworkElement)Content).Loaded -= OnRootLoaded;
        FitHeightToContent(center: true, initialWidth: true);
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

            double widthDip = ContentPanel.ActualWidth;
            if (widthDip < 1 || initialWidth)
            {
                widthDip = 400;
            }

            ContentPanel.Measure(new Size(widthDip, double.PositiveInfinity));
            double titleHeight = AppTitleBar.ActualHeight > 1 ? AppTitleBar.ActualHeight : 48;
            double contentHeight = ContentPanel.DesiredSize.Height;
            int clientHeight = Math.Clamp((int)Math.Ceiling((titleHeight + contentHeight) * scale), 240, work.Height - 48);
            int clientWidth = initialWidth
                ? Math.Clamp((int)Math.Ceiling(widthDip * scale), 360, work.Width - 48)
                : Math.Max(AppWindow.ClientSize.Width, 360);

            if (AppWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.IsMaximizable = false;
                presenter.PreferredMaximumHeight = work.Height;
            }

            AppWindow.ResizeClient(new SizeInt32(clientWidth, clientHeight));

            if (AppWindow.Presenter is OverlappedPresenter locked)
            {
                locked.PreferredMinimumWidth = 360;
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
    }

    private void RefreshKernelPlaceholder()
    {
        string? current = MihomoService.GetCurrentTaskKernelPath();
        KernelPathBox.PlaceholderText = string.IsNullOrEmpty(current)
            ? "选择或粘贴 mihomo 内核 exe"
            : current;
    }

    private string GetDisplayedKernelPath()
    {
        string filled = KernelPathBox.Text.Trim();
        if (filled.Length > 0)
        {
            return filled;
        }

        return KernelPathBox.PlaceholderText?.Trim() ?? "";
    }

    private void RefreshStatus()
    {
        RefreshKernelPlaceholder();
        var status = MihomoService.GetStatus();
        ProxyStatusText.Text = status.Proxy;
        TunStatusText.Text = status.Tun;
        KernelStatusText.Text = status.Kernel;
        if (ContentPanel.IsLoaded)
        {
            FitHeightToContent(center: false, initialWidth: false);
        }
    }

    private async void SafeRun(string action, bool skipConfirm = false)
    {
        bool needsPath = action is "InstallTask" or "RemoveTask" or "RemoveTaskConfirm";
        if (needsPath)
        {
            PersistKernelPath();
        }

        try
        {
            KernelStatusText.Text = "处理中...";
            string? path = needsPath ? GetDisplayedKernelPath() : null;
            await Task.Run(() => MihomoService.Run(action, path, skipConfirm));
            RefreshStatus();
        }
        catch (OperationCanceledException)
        {
            RefreshStatus();
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
        RefreshStatus();
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
    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        PersistKernelPath();
        RefreshStatus();
    }

    private void OnInstallTask(object sender, RoutedEventArgs e) => SafeRun("InstallTask");
}
