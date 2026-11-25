using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using FluentFTP;
using FluentFTP.Exceptions;
using Renci.SshNet;
using Renci.SshNet.Common;
using UI.Models;

namespace UI.Services;

public sealed record RemoteLocation(string Scheme, string Host, int Port, string Path)
{
    public bool IsFtp => Scheme.Equals("ftp", StringComparison.OrdinalIgnoreCase);
}

public sealed class RemoteAuthenticationException(string message, Exception? innerException = null)
    : IOException(message, innerException);

public sealed class RemoteHostKeyException(string message, string? fingerprint, bool keyChanged)
    : IOException(message)
{
    public string? Fingerprint { get; } = fingerprint;
    public bool KeyChanged { get; } = keyChanged;
}

public static class RemoteFileSystemService
{
    private sealed record StoredSecret(string UserName, string Password, bool Persist);

    private static readonly ConcurrentDictionary<string, StoredSecret> SessionSecrets = new(StringComparer.OrdinalIgnoreCase);

    public static bool IsRemotePath(string? path) => TryParse(path, out _);

    public static string NormalizeAddress(string path)
    {
        var trimmed = path.Trim();
        foreach (var scheme in new[] { "ftp", "sftp" })
        {
            if (!trimmed.StartsWith(scheme + ":", StringComparison.OrdinalIgnoreCase))
                continue;

            var remainder = trimmed[(scheme.Length + 1)..].TrimStart('\\', '/').Replace('\\', '/');
            return $"{scheme}://{remainder}";
        }

        return trimmed;
    }

    public static bool TryParse(string? path, out RemoteLocation location)
    {
        location = null!;
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var normalized = NormalizeAddress(path);
        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "ftp" && uri.Scheme != "sftp") ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.Query.Length != 0 ||
            uri.Fragment.Length != 0)
            return false;

        var defaultPort = uri.Scheme == "ftp" ? 21 : 22;
        var remotePath = Uri.UnescapeDataString(uri.AbsolutePath);
        if (string.IsNullOrEmpty(remotePath))
            remotePath = "/";
        location = new RemoteLocation(uri.Scheme, uri.Host, uri.IsDefaultPort ? defaultPort : uri.Port, remotePath);
        return true;
    }

    public static string CombinePath(string path, string name)
    {
        if (!TryParse(path, out var location))
            return Path.Combine(path, name);

        var child = name.Replace('\\', '/').Trim('/');
        var parent = location.Path.TrimEnd('/');
        return BuildUrl(location, $"{parent}/{child}");
    }

    public static string GetRootPath(string path)
    {
        var location = ParseRequired(path);
        return BuildUrl(location, "/");
    }

    public static bool Exists(string path)
    {
        var location = ParseRequired(path);
        return location.IsFtp
            ? UseFtp(location, client => client.GetObjectInfo(location.Path, true) is not null)
            : UseSftp(location, client => client.Exists(location.Path));
    }

    public static bool IsDirectory(string path)
    {
        var location = ParseRequired(path);
        return location.IsFtp
            ? UseFtp(location, client =>
                client.GetObjectInfo(location.Path, true)?.Type == FtpObjectType.Directory)
            : UseSftp(location, client =>
            {
                var attributes = client.GetAttributes(location.Path);
                return attributes.IsDirectory && !attributes.IsSymbolicLink;
            });
    }

    public static string GetParent(string path)
    {
        if (!TryParse(path, out var location))
            return path;
        var trimmed = location.Path.TrimEnd('/');
        if (trimmed.Length == 0)
            return BuildUrl(location, "/");
        var separator = trimmed.LastIndexOf('/');
        return BuildUrl(location, separator <= 0 ? "/" : trimmed[..separator]);
    }

    public static string GetName(string path)
    {
        if (!TryParse(path, out var location))
            return Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var trimmed = location.Path.TrimEnd('/');
        return trimmed.Length == 0 ? location.Host : trimmed[(trimmed.LastIndexOf('/') + 1)..];
    }

    public static void SetCredentials(string path, string userName, string password, bool persist)
    {
        if (!TryParse(path, out var location))
            throw new ArgumentException("The path is not a supported FTP or SFTP location.", nameof(path));
        SessionSecrets[GetProfileKey(location)] = new StoredSecret(userName, password, persist);
    }

    public static void TrustHostKey(string path, string fingerprint)
    {
        if (!TryParse(path, out var location) || location.IsFtp)
            throw new ArgumentException("A trusted host key can only be saved for an SFTP location.", nameof(path));
        RemoteCredentialStore.Write(GetCredentialTarget(GetHostKeyTarget(location)), "", fingerprint);
    }

    public static List<ExplorerEntry> GetEntries(string path)
    {
        if (!TryParse(path, out var location))
            throw new ArgumentException("The path is not a supported FTP or SFTP location.", nameof(path));

        return location.IsFtp
            ? UseFtp(location, client => client.GetListing(location.Path)
                .Where(item => !string.IsNullOrEmpty(item.Name) && item.Name is not "." and not "..")
                .Select(item => CreateEntry(location, item.Name, item.Type == FtpObjectType.Directory,
                    item.Size, item.Modified))
                .ToList())
            : UseSftp(location, client => client.ListDirectory(location.Path)
                .Where(item => item.Name is not "." and not "..")
                .Select(item => CreateEntry(location, item.Name, item.IsDirectory && !item.IsSymbolicLink,
                    item.IsDirectory ? 0 : item.Length, item.LastWriteTime))
                .ToList());
    }

    public static void CreateDirectory(string path)
    {
        var location = ParseRequired(path);
        if (location.IsFtp)
            UseFtp(location, client => { client.CreateDirectory(location.Path, true); return true; });
        else
            UseSftp(location, client => { client.CreateDirectory(location.Path); return true; });
    }

    public static void WriteFile(string path, Stream contents)
    {
        var location = ParseRequired(path);
        if (location.IsFtp)
        {
            UseFtp(location, client =>
            {
                using var temporaryFile = new TemporaryFile();
                using (var output = new FileStream(
                           temporaryFile.Path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    contents.CopyTo(output);
                var status = client.UploadFile(temporaryFile.Path, location.Path, FtpRemoteExists.Skip);
                if (status != FtpStatus.Success)
                    throw new IOException($"FTP could not create {path} without replacing an existing file.");
                return true;
            });
        }
        else
        {
            UseSftp(location, client =>
            {
                using var output = client.Open(location.Path, FileMode.CreateNew, FileAccess.Write);
                contents.CopyTo(output);
                return true;
            });
        }
    }

    public static void DownloadFile(string remotePath, string localPath)
    {
        var location = ParseRequired(remotePath);
        using var temporaryFile = new TemporaryFile(
            Path.GetDirectoryName(Path.GetFullPath(localPath)));
        if (location.IsFtp)
        {
            var status = UseFtp(location, client =>
                client.DownloadFile(temporaryFile.Path, location.Path, FtpLocalExists.Skip));
            if (status != FtpStatus.Success)
                throw new IOException($"FTP download failed for {remotePath}.");
        }
        else
        {
            UseSftp(location, client =>
            {
                using var output = new FileStream(
                    temporaryFile.Path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                client.DownloadFile(location.Path, output);
                return true;
            });
        }
        File.Move(temporaryFile.Path, localPath);
    }

    public static void UploadFile(string localPath, string remotePath)
    {
        var location = ParseRequired(remotePath);
        if (location.IsFtp)
        {
            var status = UseFtp(location, client =>
                client.UploadFile(localPath, location.Path, FtpRemoteExists.Skip));
            if (status != FtpStatus.Success)
                throw new IOException($"FTP upload failed for {remotePath}.");
        }
        else
        {
            UseSftp(location, client =>
            {
                using var input = File.OpenRead(localPath);
                using var output = client.Open(location.Path, FileMode.CreateNew, FileAccess.Write);
                input.CopyTo(output);
                return true;
            });
        }
    }

    public static void Rename(string path, string newPath)
    {
        var location = ParseRequired(path);
        var destination = ParseRequired(newPath);
        if (GetProfileKey(location) != GetProfileKey(destination))
            throw new InvalidOperationException("An item can only be renamed within the same remote server.");
        if (Exists(newPath))
            throw new IOException($"An item already exists at {newPath}.");
        if (location.IsFtp)
            UseFtp(location, client => { client.Rename(location.Path, destination.Path); return true; });
        else
            UseSftp(location, client => { client.RenameFile(location.Path, destination.Path); return true; });
    }

    public static void Delete(string path, bool isDirectory)
    {
        var location = ParseRequired(path);
        if (location.IsFtp)
        {
            UseFtp(location, client =>
            {
                if (isDirectory)
                    DeleteFtpDirectory(client, location.Path);
                else
                    client.DeleteFile(location.Path);
                return true;
            });
        }
        else
        {
            UseSftp(location, client =>
            {
                if (isDirectory)
                    DeleteSftpDirectory(client, location.Path);
                else
                    client.DeleteFile(location.Path);
                return true;
            });
        }
    }

    public static void CopyRemoteEntry(string source, string destinationDirectory, bool isDirectory)
    {
        var target = CombinePath(destinationDirectory, GetName(source));
        if (isDirectory)
        {
            CreateDirectory(target);
            foreach (var child in GetEntries(source))
                CopyRemoteEntry(child.FullPath, target, child.IsDirectory);
            return;
        }

        using var temporaryFile = new TemporaryFile();
        DownloadFile(source, temporaryFile.Path);
        UploadFile(temporaryFile.Path, target);
    }

    public static void CopyLocalEntryToRemote(string source, string destinationDirectory)
    {
        var target = CombinePath(destinationDirectory, Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar)));
        if (Directory.Exists(source))
        {
            CreateDirectory(target);
            foreach (var directory in Directory.EnumerateDirectories(source))
            {
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) == 0)
                    CopyLocalEntryToRemote(directory, target);
            }
            foreach (var file in Directory.EnumerateFiles(source))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                    CopyLocalEntryToRemote(file, target);
            }
        }
        else
        {
            UploadFile(source, target);
        }
    }

    public static void CopyRemoteEntryToLocal(string source, string destinationDirectory, bool isDirectory)
    {
        var target = Path.Combine(destinationDirectory, GetName(source));
        if (isDirectory)
        {
            Directory.CreateDirectory(target);
            foreach (var child in GetEntries(source))
                CopyRemoteEntryToLocal(child.FullPath, target, child.IsDirectory);
        }
        else
        {
            DownloadFile(source, target);
        }
    }

    public static void CopyLocalEntryToLocal(string source, string destinationDirectory)
    {
        var target = Path.Combine(destinationDirectory, Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar)));
        if (Directory.Exists(source))
            FileSystemService.CopyDirectory(source, target);
        else
            File.Copy(source, target, overwrite: false);
    }

    private static T UseFtp<T>(RemoteLocation location, Func<FtpClient, T> operation)
    {
        var profileKey = GetProfileKey(location);
        var secret = GetSessionOrSavedSecret(profileKey);
        var anonymousAttempt = secret is null;
        secret ??= new StoredSecret("anonymous", "anonymous@", false);

        using var client = new FtpClient(location.Host, secret.UserName, secret.Password, location.Port);
        try
        {
            client.Connect();
            var result = operation(client);
            PersistPendingSecret(profileKey, secret);
            return result;
        }
        catch (FtpAuthenticationException exception)
        {
            throw new RemoteAuthenticationException(
                anonymousAttempt
                    ? $"The FTP server at {location.Host} rejected anonymous access. Enter an account to continue."
                    : $"The FTP server at {location.Host} rejected the saved credentials.",
                exception);
        }
    }

    private static T UseSftp<T>(RemoteLocation location, Func<SftpClient, T> operation)
    {
        var profileKey = GetProfileKey(location);
        var secret = GetSessionOrSavedSecret(profileKey);
        if (secret is null)
            throw new RemoteAuthenticationException(
                $"Enter an account for the SFTP server at {location.Host} to continue.");

        var expectedHostKey = RemoteCredentialStore.Read(
            GetCredentialTarget(GetHostKeyTarget(location)))?.Password;
        string? actualHostKey = null;
        using var client = new SftpClient(location.Host, location.Port, secret.UserName, secret.Password);
        client.HostKeyReceived += (_, args) =>
        {
            actualHostKey = args.FingerPrintSHA256;
            args.CanTrust = string.Equals(actualHostKey, expectedHostKey, StringComparison.Ordinal);
        };

        try
        {
            client.Connect();
        }
        catch (SshAuthenticationException exception)
        {
            throw new RemoteAuthenticationException(
                $"The SFTP server at {location.Host} rejected the saved credentials.",
                exception);
        }
        catch (SshConnectionException) when (actualHostKey is not null)
        {
            if (expectedHostKey is null)
            {
                throw new RemoteHostKeyException(
                    $"Verify the identity of the SFTP server at {location.Host} before connecting.",
                    actualHostKey,
                    keyChanged: false);
            }

            throw new RemoteHostKeyException(
                $"The SFTP host key for {location.Host} has changed. Do not continue unless you can verify the new key.",
                actualHostKey,
                keyChanged: true);
        }

        var result = operation(client);
        PersistPendingSecret(profileKey, secret);
        return result;
    }

    private static void PersistPendingSecret(string profileKey, StoredSecret secret)
    {
        if (secret.Persist)
        {
            RemoteCredentialStore.Write(GetCredentialTarget(profileKey), secret.UserName, secret.Password);
            SessionSecrets[profileKey] = secret with { Persist = false };
        }
    }

    private static StoredSecret? GetSessionOrSavedSecret(string profileKey)
    {
        if (SessionSecrets.TryGetValue(profileKey, out var sessionSecret))
            return sessionSecret;
        var saved = RemoteCredentialStore.Read(GetCredentialTarget(profileKey));
        if (saved is null)
            return null;
        if (string.IsNullOrWhiteSpace(saved.UserName) || string.IsNullOrEmpty(saved.Password))
            throw new InvalidDataException("The saved remote-server credential is incomplete.");
        return new StoredSecret(saved.UserName, saved.Password, false);
    }

    private static void DeleteFtpDirectory(FtpClient client, string path)
    {
        foreach (var entry in client.GetListing(path))
        {
            if (entry.Name is "." or "..")
                continue;
            if (entry.Type == FtpObjectType.Directory)
                DeleteFtpDirectory(client, entry.FullName);
            else
                client.DeleteFile(entry.FullName);
        }
        client.DeleteDirectory(path);
    }

    private static void DeleteSftpDirectory(SftpClient client, string path)
    {
        foreach (var entry in client.ListDirectory(path))
        {
            if (entry.Name is "." or "..")
                continue;
            if (entry.IsDirectory && !entry.IsSymbolicLink)
                DeleteSftpDirectory(client, entry.FullName);
            else
                client.DeleteFile(entry.FullName);
        }
        client.DeleteDirectory(path);
    }

    private static ExplorerEntry CreateEntry(
        RemoteLocation location,
        string name,
        bool isDirectory,
        long size,
        DateTime modified)
    {
        return new ExplorerEntry
        {
            Name = name,
            FullPath = CombinePath(BuildUrl(location, location.Path), name),
            Kind = isDirectory ? ExplorerEntryKind.Directory : ExplorerEntryKind.File,
            Type = isDirectory
                ? "Folder"
                : string.IsNullOrEmpty(Path.GetExtension(name))
                    ? "File"
                    : $"{Path.GetExtension(name).TrimStart('.').ToUpperInvariant()} file",
            Size = isDirectory ? "" : FileSystemService.FormatSize(size),
            SizeBytes = isDirectory ? 0 : size,
            Modified = modified.ToString("g", CultureInfo.CurrentCulture),
            ModifiedTime = modified,
            IsModel = !isDirectory && PreviewFormatRegistry.IsModel(name)
        };
    }

    private static string BuildUrl(RemoteLocation location, string remotePath)
    {
        var escapedPath = string.Join("/", remotePath.Split('/').Select(Uri.EscapeDataString));
        return new UriBuilder(location.Scheme, location.Host, location.Port, escapedPath).Uri.AbsoluteUri;
    }

    private static RemoteLocation ParseRequired(string path) =>
        TryParse(path, out var location)
            ? location
            : throw new ArgumentException("The path is not a supported FTP or SFTP location.", nameof(path));

    private static string GetProfileKey(RemoteLocation location) =>
        $"ExplorerPlusPlus:remote:{location.Scheme}:{location.Host.ToLowerInvariant()}:{location.Port}";

    private static string GetHostKeyTarget(RemoteLocation location) =>
        $"{GetProfileKey(location)}:hostkey";

    private static string GetCredentialTarget(string key) =>
        $"ExplorerPlusPlus:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))}";

    private sealed class TemporaryFile : IDisposable
    {
        public string Path { get; }

        public TemporaryFile(string? directory = null)
        {
            Path = System.IO.Path.Combine(
                directory ?? System.IO.Path.GetTempPath(),
                $"ExplorerPlusPlus-{Guid.NewGuid():N}.tmp");
        }

        public void Dispose()
        {
            if (File.Exists(Path))
                File.Delete(Path);
        }
    }
}

internal static class RemoteCredentialStore
{
    private const uint GenericCredentialType = 1;
    private const uint LocalMachinePersistence = 2;

    public sealed record Secret(string UserName, string Password);

    public static Secret? Read(string target)
    {
        if (!CredRead(target, GenericCredentialType, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 1168)
                return null;
            throw new Win32Exception(error, "Windows Credential Manager could not read the saved remote-server credential.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlobSize > 5120 ||
                credential.CredentialBlobSize % sizeof(char) != 0 ||
                (credential.CredentialBlobSize > 0 && credential.CredentialBlob == IntPtr.Zero))
                throw new InvalidDataException("A saved remote-server credential contains invalid data.");
            var password = credential.CredentialBlob == IntPtr.Zero
                ? ""
                : Marshal.PtrToStringUni(
                    credential.CredentialBlob,
                    checked((int)(credential.CredentialBlobSize / sizeof(char)))) ?? "";
            return new Secret(credential.UserName == IntPtr.Zero ? "" : Marshal.PtrToStringUni(credential.UserName) ?? "", password);
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public static void Write(string target, string userName, string password)
    {
        var userNamePointer = Marshal.StringToCoTaskMemUni(userName);
        var passwordPointer = Marshal.StringToCoTaskMemUni(password);
        try
        {
            var credential = new NativeCredential
            {
                Type = GenericCredentialType,
                TargetName = Marshal.StringToCoTaskMemUni(target),
                UserName = userNamePointer,
                CredentialBlob = passwordPointer,
                CredentialBlobSize = checked((uint)(password.Length * sizeof(char))),
                Persist = LocalMachinePersistence
            };
            try
            {
                if (!CredWrite(ref credential, 0))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows Credential Manager could not save the remote-server credential.");
            }
            finally
            {
                Marshal.FreeCoTaskMem(credential.TargetName);
            }
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(passwordPointer);
            Marshal.ZeroFreeCoTaskMemUnicode(userNamePointer);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern void CredFree(IntPtr credential);
}
