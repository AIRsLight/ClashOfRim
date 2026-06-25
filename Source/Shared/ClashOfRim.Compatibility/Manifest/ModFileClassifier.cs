namespace AIRsLight.ClashOfRim.Compatibility;

public static class ModFileClassifier
{
    public static ModFileKind Classify(string relativePath)
    {
        string path = Normalize(relativePath);
        string extension = Path.GetExtension(path).ToLowerInvariant();

        if (path.StartsWith("assemblies/", StringComparison.OrdinalIgnoreCase) && extension == ".dll")
        {
            return ModFileKind.Assembly;
        }

        if (path.StartsWith("defs/", StringComparison.OrdinalIgnoreCase) && extension == ".xml")
        {
            return ModFileKind.Def;
        }

        if (path.StartsWith("patches/", StringComparison.OrdinalIgnoreCase) && extension == ".xml")
        {
            return ModFileKind.Patch;
        }

        if (path.StartsWith("languages/", StringComparison.OrdinalIgnoreCase))
        {
            return ModFileKind.Language;
        }

        if (path.StartsWith("about/", StringComparison.OrdinalIgnoreCase))
        {
            return ModFileKind.About;
        }

        if (path.StartsWith("textures/", StringComparison.OrdinalIgnoreCase)
            || path.Equals("preview.png", StringComparison.OrdinalIgnoreCase))
        {
            return ModFileKind.Texture;
        }

        return ModFileKind.Other;
    }

    public static bool IsPureTranslationMod(ModManifestEntry mod)
    {
        if (mod.Files.Count == 0)
        {
            return false;
        }

        bool hasLanguageFile = false;
        foreach (ControlledFileEntry file in mod.Files)
        {
            ModFileKind kind = file.Kind == ModFileKind.Other
                ? Classify(file.RelativePath)
                : file.Kind;

            if (kind == ModFileKind.Language)
            {
                hasLanguageFile = true;
                continue;
            }

            if (kind is ModFileKind.About or ModFileKind.Texture)
            {
                continue;
            }

            return false;
        }

        return hasLanguageFile;
    }

    private static string Normalize(string path)
    {
        return path.Replace('\\', '/').TrimStart('/');
    }
}
