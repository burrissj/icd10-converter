using System.Text;
using System.Text.RegularExpressions;

namespace ICD10Converter.Core;

public enum FileKind { Auto, Codes, Order }

/// <summary>Collects parse warnings without flooding the log.</summary>
public sealed class Issues
{
    readonly List<string> _samples = new();
    public int Count { get; private set; }
    public IReadOnlyList<string> Samples => _samples;
    public void Add(string message)
    {
        Count++;
        if (_samples.Count < 10) _samples.Add(message);
    }
}

public sealed class Addenda
{
    public List<string> ToDelete { get; } = new();
    public Dictionary<string, string> ToAdd { get; } = new(StringComparer.Ordinal);
    /// <summary>"Revise to:" lines: new description for an existing code.</summary>
    public Dictionary<string, string> ToRevise { get; } = new(StringComparer.Ordinal);
}

public static class IcdParser
{
    static readonly Regex OrderRx = new(@"^(\d+)\s+(\S+)\s+(\d+)\s+(.+)$", RegexOptions.Compiled);
    static readonly Regex OrderSplitRx = new(@"^(.+?)\s{2,}(.+)$", RegexOptions.Compiled);
    static readonly Regex AddendaRx = new(@"^(Delete|Add|Revise from|Revise to):\s+(\S+)\s+(.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // Summary lines at the end of CMS addenda files, e.g. "Codes", "74879 codes in icd10cm_order_2027.txt", "190 additions".
    static readonly Regex AddendaFooterRx = new(@"^(Codes|\d+\s+(codes\b.*|additions|deletions|revisions))$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool LooksLikeOrder(string path) =>
        Path.GetFileName(path).Contains("order", StringComparison.OrdinalIgnoreCase);

    public static FileKind Detect(string path) => LooksLikeOrder(path) ? FileKind.Order : FileKind.Codes;

    /// <summary>Reads a text file as UTF-8 (BOM tolerated); falls back to Latin-1 for legacy files.</summary>
    static string[] ReadLines(string path)
    {
        var bytes = File.ReadAllBytes(path);
        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { text = Encoding.Latin1.GetString(bytes); }
        if (text.Length > 0 && text[0] == '﻿') text = text.Substring(1);
        return text.Split('\n');
    }

    /// <summary>CMS ships A000 / A0100; the output format wants A00.0 / A01.00.</summary>
    static string NormalizeCode(string code) =>
        code.Length > 3 && code.IndexOf('.') < 0 ? code.Substring(0, 3) + "." + code.Substring(3) : code;

    public static Dictionary<string, string> Read(string path, FileKind kind, Issues issues)
    {
        if (kind == FileKind.Auto) kind = Detect(path);
        return kind == FileKind.Order ? ReadOrder(path, issues) : ReadCodes(path, issues);
    }

    /// <summary>"CODE description" file. 3-character category rows are generated when missing.</summary>
    public static Dictionary<string, string> ReadCodes(string path, Issues issues)
    {
        var codes = new Dictionary<string, string>(80_000, StringComparer.Ordinal);
        var categories = new HashSet<string>(StringComparer.Ordinal);
        var lines = ReadLines(path);

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;

            int sp = IndexOfWhitespace(line);
            if (sp <= 0)
            {
                issues.Add($"Unable to parse line {i + 1}: {line}");
                continue;
            }
            var desc = line.Substring(sp + 1).Trim();
            if (desc.Length == 0)
            {
                issues.Add($"Unable to parse line {i + 1}: {line}");
                continue;
            }

            var code = NormalizeCode(line.Substring(0, sp));
            if (code.Length >= 3) categories.Add(code.Substring(0, 3));

            if (!codes.TryAdd(code, desc))
                issues.Add($"Duplicate code found at line {i + 1}: {code}");
        }

        foreach (var cat in categories)
            codes.TryAdd(cat, Hierarchy.CategoryDescription(cat));

        return codes;
    }

    /// <summary>CMS order file. Only billable (flag = 1) rows are kept.</summary>
    public static Dictionary<string, string> ReadOrder(string path, Issues issues)
    {
        var codes = new Dictionary<string, string>(80_000, StringComparer.Ordinal);
        var lines = ReadLines(path);

        for (int i = 0; i < lines.Length; i++)
        {
            var raw = lines[i].TrimEnd('\r');
            var line = raw.Trim();
            if (line.Length == 0) continue;

            var m = OrderRx.Match(line);
            if (!m.Success)
            {
                issues.Add($"Unable to parse line {i + 1}: {line}");
                continue;
            }

            if (m.Groups[3].Value != "1") continue;
            var code = NormalizeCode(m.Groups[2].Value);

            string longDesc;
            if (IsFixedWidthOrderLine(raw))
            {
                // Short desc is a padded 60-char column; the long description starts at col 78.
                longDesc = raw.Substring(77).Trim();
            }
            else
            {
                var remainder = m.Groups[4].Value;
                var sm = OrderSplitRx.Match(remainder);
                longDesc = sm.Success ? sm.Groups[2].Value.Trim() : remainder.Trim();
            }
            if (longDesc.Length == 0) longDesc = m.Groups[4].Value.Trim();

            codes.TryAdd(code, longDesc);
        }
        return codes;
    }

    static bool IsFixedWidthOrderLine(string raw)
    {
        if (raw.Length <= 77 || raw[5] != ' ' || raw[13] != ' ' || raw[15] != ' ' || raw[76] != ' ') return false;
        for (int i = 0; i < 5; i++) if (!char.IsAsciiDigit(raw[i])) return false;
        return char.IsAsciiDigit(raw[14]);
    }

    public static Addenda ReadAddenda(string path, Issues issues)
    {
        var result = new Addenda();
        var lines = ReadLines(path);
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (line.Length == 0) continue;

            var m = AddendaRx.Match(line);
            if (!m.Success)
            {
                if (!AddendaFooterRx.IsMatch(line)) issues.Add($"Unable to parse addenda line {i + 1}: {line}");
                continue;
            }
            var code = NormalizeCode(m.Groups[2].Value);
            var action = m.Groups[1].Value;
            if (action.Equals("Delete", StringComparison.OrdinalIgnoreCase)) result.ToDelete.Add(code);
            else if (action.Equals("Add", StringComparison.OrdinalIgnoreCase)) result.ToAdd[code] = m.Groups[3].Value.Trim();
            else if (action.Equals("Revise to", StringComparison.OrdinalIgnoreCase)) result.ToRevise[code] = m.Groups[3].Value.Trim();
            // "Revise from" only documents the old text; nothing to apply.
        }
        return result;
    }

    public static Dictionary<string, string> ApplyAddenda(
        Dictionary<string, string> codes, Addenda addenda, Issues issues,
        out int added, out int modified, out int deleted, out int alreadyPresent, out int alreadyAbsent)
    {
        var updated = new Dictionary<string, string>(codes, StringComparer.Ordinal);
        added = modified = deleted = alreadyPresent = alreadyAbsent = 0;

        // A same-year codes file already contains its own addenda, so deletions of codes that are
        // absent and additions of codes that are present with the same text are counted, not warned about.
        foreach (var code in addenda.ToDelete)
        {
            if (updated.Remove(code)) deleted++; else alreadyAbsent++;
        }
        foreach (var (code, desc) in addenda.ToAdd)
        {
            if (!updated.TryGetValue(code, out var existing)) { added++; updated[code] = desc; }
            else if (existing != desc) { modified++; updated[code] = desc; }
            else alreadyPresent++;
        }
        foreach (var (code, desc) in addenda.ToRevise)
        {
            if (!updated.TryGetValue(code, out var existing)) issues.Add($"Revision for a code that is not in the file: {code}");
            else if (existing != desc) { modified++; updated[code] = desc; }
            else alreadyPresent++;
        }
        return updated;
    }

    static int IndexOfWhitespace(string s)
    {
        for (int i = 0; i < s.Length; i++) if (char.IsWhiteSpace(s[i])) return i;
        return -1;
    }
}
