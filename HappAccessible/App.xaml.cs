using System.Windows;
using System.Windows.Threading;
using HappAccessible.Services;

namespace HappAccessible;

public partial class App : System.Windows.Application
{
    private SingleInstanceManager? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        var elevatedHandoff = e.Args.Any(
            arg => string.Equals(arg, "--elevated-handoff", StringComparison.OrdinalIgnoreCase));
        _singleInstance = new SingleInstanceManager(elevatedHandoff);
        if (!_singleInstance.IsFirstInstance)
        {
            _singleInstance.TryActivateExistingInstance();
            _singleInstance.Dispose();
            Shutdown();
            return;
        }

        base.OnStartup(e);
        AppLogService.EnsureLogFile();
        AppUpdateService.CleanupStaleUpdateArtifacts();
        AppLogService.Info("Приложение запущено, версия " + AppUpdateService.GetCurrentVersion());
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                AppLogService.Error("Необработанное исключение (домен)", ex);
            else
                AppLogService.Error("Необработанное исключение (домен): " + args.ExceptionObject);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLogService.Error("Необработанное исключение задачи", args.Exception);
            args.SetObserved();
        };

        _singleInstance.StartActivationListener(() =>
        {
            Dispatcher.BeginInvoke(() => HappAccessible.MainWindow.ActivateExistingInstance(), DispatcherPriority.Normal);
        });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogService.Error("Необработанное исключение UI", e.Exception);
        e.Handled = true;
        try
        {
            System.Windows.MessageBox.Show(
                "Произошла ошибка: " + e.Exception.Message,
                "Happ Accessible",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        catch
        {
            // ignore secondary UI failure
        }
    }
}
