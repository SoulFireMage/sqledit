using System.Runtime.InteropServices;
using System.Text;

namespace SqlShell.Core.Profiles;

/// <summary>
/// Windows Credential Manager backend that reproduces the storage contract of
/// python-keyring's <c>WinVaultKeyring</c>, so credentials written by the Python
/// sqlshell are readable here and vice versa.
/// </summary>
/// <remarks>
/// Passwords are stored under the service name unless a collision occurs, in
/// which case the previous credential is moved to the compound target
/// <c>{username}@{service}</c>. Blobs are written as UTF-16LE, the comment is
/// "Stored using python-keyring", and persistence is CRED_PERSIST_ENTERPRISE.
/// </remarks>
public sealed class WindowsCredentialStore : ICredentialStore
{
    private const uint CredTypeGeneric = 1;
    private const uint CredPersistEnterprise = 3;
    private const int ErrorNotFound = 1168;
    private const string Comment = "Stored using python-keyring";

    public WindowsCredentialStore()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("WindowsCredentialStore requires Windows");
        }
    }

    public string? GetPassword(string service, string username)
        => Resolve(service, username)?.Password;

    public void SetPassword(string service, string username, string password)
    {
        var existing = Read(service);
        if (existing is not null)
        {
            Write(CompoundName(existing.UserName, service), existing.UserName, existing.Password);
        }

        Write(service, username, password);
    }

    public void DeletePassword(string service, string username)
    {
        var compound = CompoundName(username, service);
        var deleted = false;
        foreach (var target in new[] { service, compound })
        {
            var existing = Read(target);
            if (existing is not null && existing.UserName == username)
            {
                Delete(target);
                deleted = true;
            }
        }

        if (!deleted)
        {
            throw new CredentialNotFoundException(service);
        }
    }

    private static string CompoundName(string username, string service) => $"{username}@{service}";

    private static Credential? Resolve(string service, string? username)
    {
        var result = Read(service);
        if (result is null || (username is not null && result.UserName != username))
        {
            result = Read(CompoundName(username ?? string.Empty, service));
        }

        return result;
    }

    private static Credential? Read(string target)
    {
        if (!NativeMethods.CredRead(target, CredTypeGeneric, 0, out var handle))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return null;
            }

            throw new System.ComponentModel.Win32Exception(error, $"CredRead failed for '{target}' (error {error})");
        }

        try
        {
            var native = Marshal.PtrToStructure<NativeMethods.Credential>(handle);
            var password = DecodeBlob(native.CredentialBlob, native.CredentialBlobSize);
            return new Credential(native.UserName, password);
        }
        finally
        {
            NativeMethods.CredFree(handle);
        }
    }

    private static void Write(string target, string username, string password)
    {
        var blob = Encoding.Unicode.GetBytes(password);
        var blobPointer = Marshal.AllocHGlobal(blob.Length);
        try
        {
            Marshal.Copy(blob, 0, blobPointer, blob.Length);
            var credential = new NativeMethods.Credential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                UserName = username,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = blobPointer,
                Comment = Comment,
                Persist = CredPersistEnterprise,
            };

            if (!NativeMethods.CredWrite(ref credential, 0))
            {
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(), "CredWrite failed");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(blobPointer);
        }
    }

    private static void Delete(string target)
    {
        if (NativeMethods.CredDelete(target, CredTypeGeneric, 0))
        {
            return;
        }

        var error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound)
        {
            throw new System.ComponentModel.Win32Exception(error, $"CredDelete failed for '{target}'");
        }
    }

    private static string DecodeBlob(IntPtr pointer, uint size)
    {
        if (size == 0)
        {
            return string.Empty;
        }

        var bytes = new byte[size];
        Marshal.Copy(pointer, bytes, 0, (int)size);
        try
        {
            return new UnicodeEncoding(bigEndian: false, byteOrderMark: false, throwOnInvalidBytes: true)
                .GetString(bytes);
        }
        catch (ArgumentException)
        {
            // Python-keyring warns and accepts a UTF-8 blob when UTF-16 decoding fails.
            return Encoding.UTF8.GetString(bytes);
        }
    }

    private sealed record Credential(string UserName, string Password);

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct Credential
        {
            public uint Flags;
            public uint Type;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string TargetName;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string Comment;

            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string TargetAlias;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredWrite(ref Credential credential, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredDelete(string target, uint type, uint flags);

        [DllImport("advapi32.dll", EntryPoint = "CredFree", SetLastError = true)]
        internal static extern void CredFree(IntPtr buffer);
    }
}
