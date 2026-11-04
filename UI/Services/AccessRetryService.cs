using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32.SafeHandles;

namespace UI.Services;

public static class AccessRetryService
{
    private const int Logon32LogonInteractive = 2;
    private const int Logon32LogonNewCredentials = 9;
    private const int Logon32ProviderDefault = 0;
    private const int Logon32ProviderWinnt50 = 3;

    public static async Task<bool> RunActionAsync(XamlRoot xamlRoot, string path, Action operation)
    {
        var result = await RunAsync(xamlRoot, path, () =>
        {
            operation();
            return AccessCompleted.Instance;
        });
        return result is not null;
    }

    public static async Task<T?> RunAsync<T>(XamlRoot xamlRoot, string path, Func<T> operation)
        where T : class
    {
        try
        {
            return await Task.Run(operation);
        }
        catch (UnauthorizedAccessException)
        {
            var choice = await AskHowToContinueAsync(xamlRoot, path);
            if (choice == AccessChoice.Retry)
                return await Task.Run(operation);
            if (choice == AccessChoice.Administrator)
            {
                if (!App.RestartAsAdministrator(path))
                    await ShowMessageAsync(xamlRoot, "Unable to gain access", App.StartupNotice ?? "The elevated application could not be started.");
                return null;
            }
            if (choice != AccessChoice.Credentials)
                return null;
        }

        var credentials = await AskForCredentialsAsync(xamlRoot, path);
        if (credentials is null)
            return null;

        try
        {
            using var token = Logon(credentials.UserName, credentials.Password);
            credentials.ClearPassword();
            return await Task.Run(() => WindowsIdentity.RunImpersonated(token, operation));
        }
        catch (Exception exception) when (exception is Win32Exception or UnauthorizedAccessException or IOException)
        {
            credentials.ClearPassword();
            await ShowMessageAsync(xamlRoot, "Access unavailable", $"Windows could not access \"{path}\" using those credentials.\n\n{exception.Message}");
            return null;
        }
    }

    private static async Task<AccessChoice> AskHowToContinueAsync(XamlRoot xamlRoot, string path)
    {
        var choice = AccessChoice.Cancel;
        var dialog = new ContentDialog
        {
            Title = "Access denied",
            CloseButtonText = "Cancel",
            XamlRoot = xamlRoot
        };
        var content = new StackPanel { Spacing = 10, Width = 430 };
        content.Children.Add(new TextBlock
        {
            Text = $"Windows denied access to:\n{path}\n\nChoose how you would like to continue.",
            TextWrapping = TextWrapping.Wrap
        });
        var retry = new Button { Content = "Retry with current permissions", HorizontalAlignment = HorizontalAlignment.Stretch };
        var administrator = new Button { Content = "Restart as administrator", HorizontalAlignment = HorizontalAlignment.Stretch };
        var credentials = new Button { Content = "Use different Windows credentials", HorizontalAlignment = HorizontalAlignment.Stretch };
        retry.Click += (_, _) => { choice = AccessChoice.Retry; dialog.Hide(); };
        administrator.Click += (_, _) => { choice = AccessChoice.Administrator; dialog.Hide(); };
        credentials.Click += (_, _) => { choice = AccessChoice.Credentials; dialog.Hide(); };
        content.Children.Add(retry);
        content.Children.Add(administrator);
        content.Children.Add(credentials);
        dialog.Content = content;
        await dialog.ShowAsync();
        return choice;
    }

    private static async Task<Credentials?> AskForCredentialsAsync(XamlRoot xamlRoot, string path)
    {
        var userName = new TextBox
        {
            Header = "User name (DOMAIN\\user or user@domain)",
            Text = $@"{Environment.UserDomainName}\{Environment.UserName}"
        };
        var password = new PasswordBox { Header = "Password" };
        var fields = new StackPanel { Spacing = 12 };
        fields.Children.Add(new TextBlock
        {
            Text = $"Credentials will be used only to retry access to:\n{path}\nThey are not saved by this application.",
            TextWrapping = TextWrapping.Wrap
        });
        fields.Children.Add(userName);
        fields.Children.Add(password);

        var dialog = new ContentDialog
        {
            Title = "Use different Windows credentials",
            Content = fields,
            PrimaryButtonText = "Continue",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return null;
        if (string.IsNullOrWhiteSpace(userName.Text) || string.IsNullOrEmpty(password.Password))
        {
            await ShowMessageAsync(xamlRoot, "Credentials required", "Enter both a user name and password.");
            return null;
        }
        var enteredPassword = password.Password;
        password.Password = string.Empty;
        var credentials = new Credentials(userName.Text.Trim(), enteredPassword.ToCharArray());
        enteredPassword = string.Empty;
        return credentials;
    }

    private static SafeAccessTokenHandle Logon(string userName, char[] password)
    {
        var separator = userName.IndexOf('\\');
        var domain = separator > 0 ? userName[..separator] : null;
        var account = separator > 0 ? userName[(separator + 1)..] : userName;

        using var securePassword = new SecureString();
        foreach (var character in password)
            securePassword.AppendChar(character);
        securePassword.MakeReadOnly();
        var passwordPointer = Marshal.SecureStringToGlobalAllocUnicode(securePassword);
        try
        {
            if (LogonUser(account, domain, passwordPointer, Logon32LogonInteractive, Logon32ProviderDefault, out var token))
                return token;
            var interactiveError = Marshal.GetLastWin32Error();

            if (LogonUser(account, domain, passwordPointer, Logon32LogonNewCredentials, Logon32ProviderWinnt50, out token))
                return token;
            var networkError = Marshal.GetLastWin32Error();
            throw new Win32Exception(networkError, $"Windows logon failed (interactive error {interactiveError}; network credentials error {networkError}).");
        }
        finally
        {
            Marshal.ZeroFreeGlobalAllocUnicode(passwordPointer);
            Array.Clear(password);
        }
    }

    private static async Task ShowMessageAsync(XamlRoot xamlRoot, string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = message,
            CloseButtonText = "OK",
            XamlRoot = xamlRoot
        };
        await dialog.ShowAsync();
    }

    private enum AccessChoice
    {
        Cancel,
        Retry,
        Credentials,
        Administrator
    }

    private sealed class AccessCompleted
    {
        public static AccessCompleted Instance { get; } = new();
    }

    private sealed class Credentials(string userName, char[] password)
    {
        public string UserName { get; } = userName;
        public char[] Password { get; } = password;

        public void ClearPassword() => Array.Clear(Password);
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LogonUser(
        string userName,
        string? domain,
        IntPtr password,
        int logonType,
        int logonProvider,
        out SafeAccessTokenHandle token);
}
