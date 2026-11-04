using System.Reflection;
using System.Runtime.InteropServices;

namespace UI.Services;

public static class DriveEjectService
{
    public static void EjectVolume(string volumePath)
    {
        var root = Path.GetPathRoot(volumePath);
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("The selected drive does not have a mounted volume.", nameof(volumePath));

        var shellType = Type.GetTypeFromProgID("Shell.Application")
            ?? throw new InvalidOperationException("Windows Explorer shell automation is unavailable.");
        object? shell = null;
        object? folder = null;
        object? item = null;
        object? verbs = null;
        try
        {
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException("Could not start Windows Explorer shell automation.");
            folder = shellType.InvokeMember("NameSpace", BindingFlags.InvokeMethod, null, shell, [root]);
            if (folder is null)
                throw new InvalidOperationException($"Windows Explorer could not access {root}.");

            var folderType = folder.GetType();
            item = folderType.InvokeMember("Self", BindingFlags.GetProperty, null, folder, null)
                ?? throw new InvalidOperationException($"Windows Explorer could not find the device at {root}.");
            verbs = item.GetType().InvokeMember("Verbs", BindingFlags.InvokeMethod, null, item, null)
                ?? throw new InvalidOperationException($"Windows Explorer did not provide eject actions for {root}.");

            var count = (int)verbs.GetType().InvokeMember("Count", BindingFlags.GetProperty, null, verbs, null)!;
            for (var index = 0; index < count; index++)
            {
                var verb = verbs.GetType().InvokeMember("Item", BindingFlags.InvokeMethod, null, verbs, [index]);
                if (verb is null)
                    continue;
                var name = Convert.ToString(verb.GetType().InvokeMember("Name", BindingFlags.GetProperty, null, verb, null));
                if (!string.Equals(name?.Replace("&", "").Trim(), "Eject", StringComparison.OrdinalIgnoreCase))
                    continue;
                verb.GetType().InvokeMember("DoIt", BindingFlags.InvokeMethod, null, verb, null);
                return;
            }

            throw new InvalidOperationException($"Windows does not offer an eject action for {root}.");
        }
        finally
        {
            Release(verbs);
            Release(item);
            Release(folder);
            Release(shell);
        }
    }

    private static void Release(object? instance)
    {
        if (instance is not null && Marshal.IsComObject(instance))
            Marshal.ReleaseComObject(instance);
    }
}
