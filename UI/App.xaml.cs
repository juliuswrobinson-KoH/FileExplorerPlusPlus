using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Principal;
using Microsoft.UI.Xaml;

namespace UI;

public partial class App : Application
{
    private Window? _window;
    internal static string? StartupNotice { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (TryRelaunchElevated())
            return;

        _window = new MainWindow(GetOpenPathArgument());
        _window.Activate();
    }

    private static bool TryRelaunchElevated()
    {
        if (Environment.GetCommandLineArgs().Any(argument => argument.Equals("--elevated", StringComparison.OrdinalIgnoreCase)))
            return false;

        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                return false;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or PlatformNotSupportedException)
        {
            StartupNotice = $"Could not check administrator access; continuing normally. {exception.Message}";
            return false;
        }

        try
        {
            var startInfo = CreateElevatedStartInfo();
            if (Process.Start(startInfo) is null)
                throw new InvalidOperationException("Windows did not start the elevated application.");

            Environment.Exit(0);
            return true;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            StartupNotice = "Administrator approval was declined; continuing with your normal access.";
            return false;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or UnauthorizedAccessException or ArgumentException or NotSupportedException or IOException or System.Runtime.InteropServices.COMException)
        {
            StartupNotice = $"Could not start with administrator access; continuing normally. {exception.Message}";
            return false;
        }
    }

    internal static bool RestartAsAdministrator(string path)
    {
        try
        {
            if (Process.Start(CreateElevatedStartInfo(path)) is null)
                throw new InvalidOperationException("Windows did not start the elevated application.");
            Environment.Exit(0);
            return true;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or UnauthorizedAccessException or ArgumentException or NotSupportedException or IOException or System.Runtime.InteropServices.COMException)
        {
            StartupNotice = $"Could not restart with administrator access. {exception.Message}";
            return false;
        }
    }

    private static ProcessStartInfo CreateElevatedStartInfo(string? openPath = null)
    {
        var executable = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executable))
            throw new InvalidOperationException("The application executable path is unavailable.");

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Environment.CurrentDirectory
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            var assembly = Assembly.GetEntryAssembly()?.Location;
            if (string.IsNullOrWhiteSpace(assembly))
                throw new InvalidOperationException("The application assembly path is unavailable.");
            startInfo.ArgumentList.Add(assembly);
        }
        startInfo.ArgumentList.Add("--elevated");
        if (!string.IsNullOrWhiteSpace(openPath))
        {
            startInfo.ArgumentList.Add("--open");
            startInfo.ArgumentList.Add(openPath);
        }
        return startInfo;
    }

    private static string? GetOpenPathArgument()
    {
        var arguments = Environment.GetCommandLineArgs();
        for (var index = 0; index < arguments.Length - 1; index++)
        {
            if (arguments[index].Equals("--open", StringComparison.OrdinalIgnoreCase))
                return arguments[index + 1];
        }
        return null;
    }
}
