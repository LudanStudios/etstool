using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;

namespace ETSAssistant.WinUI3.Services;

/// <summary>Extracts the complete bundled VB-CABLE package before starting its install/remove setup UI.</summary>
public static class VirtualCableSetupService
{
    private const string PackageName = "VBCABLE_Driver_Pack45.zip";

    public static string PackagePath(string appRoot)
    {
        string packagedWithApp = Path.Combine(appRoot, "tools", "VBCABLE", PackageName);
        if (File.Exists(packagedWithApp)) return packagedWithApp;
        return Path.Combine(AppContext.BaseDirectory, "tools", "VBCABLE", PackageName);
    }

    public static bool HasInstaller(string appRoot) => File.Exists(PackagePath(appRoot));

    /// <summary>Extracts every driver file to a local temporary folder, then starts the vendor setup UI with UAC.</summary>
    public static async Task<int> RunSetupAsync(string appRoot)
    {
        string packagePath = PackagePath(appRoot);
        if (!File.Exists(packagePath)) throw new FileNotFoundException(LocalizationService.Current.Translate("virtual_cable.package_missing"), packagePath);

        string extractionPath = Path.Combine(Path.GetTempPath(), "ETS-VBCABLE-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(extractionPath);
        try
        {
            using (ZipArchive archive = ZipFile.OpenRead(packagePath))
            {
                ValidateArchive(archive, extractionPath);
                if (!archive.Entries.Any(entry => entry.FullName.EndsWith(".inf", StringComparison.OrdinalIgnoreCase)) ||
                    !archive.Entries.Any(entry => entry.FullName.EndsWith(".sys", StringComparison.OrdinalIgnoreCase)) ||
                    !archive.Entries.Any(entry => entry.FullName.EndsWith(".cat", StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException(LocalizationService.Current.Translate("virtual_cable.package_files_missing"));

                archive.ExtractToDirectory(extractionPath);
            }

            string installerPath = Path.Combine(extractionPath, "VBCABLE_Setup_x64.exe");
            if (!File.Exists(installerPath))
                throw new InvalidDataException(LocalizationService.Current.Translate("virtual_cable.installer_missing"));

            var startInfo = new ProcessStartInfo(installerPath)
            {
                WorkingDirectory = extractionPath,
                UseShellExecute = true,
                Verb = "runas"
            };
            using Process? process = Process.Start(startInfo);
            if (process is null) throw new InvalidOperationException(LocalizationService.Current.Translate("virtual_cable.installer_start_failed"));
            await process.WaitForExitAsync();
            return process.ExitCode;
        }
        finally
        {
            try { Directory.Delete(extractionPath, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void ValidateArchive(ZipArchive archive, string destination)
    {
        string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            string path = Path.GetFullPath(Path.Combine(destination,
                entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(LocalizationService.Current.Translate("virtual_cable.invalid_archive_path"));
        }
    }

    public static void OpenRecordingDevices()
    {
        Process.Start(new ProcessStartInfo("control.exe", "mmsys.cpl,,1")
        {
            UseShellExecute = true
        });
    }

    public static void OpenSoundSettings()
    {
        Process.Start(new ProcessStartInfo("ms-settings:sound")
        {
            UseShellExecute = true
        });
    }
}
