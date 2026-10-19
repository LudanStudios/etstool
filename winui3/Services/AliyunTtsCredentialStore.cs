using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.ComponentModel;

namespace ETSAssistant.WinUI3.Services;

public sealed record AliyunTtsCredentials(string AccessKeyId, string AccessKeySecret, string AppKey);
public sealed record AliyunTtsCredentialSaveResult(
    bool ProgramDirectorySaved,
    bool CredentialManagerSaved,
    bool UserDataFallbackSaved)
{
    public bool BothPrimaryCopiesSaved => ProgramDirectorySaved && CredentialManagerSaved;
    public bool Persisted => ProgramDirectorySaved || CredentialManagerSaved || UserDataFallbackSaved;
}

/// <summary>Stores Alibaba Cloud TTS credentials encrypted for the current Windows user.</summary>
public static class AliyunTtsCredentialStore
{
    private const string CredentialTarget = "ETSAssistant/AliyunTTS";
    private const uint GenericCredentialType = 1;
    private const uint LocalMachinePersistence = 2;
    private const int CredentialNotFound = 1168;
    private static readonly string ProgramCredentialPath =
        Path.Combine(AppContext.BaseDirectory, "aliyun_tts_credentials.bin");
    private static AliyunTtsCredentials? _sessionCredentials;
    private static bool _sessionOnly;
    private static bool _migrationAttempted;
    private static string _lastCredentialSource = "尚未读取";
    private static string _credentialManagerReadStatus = "尚未读取";
    private static readonly string[] LegacyCredentialPaths =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ETS", "aliyun_tts_credentials.bin"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ETS", "aliyun_tts_credentials.bin")
    ];

    public static bool HasCredentials => Load() is { } credentials &&
        !string.IsNullOrWhiteSpace(credentials.AccessKeyId) &&
        !string.IsNullOrWhiteSpace(credentials.AccessKeySecret) &&
        !string.IsNullOrWhiteSpace(credentials.AppKey);

    public static string CredentialSourceStatus => $"凭据读取来源：{_lastCredentialSource}";

    public static string ConfigurationStatus
    {
        get
        {
            if (_sessionCredentials is not null && _sessionOnly)
                return "状态：凭证仅在本次运行期间有效，关闭助手后需要重新输入。";
            if (Load() is null) return "状态：尚未配置阿里云 TTS 凭证。";
            bool programSaved = LoadProgramFile() is not null;
            bool managerSaved = LoadCredentialManager() is not null;
            if (programSaved && managerSaved) return "状态：凭证已加密保存在程序目录和 Windows 凭据管理器。";
            if (programSaved) return "状态：已保存在程序目录；Windows 凭据管理器副本不可用。";
            if (managerSaved) return "状态：已保存在 Windows 凭据管理器；程序目录副本不可用。";
            return LoadLegacyFile() is not null
                ? "状态：已保存在用户数据目录；程序目录和凭据管理器副本不可用。"
                : "状态：凭证仅在本次运行期间有效，双份持久保存未完成。";
        }
    }

    public static void Initialize()
    {
        try
        {
            _ = Load();
        }
        catch (Exception ex)
        {
            // Credential diagnostics must never prevent the assistant from starting.
            _lastCredentialSource = $"启动读取异常（{ex.GetType().Name}）";
        }
    }

    public static AliyunTtsCredentials? Load()
    {
        if (_sessionCredentials is not null)
        {
            _lastCredentialSource = _sessionOnly
                ? "本次运行内存（未能持久保存）"
                : "本次运行内存（凭据刚保存，重启后按持久存储优先级读取）";
            return _sessionCredentials;
        }

        // Startup priority: Windows Credential Manager first, then the encrypted
        // copy beside the executable. Legacy per-user files are migration fallbacks.
        AliyunTtsCredentials? managerCredentials = LoadCredentialManager();
        if (managerCredentials is not null)
        {
            _lastCredentialSource = "Windows 凭据管理器";
            MigrateToProgramDirectory(managerCredentials);
            return managerCredentials;
        }

        AliyunTtsCredentials? programCredentials = LoadProgramFile();
        if (programCredentials is not null)
        {
            _lastCredentialSource = $"程序目录（{_credentialManagerReadStatus}）";
            MigrateToProgramDirectory(programCredentials);
            return programCredentials;
        }

        AliyunTtsCredentials? legacyCredentials = LoadLegacyFile();
        if (legacyCredentials is not null)
        {
            _lastCredentialSource = $"用户数据目录 DPAPI 加密文件（{_credentialManagerReadStatus}）";
            MigrateToProgramDirectory(legacyCredentials);
            return legacyCredentials;
        }

        _lastCredentialSource = $"未配置（{_credentialManagerReadStatus}；程序目录无有效凭据）";
        return legacyCredentials;
    }

    private static void MigrateToProgramDirectory(AliyunTtsCredentials credentials)
    {
        if (_migrationAttempted) return;
        _migrationAttempted = true;

        byte[]? clear = null;
        byte[]? protectedData = null;
        try
        {
            clear = JsonSerializer.SerializeToUtf8Bytes(credentials);
            protectedData = Protect(clear);
            TrySaveProgramFile(protectedData);
            TrySaveCredentialManager(protectedData);
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Keep using the existing persistent store when the program folder is read-only.
        }
        finally
        {
            if (clear is not null) CryptographicOperations.ZeroMemory(clear);
            if (protectedData is not null) CryptographicOperations.ZeroMemory(protectedData);
        }
    }

    private static AliyunTtsCredentials? LoadProgramFile() => LoadEncryptedFile(ProgramCredentialPath);

    private static AliyunTtsCredentials? LoadEncryptedFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            byte[] protectedData = File.ReadAllBytes(path);
            byte[]? clear = null;
            try
            {
                clear = Unprotect(protectedData);
                return JsonSerializer.Deserialize<AliyunTtsCredentials>(clear);
            }
            finally
            {
                if (clear is not null) CryptographicOperations.ZeroMemory(clear);
                CryptographicOperations.ZeroMemory(protectedData);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static AliyunTtsCredentials? LoadCredentialManager()
    {
        if (!CredRead(CredentialTarget, GenericCredentialType, 0, out IntPtr credentialPointer))
        {
            int error = Marshal.GetLastWin32Error();
            _credentialManagerReadStatus = error == CredentialNotFound
                ? "Windows 凭据管理器中未找到副本"
                : $"Windows 凭据管理器读取失败（错误码 {error}）";
            return null;
        }

        try
        {
            NativeCredential credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0 ||
                credential.CredentialBlobSize > 5120)
            {
                _credentialManagerReadStatus = "Windows 凭据管理器中的凭据数据无效";
                return null;
            }

            byte[] protectedData = new byte[checked((int)credential.CredentialBlobSize)];
            byte[]? clear = null;
            try
            {
                Marshal.Copy(credential.CredentialBlob, protectedData, 0, protectedData.Length);
                clear = Unprotect(protectedData);
                AliyunTtsCredentials? credentials = JsonSerializer.Deserialize<AliyunTtsCredentials>(clear);
                _credentialManagerReadStatus = credentials is null
                    ? "Windows 凭据管理器中的凭据内容为空"
                    : "Windows 凭据管理器副本可读取";
                return credentials;
            }
            finally
            {
                if (clear is not null) CryptographicOperations.ZeroMemory(clear);
                CryptographicOperations.ZeroMemory(protectedData);
            }
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException)
        {
            _credentialManagerReadStatus = "Windows 凭据管理器中的副本无法解密或格式无效";
            return null;
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    private static AliyunTtsCredentials? LoadLegacyFile()
    {
        foreach (string path in LegacyCredentialPaths)
        {
            AliyunTtsCredentials? credentials = LoadEncryptedFile(path);
            if (credentials is not null) return credentials;
        }
        return null;
    }

    public static AliyunTtsCredentialSaveResult Save(AliyunTtsCredentials credentials)
    {
        if (string.IsNullOrWhiteSpace(credentials.AccessKeyId) ||
            string.IsNullOrWhiteSpace(credentials.AccessKeySecret) ||
            string.IsNullOrWhiteSpace(credentials.AppKey))
            throw new ArgumentException("AccessKey ID、AccessKey Secret 和 AppKey 都需要填写。", nameof(credentials));

        byte[] clear = JsonSerializer.SerializeToUtf8Bytes(credentials);
        byte[]? protectedData = null;
        try
        {
            protectedData = Protect(clear);
            bool savedInProgramDirectory = TrySaveProgramFile(protectedData);
            bool savedInCredentialManager = TrySaveCredentialManager(protectedData);
            bool savedInUserData = false;

            if (savedInProgramDirectory && savedInCredentialManager)
            {
                _sessionCredentials = null;
                _sessionOnly = false;
                _migrationAttempted = true;
                DeleteLegacyFiles();
                return new AliyunTtsCredentialSaveResult(true, true, false);
            }

            if (!savedInProgramDirectory && !savedInCredentialManager)
                savedInUserData = TrySaveEncryptedFile(protectedData);

            bool persisted = savedInProgramDirectory || savedInCredentialManager || savedInUserData;
            _sessionCredentials = persisted ? null : credentials;
            _sessionOnly = !persisted;
            _migrationAttempted = true;
            _lastCredentialSource = persisted
                ? "尚未读取（保存已完成，后续读取按优先级进行）"
                : "本次运行内存（未能持久保存）";
            if (persisted) DeleteLegacyFiles();
            return new AliyunTtsCredentialSaveResult(
                savedInProgramDirectory, savedInCredentialManager, savedInUserData);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(clear);
            if (protectedData is not null) CryptographicOperations.ZeroMemory(protectedData);
        }
    }

    private static bool TrySaveCredentialManager(byte[] protectedData)
    {
        IntPtr blobPointer = IntPtr.Zero;
        try
        {
            blobPointer = Marshal.AllocHGlobal(protectedData.Length);
            Marshal.Copy(protectedData, 0, blobPointer, protectedData.Length);
            var credential = new NativeCredential
            {
                Type = GenericCredentialType,
                TargetName = CredentialTarget,
                CredentialBlobSize = (uint)protectedData.Length,
                CredentialBlob = blobPointer,
                Persist = LocalMachinePersistence,
                UserName = Environment.UserName
            };
            return CredWrite(ref credential, 0);
        }
        catch (Exception ex) when (ex is Win32Exception or UnauthorizedAccessException or IOException or DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            if (blobPointer != IntPtr.Zero)
            {
                Marshal.Copy(new byte[protectedData.Length], 0, blobPointer, protectedData.Length);
                Marshal.FreeHGlobal(blobPointer);
            }
        }
    }

    private static bool TrySaveProgramFile(byte[] protectedData)
    {
        string temporaryPath = ProgramCredentialPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ProgramCredentialPath)!);
            File.WriteAllBytes(temporaryPath, protectedData);
            File.Move(temporaryPath, ProgramCredentialPath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool TrySaveEncryptedFile(byte[] protectedData)
    {
        foreach (string path in LegacyCredentialPaths)
        {
            string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(temporaryPath, protectedData);
                File.Move(temporaryPath, path, overwrite: true);
                DeleteLegacyFiles(exceptPath: path);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Try the other per-user location.
            }
            finally
            {
                try { File.Delete(temporaryPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        return false;
    }

    private static void DeleteLegacyFiles(string? exceptPath = null)
    {
        foreach (string path in LegacyCredentialPaths)
        {
            if (string.Equals(path, exceptPath, StringComparison.OrdinalIgnoreCase)) continue;
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public static void Clear()
    {
        _sessionCredentials = null;
        _sessionOnly = false;
        _migrationAttempted = false;
        _lastCredentialSource = "未配置";
        bool managerDeleted = CredDelete(CredentialTarget, GenericCredentialType, 0);
        int error = managerDeleted ? 0 : Marshal.GetLastWin32Error();

        try { File.Delete(ProgramCredentialPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        DeleteLegacyFiles();

        if (!managerDeleted)
        {
            if (error != CredentialNotFound)
                throw new Win32Exception(error, "Windows 凭据管理器无法删除阿里云凭证。");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string targetName, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string targetName, uint type, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredFree(IntPtr buffer);

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
                LocalFree(output.Data);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input.Data);
        }
    }
}
