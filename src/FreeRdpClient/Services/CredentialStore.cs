using System.Runtime.InteropServices;
using System.Text;
using FreeRdpClient.Interop;
using FreeRdpClient.Models;

namespace FreeRdpClient.Services;

/// <summary>Stores passwords in the Windows Credential Manager (generic credentials).</summary>
public static unsafe class CredentialStore
{
    private static string TargetFor(ConnectionProfile profile) =>
        $"FreeRdpClient/{profile.Host.ToLowerInvariant()}:{profile.Port}/{(profile.UserName ?? "").ToLowerInvariant()}";

    public static string? Read(ConnectionProfile profile)
    {
        if (!Win32.CredReadW(TargetFor(profile), Win32.CRED_TYPE_GENERIC, 0, out var cred))
            return null;
        try
        {
            if (cred->CredentialBlob == null || cred->CredentialBlobSize == 0)
                return null;
            return Encoding.Unicode.GetString(cred->CredentialBlob, (int)cred->CredentialBlobSize);
        }
        finally
        {
            Win32.CredFree(cred);
        }
    }

    public static void Write(ConnectionProfile profile, string password)
    {
        var target = TargetFor(profile);
        var blob = Encoding.Unicode.GetBytes(password);
        try
        {
            fixed (char* pTarget = target)
            fixed (char* pUser = profile.UserName ?? "")
            fixed (byte* pBlob = blob)
            {
                var cred = new Win32.CREDENTIALW
                {
                    Type = Win32.CRED_TYPE_GENERIC,
                    TargetName = pTarget,
                    UserName = pUser,
                    CredentialBlob = pBlob,
                    CredentialBlobSize = (uint)blob.Length,
                    Persist = Win32.CRED_PERSIST_LOCAL_MACHINE,
                };
                if (!Win32.CredWriteW(&cred, 0))
                    System.Diagnostics.Debug.WriteLine($"CredWrite failed: {Marshal.GetLastPInvokeError()}");
            }
        }
        finally
        {
            Array.Clear(blob);
        }
    }

    public static void Delete(ConnectionProfile profile) =>
        Win32.CredDeleteW(TargetFor(profile), Win32.CRED_TYPE_GENERIC, 0);
}
