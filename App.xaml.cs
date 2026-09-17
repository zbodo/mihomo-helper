using Microsoft.UI.Xaml;

namespace MihomoTray;

public partial class App : Application
{
    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        string[] commandLine = Environment.GetCommandLineArgs();
        if (commandLine.Length > 1)
        {
            try
            {
                string action = commandLine[1];
                string? path = commandLine.Length > 2 ? commandLine[2] : null;
                bool skipConfirm = action == "RemoveTaskConfirm";
                MihomoService.Run(action, path, skipConfirm);
                NativeDialog.Info(MihomoService.GetStatusText());
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                NativeDialog.Error(ex.Message);
            }

            Exit();
            return;
        }

        _window = new MainWindow();
        _window.Activate();
    }
}
