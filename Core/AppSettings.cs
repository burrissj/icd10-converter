using System.Text.Json;

namespace ICD10Converter.Core;

public sealed class DownloadSource
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "";
    /// <summary>May contain {year}.</summary>
    public string Url { get; set; } = "";
    /// <summary>File name to save as; may contain {year}.</summary>
    public string FileName { get; set; } = "";

    public string ExpandUrl(int year) => Url.Replace("{year}", year.ToString());
    public string ExpandFileName(int year)
    {
        var name = string.IsNullOrWhiteSpace(FileName)
            ? Path.GetFileName(new Uri(ExpandUrl(year)).LocalPath)
            : FileName;
        name = name.Replace("{year}", year.ToString());
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
        return name;
    }
}

public static class AppPaths
{
    public static string Root { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ICD10Converter");
    public static string History => Path.Combine(Root, "history");
    public static string SettingsFile => Path.Combine(Root, "settings.json");
}

public sealed class AppSettings
{
    public int Year { get; set; } = 2026;
    /// <summary>Blank = ask every time.</summary>
    public string DownloadFolder { get; set; } = "";
    /// <summary>Blank = "v{Year}".</summary>
    public string VersionLabel { get; set; } = "";
    public bool PrettyPrint { get; set; } = true;
    public bool SplitEnabled { get; set; } = false;
    public long MaxSizeBytes { get; set; } = 3_670_016;
    public bool KeepHistory { get; set; } = true;
    public List<DownloadSource> Sources { get; set; } = DefaultSources();

    // --- OpenFDA medications tab ---
    public List<FdaEndpoint> FdaEndpoints { get; set; } = OpenFda.DefaultEndpoints();
    public string FdaSelectedEndpoint { get; set; } = "";
    public int FdaRecordCount { get; set; } = 5000;
    public int FdaPerFile { get; set; } = 1000;
    public bool FdaRemoveDuplicates { get; set; } = true;
    public string FdaOutputFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "FDA_Medications");

    public string EffectiveVersion => string.IsNullOrWhiteSpace(VersionLabel) ? $"v{Year}" : VersionLabel.Trim();

    public static List<DownloadSource> DefaultSources() => new()
    {
        new DownloadSource
        {
            Name = "ICD-10-CM codes + order file",
            Url = "https://www.cms.gov/files/zip/{year}-code-descriptions-tabular-order.zip",
            FileName = "icd10cm-{year}-codes-order.zip",
        },
        new DownloadSource
        {
            Name = "ICD-10-CM addenda",
            Url = "https://www.cms.gov/files/zip/{year}-code-descriptions-addenda.zip",
            FileName = "icd10cm-{year}-addenda.zip",
        },
    };

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile), Opts);
                if (s != null)
                {
                    s.Sources ??= DefaultSources();
                    if (s.FdaEndpoints == null || s.FdaEndpoints.Count == 0) s.FdaEndpoints = OpenFda.DefaultEndpoints();
                    if (s.FdaRecordCount <= 0) s.FdaRecordCount = 5000;
                    if (s.FdaPerFile <= 0) s.FdaPerFile = 1000;
                    if (s.MaxSizeBytes <= 0) s.MaxSizeBytes = 3_670_016;
                    return s;
                }
            }
        }
        catch { /* corrupt file: fall back to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.Root);
        File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(this, Opts));
    }
}
