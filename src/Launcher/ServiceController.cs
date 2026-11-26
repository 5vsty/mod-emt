using FSDEV.Common.Logger;
using FSDEV.MOD.Emt.Emt_Server;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security;
using System.ServiceProcess;
using Constants = FSDEV.MOD.Emt.Emt.Constants;
using EventLog = System.Diagnostics.EventLog;

namespace FSDEV.MOD.Emt.Launcher;

[SupportedOSPlatform("windows")]
public class WindowsServiceControl : ServiceBase
{
    private readonly TaskCompletionSource Shutdown;
    private readonly CancellationTokenSource StoppingTokenSource = new();

    public WindowsServiceControl(TaskCompletionSource shutdown)
    {
        ServiceName = Constants.Service.APP_NAME;
        Shutdown = shutdown;
    }

    ~WindowsServiceControl()
    {
        StoppingTokenSource.Cancel();
        StoppingTokenSource.Dispose();
    }

    protected override void OnStart(string[] args)
    {
        Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);

        if (!TryInitializeEventLogging())
        {
            _ = Console.ReadLine();
            return;
        }

        // Log unhandled exceptions with full stack trace, code line etc
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        EventLog.WriteEntry(Constants.Service.APP_NAME, $"{Constants.Service.APP_NAME} service starting");
        Logging.Log(LogLevel.Info, $"{Constants.Service.APP_NAME} service starting");

        var initTask = EmtServer.Initialize(StoppingTokenSource.Token);
        initTask.Wait();
        if (initTask.Result is null)
        {
            EventLog.WriteEntry(Constants.Service.APP_NAME, $"There was an error initializing {Constants.Service.APP_NAME}!");
            Logging.Log(LogLevel.Info, $"There was an error initializing {Constants.Service.APP_NAME}!");
            Environment.Exit(0);
            return;
        }

        initTask.Result.StartOperating();

        EventLog.WriteEntry(Constants.Service.APP_NAME, $"{Constants.Service.APP_NAME} service started");
        Logging.Log(LogLevel.Info, $"{Constants.Service.APP_NAME} service started");
    }

    protected override void OnStop()
    {
        StoppingTokenSource.Cancel();
        EventLog.WriteEntry(Constants.Service.APP_NAME, $"{Constants.Service.APP_NAME} service stopped");
        Logging.Log(LogLevel.Info, $"{Constants.Service.APP_NAME} service stopped");
    }

    public void RunAsConsole(string[] args) => OnStart(args);

    public void StopInConsole() => OnStop();

    private static bool TryInitializeEventLogging()
    {
        try
        {
            if (!EventLog.SourceExists(Constants.Service.APP_NAME))
            {
                EventLog.CreateEventSource(Constants.Service.APP_NAME, Constants.Service.EVENT_LOG_NAME);
            }
        }
        catch (SecurityException)
        {
            Logging.Log(LogLevel.Error, $"{Constants.Service.APP_NAME} Unable to initialize EventLog. Start application with administrative privileges!");
            return false;
        }

        return true;
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        var exceptionStr = e.ExceptionObject.ToString();
        EventLog.WriteEntry(Constants.Service.APP_NAME, exceptionStr);
        Logging.Log(LogLevel.Error, e.ExceptionObject as Exception ?? new RuntimeWrappedException(e.ExceptionObject), $"{Constants.Service.APP_NAME} unhandled exception");
        _ = Shutdown.TrySetResult();
    }
}

[SupportedOSPlatform("linux")]
public class LinuxServiceControl(TaskCompletionSource shutdown)
{
    private readonly TaskCompletionSource Shutdown = shutdown;
    private readonly CancellationTokenSource StoppingTokenSource = new();

    ~LinuxServiceControl()
    {
        StoppingTokenSource.Cancel();
        StoppingTokenSource.Dispose();
    }

    public void Run()
    {
        Directory.SetCurrentDirectory(AppDomain.CurrentDomain.BaseDirectory);

        // Log unhandled exceptions with full stack trace, code line etc
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

        Logging.Log(LogLevel.Info, $"{Constants.Service.APP_NAME} service starting");

        var initTask = EmtServer.Initialize(StoppingTokenSource.Token);
        initTask.Wait();
        if (initTask.Result is null)
        {
            Logging.Log(LogLevel.Fatal, $"{Constants.Service.APP_NAME} There was an error initializing this Application!");
            Environment.Exit(0);
            return;
        }

        initTask.Result.StartOperating();

        Logging.Log(LogLevel.Info, $"{Constants.Service.APP_NAME} service started");
    }

    public void Stop()
    {
        StoppingTokenSource.Cancel();
        Logging.Log(LogLevel.Info, $"{Constants.Service.APP_NAME} service stopped");
    }

    private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        Logging.Log(LogLevel.Error, e.ExceptionObject as Exception ?? new RuntimeWrappedException(e.ExceptionObject), $"{Constants.Service.APP_NAME} unhandled exception");
        _ = Shutdown.TrySetResult();
    }
}