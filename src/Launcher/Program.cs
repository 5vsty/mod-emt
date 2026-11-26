using FSDEV.Common.Logger;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.ServiceProcess;

namespace FSDEV.MOD.Emt.Launcher;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var shutdown = new TaskCompletionSource();

        Console.CancelKeyPress += (sender, e) =>
        {
            Logging.Log(LogLevel.Info, "Strg+C empfangen...");
            e.Cancel = true;
            _ = shutdown.TrySetResult();
        };

        AssemblyLoadContext.Default.Unloading += ctx =>
        {
            Logging.Log(LogLevel.Info, "SIGTERM empfangen...");
            _ = shutdown.TrySetResult();
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var service = new WindowsServiceControl(shutdown);
            if (Environment.UserInteractive || Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true")
            {
                service.RunAsConsole(args);
                await shutdown.Task;
                service.StopInConsole();
            }
            else
            {
                ServiceBase.Run(service);
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            var service = new LinuxServiceControl(shutdown);
            service.Run();
            await shutdown.Task;
            service.Stop();
        }
        else
        {
            Logging.Log(LogLevel.Error, "Unsupported OS platform.");
        }
    }
}