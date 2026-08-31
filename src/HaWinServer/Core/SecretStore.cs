using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace HaWinServer.Core;

/// <summary>
/// The secrets this app keeps at rest: the Cloudflare tunnel run token (see
/// TunnelSupervisor) and the Cloudflare API token used by the setup wizard
/// and the various cleanup dialogs. Both are deliberately kept out of
/// settings.json entirely - "settings.json holds no secrets" is an existing,
/// load-bearing design rule (see Settings.cs), so each secret gets its own
/// file rather than bending that rule. The two are stored independently
/// (separate files) so deleting/rotating one never touches the other.
///
/// Protected with DPAPI (CurrentUser scope) rather than a hand-rolled cipher:
/// it is already in the Windows base class library
/// (System.Security.Cryptography.ProtectedData is not referenced to avoid a
/// NuGet package - Crypt32.dll is called directly instead), it ties the
/// secret to this Windows user profile the same way every other credential
/// Windows itself stores does, and it needs no key management of our own.
/// Losing/reinstalling the Windows profile invalidates it exactly like it
/// would invalidate saved browser passwords - acceptable, since re-running
/// the tunnel setup wizard (or pasting the API token again) is a two-minute
/// fix, not a data-loss event (no Home Assistant data lives here).
/// </summary>
public static class SecretStore
{
    private static readonly string TunnelTokenFile = Path.Combine(AppPaths.Root, "secrets.dat");
    private static readonly string ApiTokenFile = Path.Combine(AppPaths.Root, "cloudflare-api-token.dat");

    // Fixed application-specific entropy: makes the protected blob useless if
    // copied to another app's DPAPI-protected store under the same user
    // profile, without needing a second secret to manage.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("HaWinServer.SecretStore.v1");

    public static void SaveTunnelToken(string token) => Save(TunnelTokenFile, token);

    /// <summary>Null if no token has been saved, or if it can no longer be decrypted (different user profile, corrupted file).</summary>
    public static string? TryLoadTunnelToken() => TryLoad(TunnelTokenFile);

    public static void DeleteTunnelToken() => Delete(TunnelTokenFile);

    public static bool HasTunnelToken() => File.Exists(TunnelTokenFile);

    /// <summary>
    /// The API token entered into the tunnel setup wizard (or one of the
    /// cleanup dialogs) - saved so it survives an app restart and is already
    /// on hand the next time any instance's remote access/Access is set up,
    /// instead of being asked for again every session.
    /// </summary>
    public static void SaveApiToken(string token) => Save(ApiTokenFile, token);

    /// <summary>Null if no API token has been saved, or if it can no longer be decrypted (different user profile, corrupted file).</summary>
    public static string? TryLoadApiToken() => TryLoad(ApiTokenFile);

    public static void DeleteApiToken() => Delete(ApiTokenFile);

    public static bool HasApiToken() => File.Exists(ApiTokenFile);

    private static void Save(string path, string value)
    {
        AppPaths.EnsureCreated();
        var plainBytes = Encoding.UTF8.GetBytes(value);
        var protectedBytes = ProtectedDataCompat.Protect(plainBytes, Entropy);
        File.WriteAllBytes(path, protectedBytes);
    }

    private static string? TryLoad(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            var protectedBytes = File.ReadAllBytes(path);
            var plainBytes = ProtectedDataCompat.Unprotect(protectedBytes, Entropy);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void Delete(string path)
    {
        try { File.Delete(path); } catch (Exception) { /* absent, or busy - neither is fatal */ }
    }
}

/// <summary>
/// Thin P/Invoke over CryptProtectData/CryptUnprotectData (Crypt32.dll) -
/// equivalent to System.Security.Cryptography.ProtectedData, which is a
/// separate NuGet package on .NET 9 and would break this project's
/// zero-package property. CurrentUser scope (no CRYPTPROTECT_LOCAL_MACHINE
/// flag): the blob can only be unprotected by the same Windows user account
/// that wrote it, matching the rest of this app's per-user data model
/// (%LOCALAPPDATA%, HKCU).
/// </summary>
internal static class ProtectedDataCompat
{
    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public IntPtr pbData;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn, string? szDataDescr, ref DataBlob optionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn, IntPtr ppszDataDescr, ref DataBlob optionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, ref DataBlob dataOut);

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    public static byte[] Protect(byte[] plainBytes, byte[] entropy)
    {
        var inHandle = GCHandle.Alloc(plainBytes, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(entropy, GCHandleType.Pinned);
        try
        {
            var dataIn = new DataBlob { pbData = inHandle.AddrOfPinnedObject(), cbData = plainBytes.Length };
            var entropyBlob = new DataBlob { pbData = entropyHandle.AddrOfPinnedObject(), cbData = entropy.Length };
            var dataOut = new DataBlob();

            var ok = CryptProtectData(
                ref dataIn, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref dataOut);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());

            return CopyAndFree(dataOut);
        }
        finally
        {
            inHandle.Free();
            entropyHandle.Free();
        }
    }

    public static byte[] Unprotect(byte[] protectedBytes, byte[] entropy)
    {
        var inHandle = GCHandle.Alloc(protectedBytes, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(entropy, GCHandleType.Pinned);
        try
        {
            var dataIn = new DataBlob { pbData = inHandle.AddrOfPinnedObject(), cbData = protectedBytes.Length };
            var entropyBlob = new DataBlob { pbData = entropyHandle.AddrOfPinnedObject(), cbData = entropy.Length };
            var dataOut = new DataBlob();

            var ok = CryptUnprotectData(
                ref dataIn, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref dataOut);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());

            return CopyAndFree(dataOut);
        }
        finally
        {
            inHandle.Free();
            entropyHandle.Free();
        }
    }

    private static byte[] CopyAndFree(DataBlob dataOut)
    {
        try
        {
            var result = new byte[dataOut.cbData];
            Marshal.Copy(dataOut.pbData, result, 0, dataOut.cbData);
            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(dataOut.pbData);
        }
    }
}
