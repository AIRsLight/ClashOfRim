using AIRsLight.ClashOfRim.Protocol;
using System.Text.Json;

namespace AIRsLight.ClashOfRim.Network;

public static class ServerLocalizationFileLoader
{
    public static void Load(string contentRootPath)
    {
        ServerLocalization.Reset();
        string directory = Path.Combine(contentRootPath, "Localization");
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException("Server localization directory was not found: " + directory);
        }

        int loadedFiles = 0;
        foreach (string file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                using FileStream stream = File.OpenRead(file);
                Dictionary<string, string>? entries = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
                if (entries is not null)
                {
                    ServerLocalization.MergeExternal(Path.GetFileNameWithoutExtension(file), entries);
                    loadedFiles++;
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to load server localization file: " + file, ex);
            }
        }

        if (loadedFiles == 0)
        {
            throw new InvalidOperationException("No server localization files were found in: " + directory);
        }

        ServerLocalization.RequireReady();
    }
}
