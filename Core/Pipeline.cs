using System.Diagnostics;

namespace ICD10Converter.Core;

public sealed record ConvertOptions(
    string Input, string? Addenda, string Output, FileKind Kind,
    bool Pretty, bool Split, long MaxBytes, string Version, bool KeepHistory);

public sealed record ConvertResult(string OpenPath, bool IsFolder, int CodeCount, int Parts, TimeSpan Elapsed);

public static class Pipeline
{
    public static ConvertResult Run(ConvertOptions o, Action<string> log)
    {
        var sw = Stopwatch.StartNew();
        var issues = new Issues();

        var kind = o.Kind == FileKind.Auto ? IcdParser.Detect(o.Input) : o.Kind;
        log($"Reading {kind} file: {o.Input}");
        var codes = IcdParser.Read(o.Input, kind, issues);
        log($"Loaded {codes.Count:N0} codes");

        if (!string.IsNullOrWhiteSpace(o.Addenda))
        {
            log($"Applying addenda: {o.Addenda}");
            var addenda = IcdParser.ReadAddenda(o.Addenda, issues);
            codes = IcdParser.ApplyAddenda(codes, addenda, issues, out var added, out var modified, out var deleted);
            log($"Addenda applied: {added} added, {modified} modified, {deleted} deleted -> {codes.Count:N0} codes");
        }

        if (issues.Count > 0)
        {
            log($"{issues.Count} warning(s) while reading:");
            foreach (var s in issues.Samples) log("  " + s);
            if (issues.Count > issues.Samples.Count) log($"  ... and {issues.Count - issues.Samples.Count} more");
        }

        if (codes.Count == 0)
            throw new InvalidDataException("No codes were found in the input file. Check the file type setting.");

        string openPath;
        bool isFolder;
        int parts = 1;

        if (o.Split)
        {
            log($"Splitting (max {o.MaxBytes:N0} bytes = {o.MaxBytes / 1048576.0:0.00} MB per file)...");
            var r = Splitter.Split(o.Output, codes, o.MaxBytes, o.Pretty, o.Version, log);
            openPath = r.Directory;
            isFolder = true;
            parts = r.Parts;
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
        }

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
        return new ConvertResult(openPath, isFolder, codes.Count, parts, sw.Elapsed);
    }

    static string HistorySnapshotPath(string version)
    {
        var safe = string.Concat(version.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(AppPaths.History, $"{safe}_{DateTime.Now:yyyyMMdd-HHmmss}.json");
    }
}
