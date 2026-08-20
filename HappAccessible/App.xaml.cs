using System.Windows;
using System.Windows.Threading;
using HappAccessible.Services;

namespace HappAccessible;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppLogService.EnsureLogFile();
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
    }

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLogService.Error("Необработанное исключение UI", e.Exception);
        // keep default crash behavior unless we mark handled
    }
}
