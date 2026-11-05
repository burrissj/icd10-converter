using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ICD10Converter.Core;

public sealed record GroupDto(string Path, string[] Descriptions);
public sealed record CodeDto(string Id, string GroupPath, string Description);

public sealed class MetadataDto
{
    public int PartNumber { get; set; }
    public int TotalParts { get; set; }
    public int CodesInPart { get; set; }
    public int TotalCodes { get; set; }
}

public sealed class IcdDocument
{
    public string Name { get; set; } = IcdJson.DefaultName;
    public string Description { get; set; } = IcdJson.DefaultDescription;
    public string VersionDescription { get; set; } = "";
    public List<GroupDto> Groups { get; set; } = new();
    public List<CodeDto> Codes { get; set; } = new();
    public string[] ExtensionGroups { get; set; } = Array.Empty<string>();

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public MetadataDto? Metadata { get; set; }
}

public static class IcdJson
{
    public const string DefaultName = "ICD-10-CM";
    public const string DefaultDescription = "International Classification of Diseases, 10th Revision, Clinical Modification";

    static readonly JsonSerializerOptions PrettyOpts = new()
    {
        WriteIndented = true,
        IndentSize = 4,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    static readonly JsonSerializerOptions CompactOpts = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static byte[] Serialize(IcdDocument doc, bool pretty) =>
        JsonSerializer.SerializeToUtf8Bytes(doc, pretty ? PrettyOpts : CompactOpts);

    public static List<string> SortedKeys(Dictionary<string, string> codes)
    {
        var keys = new List<string>(codes.Keys);
        keys.Sort(StringComparer.Ordinal);
        return keys;
    }

    public static string Category(string code) => code.Substring(0, Math.Min(3, code.Length));

    /// <summary>Builds the group + code lists for the given (already sorted) codes.</summary>
    public static void BuildGroupsAndCodes(
        IEnumerable<string> sortedCodes, Dictionary<string, string> all,
        List<GroupDto> groups, List<CodeDto> codes)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var code in sortedCodes)
        {
            var desc = all[code];

            if (code.Contains('.'))
            {
                var category = Category(code);
                var block = Hierarchy.BlockRange(category);
                var groupPath = $"1/{block}/{category}";
                codes.Add(new CodeDto(code, groupPath, desc));

                if (seen.Add(groupPath))
                {
                    var catDesc = all.TryGetValue(category, out var cd) ? cd : category;
                    groups.Add(new GroupDto(groupPath, new[]
                    {
                        catDesc, Hierarchy.BlockDescription(block), Hierarchy.ChapterDescription(category)
                    }));
                }

                var blockPath = $"1/{block}";
                if (seen.Add(blockPath))
                {
                    groups.Add(new GroupDto(blockPath, new[]
                    {
                        Hierarchy.BlockDescription(block), Hierarchy.ChapterDescription(category)
                    }));
                }
            }
            else
            {
                var block = Hierarchy.BlockRange(code);
                var groupPath = $"1/{block}";
                codes.Add(new CodeDto(code, groupPath, desc));

                if (seen.Add(groupPath))
                {
                    groups.Add(new GroupDto(groupPath, new[]
                    {
                        Hierarchy.BlockDescription(block), Hierarchy.ChapterDescription(code)
                    }));
                }
            }
        }
    }

    public static IcdDocument BuildDocument(Dictionary<string, string> codes, string version)
    {
        var doc = new IcdDocument { VersionDescription = version };
        BuildGroupsAndCodes(SortedKeys(codes), codes, doc.Groups, doc.Codes);
        return doc;
    }

    public static void WriteFile(string path, byte[] bytes)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllBytes(path, bytes);
    }
}

public sealed record SplitResult(string Directory, int Parts);

/// <summary>
/// Splits the output into parts that are each guaranteed to be &lt;= the size limit.
/// Categories (A00, A01, ...) always stay together with their sub-codes. Sizes are measured on the real
/// serialized file (groups, indentation and metadata included), not an approximation.
/// </summary>
public static class Splitter
{
    sealed record Cat(string Name, List<string> Codes);

    public static SplitResult Split(
        string outputPath, Dictionary<string, string> all, long maxBytes, bool pretty,
        string version, Action<string> log)
    {
        var sorted = IcdJson.SortedKeys(all);

        var cats = new List<Cat>();
        foreach (var code in sorted)
        {
            var name = IcdJson.Category(code);
            if (cats.Count == 0 || cats[^1].Name != name) cats.Add(new Cat(name, new List<string>()));
            cats[^1].Codes.Add(code);
        }
        log($"Total codes to split: {sorted.Count} in {cats.Count} categories");

        IcdDocument MakeDoc(int start, int count, int part, int total)
        {
            var list = new List<string>();
            for (int i = start; i < start + count; i++) list.AddRange(cats[i].Codes);
            var doc = new IcdDocument { VersionDescription = version };
            IcdJson.BuildGroupsAndCodes(list, all, doc.Groups, doc.Codes);
            doc.Metadata = new MetadataDto
            {
                PartNumber = part, TotalParts = total, CodesInPart = list.Count, TotalCodes = all.Count
            };
            return doc;
        }

        // Worst-case digit widths in the metadata so the final file is never larger than the probe.
        long SizeOf(int start, int count) => IcdJson.Serialize(MakeDoc(start, count, 99999, 99999), pretty).LongLength;

        var ranges = new List<(int Start, int Count)>();
        int pos = 0;
        while (pos < cats.Count)
        {
            int remaining = cats.Count - pos;
            int lo = 1; // known (or forced) to fit
            if (SizeOf(pos, 1) > maxBytes)
                log($"  Warning: category {cats[pos].Name} alone exceeds the limit; it will be its own part.");
            else
            {
                int hi = remaining + 1; // known not to fit (sentinel)
                int probe = 2;
                while (probe <= remaining)
                {
                    if (SizeOf(pos, probe) <= maxBytes) { lo = probe; probe *= 2; }
                    else { hi = probe; break; }
                }
                while (hi - lo > 1)
                {
                    int mid = (lo + hi) / 2;
                    if (SizeOf(pos, mid) <= maxBytes) lo = mid; else hi = mid;
                }
            }
            ranges.Add((pos, lo));
            pos += lo;
        }

        var baseDir = Path.GetDirectoryName(Path.GetFullPath(outputPath))!;
        var baseName = Path.GetFileNameWithoutExtension(outputPath);
        var splitDir = Path.Combine(baseDir, $"{baseName}-Parts");
        Directory.CreateDirectory(splitDir);
        log($"Writing {ranges.Count} files to: {splitDir}");

        // Clear stale parts from an earlier run so the folder never mixes two splits.
        foreach (var old in Directory.GetFiles(splitDir, $"{baseName}-Part-*-of-*.json")) File.Delete(old);

        int width = Math.Max(2, ranges.Count.ToString().Length);
        for (int p = 0; p < ranges.Count; p++)
        {
            var (start, count) = ranges[p];
            var doc = MakeDoc(start, count, p + 1, ranges.Count);
            var bytes = IcdJson.Serialize(doc, pretty);
            var name = $"{baseName}-Part-{(p + 1).ToString().PadLeft(width, '0')}-of-{ranges.Count.ToString().PadLeft(width, '0')}.json";
            File.WriteAllBytes(Path.Combine(splitDir, name), bytes);
            log($"  {name}  ({bytes.Length / 1048576.0:0.00} MB, {doc.Codes.Count} codes)");
        }

        var sb = new StringBuilder();
        sb.AppendLine("ICD-10 CODES - SPLIT FILES");
        sb.AppendLine("==========================");
        sb.AppendLine();
        sb.AppendLine($"Total Codes: {all.Count}");
        sb.AppendLine($"Split into: {ranges.Count} parts");
        sb.AppendLine($"Max file size: {maxBytes} bytes ({maxBytes / 1048576.0:0.00} MB)");
        sb.AppendLine($"Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine("IMPORT INSTRUCTIONS:");
        sb.AppendLine("-------------------");
        sb.AppendLine("1. Import each file in order: Part-01, Part-02, etc.");
        sb.AppendLine("2. In your application, click Import and select each file one at a time");
        sb.AppendLine("3. Wait for each import to complete before importing the next file");
        sb.AppendLine($"4. After all files are imported, you should have all {all.Count} codes");
        sb.AppendLine();
        sb.AppendLine($"Files are located in: {splitDir}");
        File.WriteAllText(Path.Combine(splitDir, "IMPORT-INSTRUCTIONS.txt"), sb.ToString(), new UTF8Encoding(false));
        log("  IMPORT-INSTRUCTIONS.txt");

        return new SplitResult(splitDir, ranges.Count);
    }
}
