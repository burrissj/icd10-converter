using System.Diagnostics;

namespace ICD10Converter.Core;

public sealed record ConvertOptions(
    string Input, string? Addenda, string Output, FileKind Kind,
    bool Pretty, bool Split, long MaxBytes, string Version, bool KeepHistory, bool WriteReport = true);

public sealed record ConvertResult(
    string OpenPath, bool IsFolder, int CodeCount, int Parts, TimeSpan Elapsed,
    ValidationReport Report, string? ReportPath);

public static class Pipeline
{
    public static ConvertResult Run(ConvertOptions o, Action<string> log)
    {
        var sw = Stopwatch.StartNew();
        var issues = new Issues();
        var report = new ValidationReport("ICD-10 conversion") { Source = o.Input };

        var kind = o.Kind == FileKind.Auto ? IcdParser.Detect(o.Input) : o.Kind;
        log($"Reading {kind} file: {o.Input}");
        var codes = IcdParser.Read(o.Input, kind, issues);
        log($"Loaded {codes.Count:N0} codes");
        report.Stat("Input file type", kind.ToString());
        report.Stat("Version label", o.Version);

        if (!string.IsNullOrWhiteSpace(o.Addenda))
        {
            log($"Applying addenda: {o.Addenda}");
            var addenda = IcdParser.ReadAddenda(o.Addenda, issues);
            int before = codes.Count;
            codes = IcdParser.ApplyAddenda(codes, addenda, issues, out var added, out var modified, out var deleted);
            log($"Addenda applied: {added} added, {modified} modified, {deleted} deleted -> {codes.Count:N0} codes");
            report.Stat("Addenda file", Path.GetFileName(o.Addenda));
            report.Stat("Addenda: added / modified / deleted", $"{added} / {modified} / {deleted}");
            report.Stat("Codes before addenda", before);
        }

        if (issues.Count > 0)
        {
            log($"{issues.Count} warning(s) while reading:");
            foreach (var s in issues.Samples) log("  " + s);
            if (issues.Count > issues.Samples.Count) log($"  ... and {issues.Count - issues.Samples.Count} more");
            report.Add(Severity.Warning, "Parsing",
                $"{issues.Count:N0} input line(s) were skipped, duplicated or could not be applied.", issues.Samples);
        }

        if (codes.Count == 0)
            throw new InvalidDataException("No codes were found in the input file. Check the file type setting.");

        // 1) Checks on the data before it is written.
        IcdValidator.ValidateCodes(codes, report);
        CompareWithPrevious(codes, report);

        // 2) Write.
        string openPath;
        bool isFolder;
        int parts = 1;
        List<string> written;

        if (o.Split)
        {
            log($"Splitting (max {o.MaxBytes:N0} bytes = {o.MaxBytes / 1048576.0:0.00} MB per file)...");
            var r = Splitter.Split(o.Output, codes, o.MaxBytes, o.Pretty, o.Version, log);
            openPath = r.Directory;
            isFolder = true;
            parts = r.Parts;
            written = Directory.GetFiles(r.Directory, "*-Part-*-of-*.json").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        }
        else
        {
            log("Writing JSON...");
            var doc = IcdJson.BuildDocument(codes, o.Version);
            var bytes = IcdJson.Serialize(doc, o.Pretty);
            IcdJson.WriteFile(o.Output, bytes);
            log($"Output file created: {bytes.Length / 1048576.0:0.00} MB, {doc.Codes.Count:N0} codes, {doc.Groups.Count:N0} groups");
            openPath = o.Output;
            isFolder = false;
            written = new List<string> { o.Output };
        }

        // 3) Read the written files back and verify them against the source.
        log("Verifying the output files...");
        IcdValidator.VerifyJson(written, codes, o.Split ? o.MaxBytes : 0, report);

        string? reportPath = null;
        if (o.WriteReport)
        {
            try
            {
                var baseDir = Path.GetDirectoryName(Path.GetFullPath(o.Output))!;
                reportPath = Path.Combine(baseDir, Path.GetFileNameWithoutExtension(o.Output) + "-validation-report.txt");
                report.Save(reportPath);
            }
            catch (Exception ex)
            {
                log("Could not write the validation report: " + ex.Message);
                reportPath = null;
            }
        }
        report.LogSummary(log);
        if (reportPath != null) log($"Validation report: {reportPath}");

        if (o.KeepHistory)
        {
            try
            {
                var snap = IcdJson.Serialize(IcdJson.BuildDocument(codes, o.Version), false);
                IcdJson.WriteFile(HistorySnapshotPath(o.Version), snap);
                log("Saved a snapshot to history (available in the Compare tab).");
            }
            catch (Exception ex) { log("Could not save history snapshot: " + ex.Message); }
        }

        sw.Stop();
        return new ConvertResult(openPath, isFolder, codes.Count, parts, sw.Elapsed, report, reportPath);
    }

    /// <summary>Compares with the newest history snapshot (taken before this run's snapshot is saved).</summary>
    static void CompareWithPrevious(Dictionary<string, string> codes, ValidationReport report)
    {
        try
        {
            if (!Directory.Exists(AppPaths.History)) return;
            var prevPath = Directory.GetFiles(AppPaths.History, "*.json")
                .OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
            if (prevPath == null) return;

            var prev = CodeSetLoader.Load(prevPath);
            var diff = CodeDiff.Compare(prev, codes);
            string name = Path.GetFileNameWithoutExtension(prevPath);
            report.Add(Severity.Info, "Previous version",
                $"Compared with the last saved snapshot ({name}, {prev.Count:N0} codes): +{diff.Added:N0} added, -{diff.Removed:N0} removed, {diff.Changed:N0} changed.");
            if (prev.Count > 0 && diff.Removed * 50L > prev.Count)
                report.Add(Severity.Warning, "Previous version",
                    $"{diff.Removed:N0} codes ({diff.Removed * 100.0 / prev.Count:0.0}%) in the previous snapshot are missing from this conversion. Check that the input file is complete.",
                    diff.Rows.Where(x => x.Status == "Removed").Select(x => x.Code));
        }
        catch { /* the comparison is a bonus; never block a conversion on it */ }
    }

    static string HistorySnapshotPath(string version)
    {
        var safe = string.Concat(version.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(AppPaths.History, $"{safe}_{DateTime.Now:yyyyMMdd-HHmmss}.json");
    }
}
