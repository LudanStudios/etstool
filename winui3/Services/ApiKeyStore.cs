using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace ETSAssistant.WinUI3.Services;

/// <summary>
/// Stores the DeepSeek API key with Windows DPAPI (current user), so the key
/// never lands in the configuration file or the repository in clear text.
/// </summary>
public static class ApiKeyStore
{
    private static readonly string KeyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ETS", "deepseek_api_key.bin");

    public static string FilePath => KeyPath;

    public static bool HasKey => Load().Length > 0;

    public static string Load()
    {
        try
        {
            if (!File.Exists(KeyPath)) return string.Empty;
            byte[] plain = Unprotect(File.ReadAllBytes(KeyPath));
            return Encoding.UTF8.GetString(plain).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
        {
            return string.Empty;
        }
    }

    public static void Save(string key)
    {
        key = (key ?? string.Empty).Trim();
        if (key.Length == 0)
        {
            Clear();
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(KeyPath)!);
        File.WriteAllBytes(KeyPath, Protect(Encoding.UTF8.GetBytes(key)));
    }

    public static void Clear()
    {
        try { File.Delete(KeyPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private const int CryptProtectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description,
        IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description,
        IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private static byte[] Protect(byte[] data) => Transform(data, protect: true);

    private static byte[] Unprotect(byte[] data) => Transform(data, protect: false);

    private static byte[] Transform(byte[] data, bool protect)
    {
        var input = new DataBlob { Size = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        Marshal.Copy(data, 0, input.Data, data.Length);
        try
        {
            DataBlob output;
            bool ok = protect
                ? CryptProtectData(ref input, "ETS DeepSeek API key", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    CryptProtectUiForbidden, out output);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            try
            {
                byte[] result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally
            {
                LocalFree(output.Data);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
        }
    }
}
