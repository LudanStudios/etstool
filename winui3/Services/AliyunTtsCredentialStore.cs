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
    private static string _lastCredentialSource = "尚未读取";

    public static bool HasCredentials => Load() is { } credentials &&
        !string.IsNullOrWhiteSpace(credentials.AccessKeyId) &&
        !string.IsNullOrWhiteSpace(credentials.AccessKeySecret) &&
        !string.IsNullOrWhiteSpace(credentials.AppKey);

    public static string CredentialSourceStatus =>
        $"凭据读取来源：{_lastCredentialSource}（{ProgramCredentialPath}）";

    public static string ConfigurationStatus => Load() is null
        ? "状态：本机未配置阿里云凭据，将使用内置 Worker（设备每天最多 10 次，IP 每天最多 30 次）。"
        : "状态：本机凭据已使用当前 Windows 用户 DPAPI 加密保存在程序目录；本机凭据调用不占 Worker 额度。";

    public static void Initialize()
    {
        try
        {
            _ = Load();
        }
        catch (Exception ex)
        {
            // Credential diagnostics must never prevent the assistant from starting.
            _lastCredentialSource = $"程序目录读取异常（{ex.GetType().Name}）";
        }
    }

    public static AliyunTtsCredentials? Load()
    {
        try
        {
            if (!File.Exists(ProgramCredentialPath))
            {
                _lastCredentialSource = "程序目录中未找到有效凭据文件";
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
                    ? "程序目录 DPAPI 加密文件"
                    : "程序目录凭据文件内容无效";
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
            _lastCredentialSource = $"程序目录凭据文件无法读取或解密（{ex.GetType().Name}）";
            return null;
        }
    }

    public static void Save(AliyunTtsCredentials credentials)
    {
        if (string.IsNullOrWhiteSpace(credentials.AccessKeyId) ||
            string.IsNullOrWhiteSpace(credentials.AccessKeySecret) ||
            string.IsNullOrWhiteSpace(credentials.AppKey))
            throw new ArgumentException("AccessKey ID、AccessKey Secret 和 AppKey 都需要填写。", nameof(credentials));

        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(credentials);
        byte[]? protectedData = null;
        string temporaryPath = ProgramCredentialPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            protectedData = Protect(clear);
            Directory.CreateDirectory(Path.GetDirectoryName(ProgramCredentialPath)!);
            File.WriteAllBytes(temporaryPath, protectedData);
            File.Move(temporaryPath, ProgramCredentialPath, overwrite: true);
            _lastCredentialSource = "程序目录 DPAPI 加密文件（刚保存）";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                  CryptographicException or ArgumentException or NotSupportedException)
        {
            throw new IOException(
                $"无法在程序运行目录保存阿里云凭据：{ProgramCredentialPath}。请检查该目录的写入权限。", ex);
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
        _lastCredentialSource = "程序目录凭据文件已清除";
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
