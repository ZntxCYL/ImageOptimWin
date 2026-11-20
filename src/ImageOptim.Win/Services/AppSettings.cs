using System.IO;
using System.Text.Json;

namespace ImageOptim.Win.Services;

public sealed class AppSettings
{
    // System follows Windows AppsUseLightTheme (not desktop wallpaper/accent color).
    public string ThemeMode { get; set; } = "System";
    public bool UseOxipng { get; set; } = true;
    public bool UsePngquant { get; set; } = true;
    public bool UseJpegoptim { get; set; } = true;
    public bool UseJpegtran { get; set; } = true;
    public bool UseGifsicle { get; set; } = true;
    public bool UseSvgo { get; set; } = true;
    public bool StripPngMetadata { get; set; } = true;
    public bool StripJpegMetadata { get; set; } = true;
    public bool PreserveTimestamps { get; set; } = true;
    public bool EnableLossy { get; set; } = false;
    public int JpegQuality { get; set; } = 80;
    public int PngQuality { get; set; } = 80;
    public int OptimizationLevel { get; set; } = 2;
    public string ToolsDirectory { get; set; } = "";

    public AppSettings Copy() => (AppSettings)MemberwiseClone();
}

public static class SettingsStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ImageOptimWin", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings()
                : new AppSettings();
        }
        catch (Exception) { return new AppSettings(); }
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, FilePath, overwrite: true);
    }
}
