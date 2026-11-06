using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ICD10Converter.Core;

public enum Severity { Info, Warning, Error }

public sealed record Finding(Severity Severity, string Check, string Message, string[] Samples);

/// <summary>Result of validating generated data: findings, headline numbers and a printable report.</summary>
public sealed class ValidationReport
{
    public ValidationReport(string title) => Title = title;

    public string Title { get; }
    public string Source { get; set; } = "";
    public DateTime Created { get; } = DateTime.Now;
    public List<Finding> Findings { get; } = new();
    public List<(string Label, string Value)> Stats { get; } = new();
    public List<string> Tables { get; } = new();

    public int Errors => Findings.Count(f => f.Severity == Severity.Error);
    public int Warnings => Findings.Count(f => f.Severity == Severity.Warning);
    public bool Passed => Errors == 0;

    public string Headline =>
        Errors > 0 ? $"FAILED - {Errors} error(s), {Warnings} warning(s)"
        : Warnings > 0 ? $"PASSED with {Warnings} warning(s)"
        : "PASSED - no problems found";

    public void Add(Severity severity, string check, string message, IEnumerable<string>? samples = null) =>
        Findings.Add(new Finding(severity, check, message, samples?.Take(10).ToArray() ?? Array.Empty<string>()));

    public void Stat(string label, int value) => Stats.Add((label, value.ToString("N0", CultureInfo.InvariantCulture)));
    public void Stat(string label, string value) => Stats.Add((label, value));

    public string ToText()
    {
        var sb = new StringBuilder();
        string title = Title + " - validation report";
        sb.AppendLine(title);
        sb.AppendLine(new string('=', title.Length));
        sb.AppendLine($"Generated : {Created:yyyy-MM-dd HH:mm:ss}");
        if (Source.Length > 0) sb.AppendLine($"Source    : {Source}");
        sb.AppendLine($"Result    : {Headline}");

        foreach (var sev in new[] { Severity.Error, Severity.Warning })
        {
            var list = Findings.Where(f => f.Severity == sev).ToList();
            if (list.Count == 0) continue;
            sb.AppendLine().AppendLine($"{sev}s ({list.Count})").AppendLine(new string('-', 20));
            foreach (var f in list)
            {
                sb.AppendLine($"  [{f.Check}] {f.Message}");
                foreach (var s in f.Samples) sb.AppendLine("        " + s);
            }
        }

        var info = Findings.Where(f => f.Severity == Severity.Info).ToList();
        if (info.Count > 0 || Stats.Count > 0)
        {
            sb.AppendLine().AppendLine("Details").AppendLine(new string('-', 20));
            foreach (var (label, value) in Stats) sb.AppendLine($"  {label,-40}{value}");
            foreach (var f in info) sb.AppendLine($"  [{f.Check}] {f.Message}");
        }
        foreach (var t in Tables) sb.AppendLine().Append(t);
        return sb.ToString();
    }

    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, ToText(), new UTF8Encoding(false));
    }

    /// <summary>Writes errors and warnings (not details) to a log callback.</summary>
    public void LogSummary(Action<string> log)
    {
        log($"Validation: {Headline}");
        foreach (var f in Findings.Where(f => f.Severity != Severity.Info))
        {
            string sample = f.Samples.Length > 0 ? $" e.g. {string.Join(", ", f.Samples.Take(3))}" : "";
            log($"  [{f.Severity}] {f.Check}: {f.Message}{sample}");
        }
    }
}

/// <summary>Checks on the ICD-10 code set and on the generated JSON files.</summary>
public static class IcdValidator
{
    // ICD-10-CM: a letter, then two alphanumerics (FY2027 added categories such as QA0 with a letter in
    // position 2), then an optional '.' with 1-4 alphanumerics.
    static readonly Regex CodeRx = new(@"^[A-Z][0-9A-Z][0-9A-Z](\.[0-9A-Z]{1,4})?$", RegexOptions.Compiled);

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    static bool HasControlOrBad(string s)
    {
        foreach (var c in s)
            if (c == '�' || (c < ' ' && c != '\t')) return true;
        return false;
    }

    /// <summary>Source-level checks on the final code set (after addenda).</summary>
    public static void ValidateCodes(Dictionary<string, string> codes, ValidationReport r, bool skipFormatChecks = false)
    {
        r.Stat("Total codes", codes.Count);
        if (codes.Count == 0)
        {
            r.Add(Severity.Error, "Content", "The code set is empty.");
            return;
        }

        var invalid = new List<string>();
        var empty = new List<string>();
        var badChars = new List<string>();
        var caseDup = new List<string>();
        var seenUpper = new HashSet<string>(StringComparer.Ordinal);
        var perLetter = new SortedDictionary<char, int>();
        var categoriesSeen = new HashSet<string>(StringComparer.Ordinal);
        int categories = 0, subcodes = 0, orphans = 0, longDesc = 0, doubleSpace = 0, maxLen = 0;

        foreach (var (code, desc) in codes)
        {
            if (!CodeRx.IsMatch(code)) invalid.Add(code);
            if (string.IsNullOrWhiteSpace(desc)) empty.Add(code);
            else
            {
                if (HasControlOrBad(desc)) badChars.Add(code);
                if (desc.Length > 500) longDesc++;
                if (desc.Contains("  ")) doubleSpace++;
                maxLen = Math.Max(maxLen, desc.Length);
            }
            if (!seenUpper.Add(code.ToUpperInvariant())) caseDup.Add(code);

            perLetter[code[0]] = perLetter.GetValueOrDefault(code[0]) + 1;
            categoriesSeen.Add(IcdJson.Category(code));
            if (code.Contains('.'))
            {
                subcodes++;
                if (!codes.ContainsKey(IcdJson.Category(code))) orphans++;
            }
            else categories++;
        }

        r.Stat("3-character categories", categories);
        r.Stat("Subcodes", subcodes);
        r.Stat("Longest description (chars)", maxLen);

        // When verifying JSON files these two are already reported by VerifyJson, so skip the repeat.
        if (!skipFormatChecks && invalid.Count > 0)
            r.Add(Severity.Error, "Code format", $"{invalid.Count:N0} code(s) are not valid ICD-10-CM codes (expected e.g. A00, A00.0, S72.001A).", invalid.OrderBy(x => x, StringComparer.Ordinal));
        if (!skipFormatChecks && empty.Count > 0)
            r.Add(Severity.Error, "Description", $"{empty.Count:N0} code(s) have an empty description.", empty);
        if (badChars.Count > 0)
            r.Add(Severity.Warning, "Encoding", $"{badChars.Count:N0} description(s) contain control or replacement characters (possible encoding problem in the source file).", badChars);
        if (caseDup.Count > 0)
            r.Add(Severity.Warning, "Duplicates", $"{caseDup.Count:N0} code(s) differ only by letter case from another code.", caseDup);
        if (longDesc > 0)
            r.Add(Severity.Warning, "Description", $"{longDesc:N0} description(s) are longer than 500 characters.");
        if (doubleSpace > 0)
            r.Add(Severity.Info, "Description", $"{doubleSpace:N0} description(s) contain double spaces (kept as in the source).");
        if (orphans > 0)
            r.Add(Severity.Info, "Hierarchy", $"{orphans:N0} subcode(s) have no 3-character category entry (normal when converting an order file, which only lists billable codes).");

        var missingLetters = "ABCDEFGHIJKLMNOPQRSTVWXYZ".Where(c => !perLetter.ContainsKey(c)).ToList();
        if (missingLetters.Count > 0)
            r.Add(Severity.Warning, "Coverage", $"No codes start with: {string.Join(", ", missingLetters)}. A complete release covers every letter (except U). Expected only for a partial file.");

        if (codes.Count < 60_000)
            r.Add(Severity.Warning, "Size", $"Only {codes.Count:N0} codes. A complete CMS release has roughly 70,000 or more, so check the input if this should be a full set.");

        int undescribed = categoriesSeen
            .Select(Hierarchy.BlockRange).Distinct()
            .Count(b => Hierarchy.BlockDescription(b) == b);
        if (undescribed > 0)
            r.Add(Severity.Info, "Hierarchy", $"{undescribed:N0} block group(s) have no descriptive name (only blocks A00-B99 have names, as in the original script).");

        var sb = new StringBuilder("Codes per chapter letter\n------------------------\n");
        foreach (var (letter, count) in perLetter)
        {
            string chapter = letter == 'U' ? "Codes for special purposes" : Hierarchy.ChapterDescription(letter.ToString());
            sb.AppendLine($"  {letter}  {count,7:N0}  {chapter}");
        }
        r.Tables.Add(sb.ToString());
    }

    /// <summary>Re-reads generated JSON file(s) and checks structure, hierarchy, split rules and (optionally) the source.</summary>
    public static void VerifyJson(IReadOnlyList<string> paths, Dictionary<string, string>? expected, long maxBytes, ValidationReport r)
    {
        var union = new Dictionary<string, string>(StringComparer.Ordinal);
        var dupIds = new List<string>();
        var invalidIds = new List<string>();
        var emptyDesc = new List<string>();
        var badPaths = new List<string>();
        var badGroups = new List<string>();
        var unusedGroups = new List<string>();
        var splitCats = new List<string>();
        var catFile = new Dictionary<string, string>(StringComparer.Ordinal);
        var metas = new List<(string File, int Part, int Total, int Declared, int Actual, int TotalCodes)>();
        int filesWithoutMeta = 0, totalGroups = 0, structureErrors = 0, readFiles = 0;

        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            try
            {
                long len = new FileInfo(path).Length;
                if (maxBytes > 0 && len > maxBytes)
                    r.Add(Severity.Error, "Size", $"{name} is {len:N0} bytes, over the {maxBytes:N0} byte limit.");

                using var fs = File.OpenRead(path);
                using var doc = JsonDocument.Parse(fs);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("Codes", out var codesEl) || codesEl.ValueKind != JsonValueKind.Array
                    || !root.TryGetProperty("Groups", out var groupsEl) || groupsEl.ValueKind != JsonValueKind.Array)
                {
                    r.Add(Severity.Error, "Structure", $"{name}: the Codes and/or Groups array is missing.");
                    structureErrors++;
                    continue;
                }
                readFiles++;

                if (string.IsNullOrEmpty(Str(root, "Name")))
                    r.Add(Severity.Warning, "Structure", $"{name}: the Name field is missing or empty.");
                if (string.IsNullOrEmpty(Str(root, "VersionDescription")))
                    r.Add(Severity.Warning, "Structure", $"{name}: the VersionDescription field is missing or empty.");

                var groupPaths = new HashSet<string>(StringComparer.Ordinal);
                foreach (var g in groupsEl.EnumerateArray())
                {
                    var gp = Str(g, "Path");
                    if (string.IsNullOrEmpty(gp)) { badGroups.Add($"{name}: a group has no Path"); continue; }
                    if (!groupPaths.Add(gp)) badGroups.Add($"{name}: duplicate group path {gp}");
                    bool descOk = g.TryGetProperty("Descriptions", out var d) && d.ValueKind == JsonValueKind.Array
                                  && d.GetArrayLength() > 0
                                  && d.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(x.GetString()));
                    if (!descOk) badGroups.Add($"{name}: group {gp} has a missing or empty description");
                }
                totalGroups += groupPaths.Count;

                var used = new HashSet<string>(StringComparer.Ordinal);
                int actual = 0;
                foreach (var c in codesEl.EnumerateArray())
                {
                    actual++;
                    var id = Str(c, "Id");
                    var gp = Str(c, "GroupPath");
                    var desc = Str(c, "Description");
                    if (string.IsNullOrEmpty(id)) { structureErrors++; r.Add(Severity.Error, "Structure", $"{name}: a code entry has no Id."); continue; }

                    if (!CodeRx.IsMatch(id)) invalidIds.Add(id);
                    if (string.IsNullOrWhiteSpace(desc)) emptyDesc.Add(id);

                    if (union.ContainsKey(id)) dupIds.Add(id);
                    else union[id] = desc ?? "";

                    var cat = IcdJson.Category(id);
                    if (catFile.TryGetValue(cat, out var firstFile))
                    {
                        if (firstFile != name && !splitCats.Contains(cat)) splitCats.Add(cat);
                    }
                    else catFile[cat] = name;

                    // Group path must exist and agree with the code.
                    var segs = (gp ?? "").Split('/');
                    bool pathOk = segs.Length is 2 or 3 && segs[0] == "1" && groupPaths.Contains(gp!);
                    if (pathOk)
                    {
                        if (segs.Length == 3)
                            pathOk = id.Contains('.') && segs[2] == cat && segs[1] == Hierarchy.BlockRange(cat) && groupPaths.Contains($"1/{segs[1]}");
                        else
                            pathOk = !id.Contains('.') && segs[1] == Hierarchy.BlockRange(id);
                    }
                    if (!pathOk) badPaths.Add($"{id} -> {gp ?? "(none)"} ({name})");
                    else
                    {
                        used.Add(gp!);
                        if (segs.Length == 3) used.Add($"1/{segs[1]}");
                    }
                }
                foreach (var gp in groupPaths) if (!used.Contains(gp)) unusedGroups.Add($"{gp} ({name})");

                if (root.TryGetProperty("Metadata", out var m) && m.ValueKind == JsonValueKind.Object)
                {
                    int Get(string n) => m.TryGetProperty(n, out var v) && v.TryGetInt32(out var i) ? i : -1;
                    metas.Add((name, Get("PartNumber"), Get("TotalParts"), Get("CodesInPart"), actual, Get("TotalCodes")));
                }
                else filesWithoutMeta++;
            }
            catch (JsonException ex)
            {
                structureErrors++;
                r.Add(Severity.Error, "Structure", $"{name} is not valid JSON: {ex.Message}");
            }
            catch (IOException ex)
            {
                structureErrors++;
                r.Add(Severity.Error, "Structure", $"{name} could not be read: {ex.Message}");
            }
        }

        r.Stat("JSON files checked", readFiles);
        r.Stat("Codes in output", union.Count);
        r.Stat("Groups in output", totalGroups);

        if (dupIds.Count > 0) r.Add(Severity.Error, "Duplicates", $"{dupIds.Count:N0} code Id(s) appear more than once in the output.", dupIds);
        if (invalidIds.Count > 0) r.Add(Severity.Error, "Code format", $"{invalidIds.Count:N0} code Id(s) in the output are not valid ICD-10-CM codes.", invalidIds);
        if (emptyDesc.Count > 0) r.Add(Severity.Error, "Description", $"{emptyDesc.Count:N0} code(s) in the output have an empty description.", emptyDesc);
        if (badPaths.Count > 0) r.Add(Severity.Error, "Hierarchy", $"{badPaths.Count:N0} code(s) have a GroupPath that is missing, has no matching group, or disagrees with the code.", badPaths);
        if (badGroups.Count > 0) r.Add(Severity.Error, "Hierarchy", $"{badGroups.Count:N0} problem(s) in the Groups list.", badGroups);
        if (unusedGroups.Count > 0) r.Add(Severity.Warning, "Hierarchy", $"{unusedGroups.Count:N0} group(s) are not used by any code.", unusedGroups);
        if (splitCats.Count > 0) r.Add(Severity.Error, "Split", $"{splitCats.Count:N0} category(ies) are spread across more than one file (categories should stay together).", splitCats);

        // Split-set consistency.
        if (metas.Count > 0)
        {
            if (filesWithoutMeta > 0)
                r.Add(Severity.Warning, "Split", "Some files carry split Metadata and some do not; they may not belong to the same set.");
            int total = metas[0].Total;
            if (metas.Any(x => x.Total != total))
                r.Add(Severity.Error, "Split", "The files disagree on the total number of parts.");
            if (metas.Count != total)
                r.Add(Severity.Error, "Split", $"The set says it has {total} part(s) but {metas.Count} file(s) were checked.");
            var missing = Enumerable.Range(1, Math.Max(total, 0)).Where(p => metas.All(x => x.Part != p)).ToList();
            if (missing.Count > 0) r.Add(Severity.Error, "Split", $"Missing part number(s): {string.Join(", ", missing)}.");
            var wrongCount = metas.Where(x => x.Declared != x.Actual).Select(x => $"{x.File}: says {x.Declared:N0}, has {x.Actual:N0}").ToList();
            if (wrongCount.Count > 0) r.Add(Severity.Error, "Split", "CodesInPart does not match the file's real code count.", wrongCount);
            if (metas.Any(x => x.TotalCodes != union.Count))
                r.Add(Severity.Error, "Split", $"TotalCodes in the Metadata does not equal the {union.Count:N0} codes actually found across the files.");
            r.Stat("Parts in set", metas.Count);
        }

        // Compare with the source data.
        if (expected != null)
        {
            var missing = expected.Keys.Where(k => !union.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
            var extra = union.Keys.Where(k => !expected.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
            var changed = expected.Where(kv => union.TryGetValue(kv.Key, out var d) && d != kv.Value).Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
            if (missing.Count > 0) r.Add(Severity.Error, "Round trip", $"{missing.Count:N0} source code(s) are missing from the output.", missing);
            if (extra.Count > 0) r.Add(Severity.Error, "Round trip", $"{extra.Count:N0} code(s) in the output were not in the source.", extra);
            if (changed.Count > 0) r.Add(Severity.Error, "Round trip", $"{changed.Count:N0} code(s) have a different description in the output than in the source.", changed);
            if (missing.Count + extra.Count + changed.Count == 0)
                r.Add(Severity.Info, "Round trip", $"The output files match the source exactly ({expected.Count:N0} codes, identical descriptions).");
        }
        else
        {
            // Standalone check of existing files: the code-set rules still apply.
            ValidateCodes(union, r, skipFormatChecks: true);
        }
    }
}

/// <summary>Checks on generated OpenFDA medication files.</summary>
public static class MedicationValidator
{
    static readonly Regex PartRx = new(@"^medications_(\d+)_of_(\d+)\.json$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;

    public static void Verify(IReadOnlyList<string> paths, int? expectedCount, int perFile, bool dedupeExpected, ValidationReport r)
    {
        var generics = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dupGeneric = new List<string>();
        var pairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dupPairs = new List<string>();
        var noIngredients = new List<string>();
        var badItems = new List<string>();
        var spacing = new List<string>();
        var partNumbers = new List<(int Part, int Total)>();
        bool allNamed = true;
        int total = 0, ingTotal = 0, ingUpper = 0, sameName = 0, files = 0;

        foreach (var path in paths)
        {
            var name = Path.GetFileName(path);
            var pm = PartRx.Match(name);
            if (pm.Success) partNumbers.Add((int.Parse(pm.Groups[1].Value), int.Parse(pm.Groups[2].Value))); else allNamed = false;

            try
            {
                using var fs = File.OpenRead(path);
                using var doc = JsonDocument.Parse(fs);
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("Medications", out var arr) || arr.ValueKind != JsonValueKind.Array)
                {
                    r.Add(Severity.Error, "Structure", $"{name}: the Medications array is missing.");
                    continue;
                }
                files++;
                int count = arr.GetArrayLength();
                if (perFile > 0 && count > perFile)
                    r.Add(Severity.Error, "Size", $"{name} has {count:N0} medications, more than the {perFile:N0} per file that was requested.");
                if (count > 1000)
                    r.Add(Severity.Warning, "Import limit", $"{name} has {count:N0} medications. Import and activate is not recommended above 1,000 per file.");

                int idx = 0;
                foreach (var m in arr.EnumerateArray())
                {
                    idx++;
                    total++;
                    var label = Str(m, "DrugLabel");
                    var generic = Str(m, "GenericName");
                    string where = $"{name} #{idx}";
                    if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(generic))
                    {
                        badItems.Add($"{where}: DrugLabel or GenericName is missing or empty");
                        continue;
                    }
                    if (label != label.Trim() || generic != generic.Trim() || label.Any(char.IsControl) || generic.Any(char.IsControl) || label.Contains('�') || generic.Contains('�'))
                        spacing.Add($"{label} / {generic}");
                    if (label.Equals(generic, StringComparison.OrdinalIgnoreCase)) sameName++;

                    if (!generics.Add(generic)) dupGeneric.Add(generic);
                    if (!pairs.Add(label + "\u0001" + generic)) dupPairs.Add($"{label} / {generic}");

                    if (!m.TryGetProperty("Ingredients", out var ing) || ing.ValueKind != JsonValueKind.Array)
                    {
                        badItems.Add($"{where} ({label}): Ingredients is not an array");
                        continue;
                    }
                    int n = 0;
                    foreach (var i in ing.EnumerateArray())
                    {
                        var s = i.ValueKind == JsonValueKind.String ? i.GetString() : null;
                        if (string.IsNullOrWhiteSpace(s)) { badItems.Add($"{where} ({label}): an ingredient is empty or not text"); continue; }
                        n++;
                        ingTotal++;
                        if (s == s.ToUpperInvariant()) ingUpper++;
                    }
                    if (n == 0) noIngredients.Add($"{label} / {generic}");
                }
            }
            catch (JsonException ex)
            {
                r.Add(Severity.Error, "Structure", $"{name} is not valid JSON: {ex.Message}");
            }
        }

        r.Stat("Medication files checked", files);
        r.Stat("Medications", total);
        r.Stat("Ingredients (total)", ingTotal);
        if (ingTotal > 0) r.Stat("Ingredients in ALL CAPS", $"{ingUpper * 100.0 / ingTotal:0}%");
        r.Stat("Label same as generic name", sameName);

        if (expectedCount is int exp && exp != total)
            r.Add(Severity.Error, "Round trip", $"{exp:N0} medications were written but {total:N0} were read back.");
        if (badItems.Count > 0) r.Add(Severity.Error, "Structure", $"{badItems.Count:N0} medication(s) have missing or invalid fields.", badItems);
        if (dupGeneric.Count > 0)
            r.Add(dedupeExpected ? Severity.Error : Severity.Info, "Duplicates",
                dedupeExpected ? $"{dupGeneric.Count:N0} duplicate generic name(s) remain although duplicate removal was on."
                               : $"{dupGeneric.Count:N0} medication(s) share a generic name with another (duplicate removal was off).",
                dupGeneric.Distinct());
        if (dupPairs.Count > 0) r.Add(Severity.Warning, "Duplicates", $"{dupPairs.Count:N0} medication(s) repeat the same label and generic name.", dupPairs);
        if (noIngredients.Count > 0)
            r.Add(Severity.Warning, "Ingredients", $"{noIngredients.Count:N0} medication(s) have no ingredients. The import requires ingredients that exist in the 'Drug ingredients' value set, so these may be rejected.", noIngredients);
        if (spacing.Count > 0)
            r.Add(Severity.Warning, "Text", $"{spacing.Count:N0} medication(s) have leading/trailing spaces, control characters or replacement characters.", spacing);

        if (allNamed && partNumbers.Count > 0)
        {
            int declared = partNumbers[0].Total;
            if (partNumbers.Any(p => p.Total != declared))
                r.Add(Severity.Error, "Files", "The file names disagree on the total number of files.");
            var missing = Enumerable.Range(1, declared).Where(p => partNumbers.All(x => x.Part != p)).ToList();
            if (missing.Count > 0) r.Add(Severity.Error, "Files", $"Missing file number(s): {string.Join(", ", missing)} of {declared}.");
        }
    }
}

/// <summary>Validates existing output files or folders; works out whether they hold ICD-10 codes or medications.</summary>
public static class Validator
{
    public static List<string> ResolveFiles(string pathSpec)
    {
        var files = new List<string>();
        foreach (var p in pathSpec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Directory.Exists(p))
                files.AddRange(Directory.GetFiles(p, "*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
            else if (File.Exists(p)) files.Add(p);
            else throw new FileNotFoundException($"Not found: {p}");
        }
        if (files.Count == 0) throw new FileNotFoundException("No .json files found.");
        return files;
    }

    public static ValidationReport Run(string pathSpec)
    {
        var files = ResolveFiles(pathSpec);
        bool meds = false, icd = false;
        try
        {
            using var fs = File.OpenRead(files[0]);
            using var doc = JsonDocument.Parse(fs);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                meds = doc.RootElement.TryGetProperty("Medications", out _);
                icd = doc.RootElement.TryGetProperty("Codes", out _);
            }
        }
        catch (JsonException) { /* reported below */ }

        if (meds)
        {
            var r = new ValidationReport("OpenFDA medications") { Source = pathSpec };
            MedicationValidator.Verify(files, null, 0, false, r);
            return r;
        }
        if (icd)
        {
            var r = new ValidationReport("ICD-10 JSON") { Source = pathSpec };
            IcdValidator.VerifyJson(files, null, 0, r);
            return r;
        }
        var bad = new ValidationReport("Unknown file") { Source = pathSpec };
        bad.Add(Severity.Error, "Structure", $"{Path.GetFileName(files[0])} is not valid JSON, or contains neither a \"Codes\" nor a \"Medications\" list.");
        return bad;
    }
}
