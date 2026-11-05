using System.Text;

namespace ETSAssistant.WinUI3.Services;

/// <summary>Publish a whole settings file so a running listener never reads a truncated edit.</summary>
internal static class SettingsFileStore
{
    public static void Write(string path, string contents)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, contents, new UTF8Encoding(false));
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Replace(temporary, path, null, ignoreMetadataErrors: true);
                    else File.Move(temporary, path);
                    break;
                }
                catch (Exception ex) when (attempt < 3 && ex is IOException or UnauthorizedAccessException)
                { Thread.Sleep(15); }
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
