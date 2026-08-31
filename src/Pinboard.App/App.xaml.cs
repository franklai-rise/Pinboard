using System.Windows;
using Pinboard.App.Models;
using Pinboard.App.Services;

namespace Pinboard.App;

public partial class App : System.Windows.Application
{
    private SingleInstanceService? _singleInstance;
    private MainWindow? _mainWindow;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, eventArgs) =>
            DiagnosticLog.Write("dispatcher-unhandled", eventArgs.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
            {
                DiagnosticLog.Write("appdomain-unhandled", exception);
            }
        };
        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            DiagnosticLog.Write("task-unobserved", eventArgs.Exception);
            eventArgs.SetObserved();
        };
        _singleInstance = new SingleInstanceService();
        if (!_singleInstance.IsPrimary)
        {
            try
            {
                await _singleInstance.SendArgumentsAsync(e.Args);
            }
            catch (Exception ex)
            {
                MessageBox.Show(LocalizationService.T("SingleInstanceForwardFailedFormat", ex.Message), "Pinboard", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            Shutdown();
            return;
        }

        var settings = AppSettings.Load();
        LocalizationService.Apply(settings.Language);
        Directory.CreateDirectory(settings.LibraryPath);
        Directory.CreateDirectory(AppSettings.RecoveryDirectory);

        try
        {
            new FileAssociationService().Register();
        }
        catch
        {
            // A portable app still remains usable when registry policy blocks associations.
        }

        try
        {
            new StartupService().SetEnabled(settings.StartAtLogin);
        }
        catch
        {
            // Startup registration is optional when an endpoint policy blocks the Run key.
        }

        var startHidden = ActivationParser.IsBackgroundRequest(e.Args);
        _mainWindow = new MainWindow(settings, startHidden);
        MainWindow = _mainWindow;
        _singleInstance.StartServer(args => Dispatcher.InvokeAsync(() => _mainWindow.HandleActivationAsync(args)).Task.Unwrap());
        await _mainWindow.InitializeAsync(e.Args);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}

internal static class ActivationParser
{
    public static bool IsCaptureRequest(IEnumerable<string> args) => args.Any(IsCaptureArgument);
    public static bool IsBackgroundRequest(IEnumerable<string> args) => args.Any(IsBackgroundArgument) || IsCaptureRequest(args);

    public static bool IsBackgroundArgument(string arg) =>
        string.Equals(arg, "--background", StringComparison.OrdinalIgnoreCase);

    public static bool IsCaptureArgument(string arg)
    {
        if (string.Equals(arg, "--capture-next", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return Uri.TryCreate(arg, UriKind.Absolute, out var uri)
               && uri.Scheme.Equals("pinboard", StringComparison.OrdinalIgnoreCase)
               && uri.Host.Equals("capture-next", StringComparison.OrdinalIgnoreCase);
    }
}
