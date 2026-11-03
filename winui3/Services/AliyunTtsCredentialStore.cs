using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace ETSAssistant.WinUI3.Services;

public sealed record AliyunTtsCredentials(string AccessKeyId, string AccessKeySecret, string AppKey);

/// <summary>Stores Alibaba Cloud TTS credentials in one DPAPI-protected file beside the app.</summary>
public static class AliyunTtsCredentialStore
{
    private static readonly string ProgramCredentialPath =
        Path.Combine(AppContext.BaseDirectory, "aliyun_tts_credentials.bin");
    private static string _lastCredentialSource = "credentials.source_not_read";

    public static bool HasCredentials => Load() is { } credentials &&
        !string.IsNullOrWhiteSpace(credentials.AccessKeyId) &&
        !string.IsNullOrWhiteSpace(credentials.AccessKeySecret) &&
        !string.IsNullOrWhiteSpace(credentials.AppKey);

    public static string CredentialSourceStatus =>
        LocalizationService.Current.Format("credentials.source_status",
            ("source", LocalizationService.Current.Translate(_lastCredentialSource)),
            ("path", ProgramCredentialPath));

    public static string ConfigurationStatus => Load() is null
        ? LocalizationService.Current.Translate("credentials.aliyun_not_configured")
        : LocalizationService.Current.Translate("credentials.aliyun_configured_dpapi");

    public static void Initialize()
    {
        try
        {
            _ = Load();
        }
        catch (Exception ex)
        {
            // Credential diagnostics must never prevent the assistant from starting.
            _lastCredentialSource = "credentials.source_read_exception";
        }
    }

    public static AliyunTtsCredentials? Load()
    {
        try
        {
            if (!File.Exists(ProgramCredentialPath))
            {
                _lastCredentialSource = "credentials.source_file_missing";
                return null;
            }

            byte[] protectedData = File.ReadAllBytes(ProgramCredentialPath);
            byte[]? clear = null;
            try
            {
                clear = Unprotect(protectedData);
                AliyunTtsCredentials? credentials = JsonSerializer.Deserialize<AliyunTtsCredentials>(clear);
                bool valid = credentials is not null &&
                    !string.IsNullOrWhiteSpace(credentials.AccessKeyId) &&
                    !string.IsNullOrWhiteSpace(credentials.AccessKeySecret) &&
                    !string.IsNullOrWhiteSpace(credentials.AppKey);
                _lastCredentialSource = valid
                    ? "credentials.source_program_dpapi"
                    : "credentials.source_file_invalid";
                return valid ? credentials : null;
            }
            finally
            {
                if (clear is not null) CryptographicOperations.ZeroMemory(clear);
                CryptographicOperations.ZeroMemory(protectedData);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                  CryptographicException or JsonException or ArgumentException)
        {
            _lastCredentialSource = "credentials.source_file_unreadable";
            return null;
        }
    }

    public static void Save(AliyunTtsCredentials credentials)
    {
        if (string.IsNullOrWhiteSpace(credentials.AccessKeyId) ||
            string.IsNullOrWhiteSpace(credentials.AccessKeySecret) ||
            string.IsNullOrWhiteSpace(credentials.AppKey))
            throw new ArgumentException(LocalizationService.Current.Translate("credentials.aliyun_fields_required"), nameof(credentials));

        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(credentials);
        byte[]? protectedData = null;
        string temporaryPath = ProgramCredentialPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            protectedData = Protect(clear);
            Directory.CreateDirectory(Path.GetDirectoryName(ProgramCredentialPath)!);
            File.WriteAllBytes(temporaryPath, protectedData);
            File.Move(temporaryPath, ProgramCredentialPath, overwrite: true);
            _lastCredentialSource = "credentials.source_program_dpapi_saved";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                  CryptographicException or ArgumentException or NotSupportedException)
        {
            throw new IOException(LocalizationService.Current.Format("credentials.aliyun_save_failed_path",
                ("path", ProgramCredentialPath)), ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            if (protectedData is not null) CryptographicOperations.ZeroMemory(protectedData);
            try { File.Delete(temporaryPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public static void Clear()
    {
        File.Delete(ProgramCredentialPath);
        _lastCredentialSource = "credentials.source_file_cleared";
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
                ? CryptProtectData(ref input, "ETS Alibaba Cloud TTS credentials", IntPtr.Zero, IntPtr.Zero,
                    IntPtr.Zero, CryptProtectUiForbidden, out output)
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
                if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            }
        }
        finally
        {
            Marshal.Copy(new byte[data.Length], 0, input.Data, data.Length);
            Marshal.FreeHGlobal(input.Data);
        }
    }
}
