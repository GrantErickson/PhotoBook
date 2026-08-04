using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Identity.Client;

namespace PhotoBook.Ingestion.OneDrive;

/// <summary>
/// The MSAL token cache, serialized to <c>%LOCALAPPDATA%\PhotoBook\msal.cache</c> and
/// <b>encrypted at rest with DPAPI (current user)</b> — kernel §5 and doc 05: tokens never enter the
/// project folder and never sit on disk in the clear.
/// <para>
/// DPAPI is called directly through <c>crypt32.dll</c> rather than through a package so the encrypted
/// cache has no dependency beyond MSAL itself. Writes are atomic (temp file plus rename) like every
/// other file PhotoBook writes, so a crash mid-write can never leave a half-cache that fails to
/// deserialize and forces a surprise sign-in.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiTokenCacheStorage
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("PhotoBook.OneDrive.TokenCache.v1");

    private readonly Lock _gate = new();

    /// <summary>Creates cache storage at a path.</summary>
    /// <param name="cacheFilePath">Where the encrypted cache lives; null uses the documented default.</param>
    public DpapiTokenCacheStorage(string? cacheFilePath = null)
    {
        CacheFilePath = string.IsNullOrWhiteSpace(cacheFilePath)
            ? OneDriveConfigurationLoader.DefaultTokenCachePath
            : Path.GetFullPath(cacheFilePath);
    }

    /// <summary>The absolute path of the encrypted cache file.</summary>
    public string CacheFilePath { get; }

    /// <summary>True when a cache file exists — i.e. someone has signed in on this machine before.</summary>
    public bool Exists => File.Exists(CacheFilePath);

    /// <summary>Wires this storage into an MSAL application's user token cache.</summary>
    /// <param name="cache">The application's <see cref="IPublicClientApplication.UserTokenCache"/>.</param>
    public void Attach(ITokenCache cache)
    {
        ArgumentNullException.ThrowIfNull(cache);

        cache.SetBeforeAccess(args =>
        {
            lock (_gate)
            {
                var data = Read();
                if (data.Length > 0) args.TokenCache.DeserializeMsalV3(data, shouldClearExistingCache: true);
            }
        });

        cache.SetAfterAccess(args =>
        {
            if (!args.HasStateChanged) return;
            lock (_gate) Write(args.TokenCache.SerializeMsalV3());
        });
    }

    /// <summary>Deletes the cache — the sign-out step. The project keeps working; originals are local.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(CacheFilePath)) File.Delete(CacheFilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Best effort: a locked cache file is overwritten on the next successful sign-in.
            }
        }
    }

    private byte[] Read()
    {
        try
        {
            if (!File.Exists(CacheFilePath)) return [];
            var encrypted = File.ReadAllBytes(CacheFilePath);
            return encrypted.Length == 0 ? [] : Unprotect(encrypted);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicFailure)
        {
            // A cache written by another user or a corrupted file simply means "sign in again".
            return [];
        }
    }

    private void Write(byte[] data)
    {
        try
        {
            var folder = Path.GetDirectoryName(CacheFilePath);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

            if (data.Length == 0)
            {
                if (File.Exists(CacheFilePath)) File.Delete(CacheFilePath);
                return;
            }

            var encrypted = Protect(data);
            var temp = CacheFilePath + ".tmp";
            File.WriteAllBytes(temp, encrypted);
            File.Move(temp, CacheFilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicFailure)
        {
            // Failing to persist the cache costs a re-sign-in, never a crash mid-sync.
        }
    }

    // ------------------------------------------------------------------ DPAPI

    private const int CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int CbData;
        public IntPtr PbData;
    }

    // Classic DllImport rather than LibraryImport: the generated marshalling stubs require
    // AllowUnsafeBlocks, and three blittable-signature calls are not worth turning unsafe code on for
    // the whole project.
    [DllImport("crypt32.dll", EntryPoint = "CryptProtectData", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags,
        out DataBlob dataOut);

    [DllImport("crypt32.dll", EntryPoint = "CryptUnprotectData", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags,
        out DataBlob dataOut);

    [DllImport("kernel32.dll", EntryPoint = "LocalFree", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr handle);

    private static byte[] Protect(byte[] plaintext) => Transform(plaintext, protect: true);

    private static byte[] Unprotect(byte[] ciphertext) => Transform(ciphertext, protect: false);

    private static byte[] Transform(byte[] input, bool protect)
    {
        if (!OperatingSystem.IsWindows())
            throw new CryptographicFailure("The encrypted OneDrive token cache requires Windows (DPAPI).");

        var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        var output = new DataBlob();
        try
        {
            var inBlob = new DataBlob { CbData = input.Length, PbData = inputHandle.AddrOfPinnedObject() };
            var entropyBlob = new DataBlob { CbData = Entropy.Length, PbData = entropyHandle.AddrOfPinnedObject() };

            var ok = protect
                ? CryptProtectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out output)
                : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out output);

            if (!ok)
            {
                throw new CryptographicFailure(
                    $"DPAPI {(protect ? "encryption" : "decryption")} of the OneDrive token cache failed " +
                    $"(error {Marshal.GetLastWin32Error()}).");
            }

            var result = new byte[output.CbData];
            Marshal.Copy(output.PbData, result, 0, output.CbData);
            return result;
        }
        finally
        {
            if (output.PbData != IntPtr.Zero) LocalFree(output.PbData);
            if (inputHandle.IsAllocated) inputHandle.Free();
            if (entropyHandle.IsAllocated) entropyHandle.Free();
        }
    }
}

/// <summary>The token cache could not be encrypted or decrypted; the caller degrades to a fresh sign-in.</summary>
public sealed class CryptographicFailure : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">What failed.</param>
    public CryptographicFailure(string message) : base(message)
    {
    }
}
