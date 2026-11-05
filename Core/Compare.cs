using System.Text.Json;

namespace ICD10Converter.Core;

public sealed record DiffRow(string Status, string Code, string OldDescription, string NewDescription);

public sealed record DiffResult(List<DiffRow> Rows, int Unchanged, int OldCount, int NewCount)
{
    public int Added => Rows.Count(r => r.Status == "Added");
    public int Removed => Rows.Count(r => r.Status == "Removed");
    public int Changed => Rows.Count(r => r.Status == "Changed");
}

public static class CodeSetLoader
{
    /// <summary>
    /// Loads a code set from one or more paths separated by ';'. Each path may be a converted .json file,
    /// a CMS .txt codes/order file, or a folder (e.g. a "-Parts" folder of split files).
    /// </summary>
    public static Dictionary<string, string> Load(string pathSpec)
    {
        var result = new Dictionary<string, string>(80_000, StringComparer.Ordinal);
        var parts = pathSpec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) throw new ArgumentException("No file selected.");

        foreach (var p in parts)
        {
            var files = new List<string>();
            if (Directory.Exists(p))
            {
                files.AddRange(Directory.GetFiles(p, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
                if (files.Count == 0)
                {
                    var (codes, _) = Downloader.FindFiles(p);
                    if (codes != null) files.Add(codes);
                }
                if (files.Count == 0) throw new FileNotFoundException($"No .json or codes .txt files found in {p}");
            }
            else if (File.Exists(p)) files.Add(p);
            else throw new FileNotFoundException($"File not found: {p}");

            foreach (var f in files)
            {
                var set = f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                    ? LoadJson(f)
                    : IcdParser.Read(f, FileKind.Auto, new Issues());
                foreach (var (k, v) in set) result[k] = v;
            }
        }
        return result;
    }

    static Dictionary<string, string> LoadJson(string path)
    {
        using var fs = File.OpenRead(path);
        using var doc = JsonDocument.Parse(fs, new JsonDocumentOptions { AllowTrailingCommas = true });
        if (doc.RootElement.ValueKind != JsonValueKind.Object ||
            !doc.RootElement.TryGetProperty("Codes", out var arr) || arr.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{Path.GetFileName(path)} is not an ICD-10 JSON file produced by this converter (no \"Codes\" array).");

        var set = new Dictionary<string, string>(arr.GetArrayLength(), StringComparer.Ordinal);
        foreach (var e in arr.EnumerateArray())
        {
            if (e.TryGetProperty("Id", out var id) && id.GetString() is { } code)
                set[code] = e.TryGetProperty("Description", out var d) ? d.GetString() ?? "" : "";
        }
        return set;
    }
}

public static class CodeDiff
{
    static string Norm(string s) => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static DiffResult Compare(Dictionary<string, string> older, Dictionary<string, string> newer)
    {
        var rows = new List<DiffRow>();
        int unchanged = 0;

        foreach (var (code, oldDesc) in older)
        {
            if (!newer.TryGetValue(code, out var newDesc))
                rows.Add(new DiffRow("Removed", code, oldDesc, ""));
            else if (Norm(oldDesc) != Norm(newDesc))
                rows.Add(new DiffRow("Changed", code, oldDesc, newDesc));
            else
                unchanged++;
        }
        foreach (var (code, newDesc) in newer)
            if (!older.ContainsKey(code)) rows.Add(new DiffRow("Added", code, "", newDesc));

        rows.Sort((a, b) => string.CompareOrdinal(a.Code, b.Code));
        return new DiffResult(rows, unchanged, older.Count, newer.Count);
    }
}
