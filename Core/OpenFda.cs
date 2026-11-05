using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace ICD10Converter.Core;

public sealed class FdaEndpoint
{
    public string Name { get; set; } = "";
    /// <summary>Base URL without paging parameters, e.g. https://api.fda.gov/drug/ndc.json</summary>
    public string Url { get; set; } = "";
    /// <summary>Response layout: "ndc" (flat records) or "drugsfda" (records with a products[] array).</summary>
    public string Format { get; set; } = "ndc";

    public override string ToString() => Name;
}

public sealed record Medication(string DrugLabel, string GenericName, string[] Ingredients);

public sealed class MedicationsFile
{
    public List<Medication> Medications { get; set; } = new();
}

public sealed record FdaOptions(
    FdaEndpoint Endpoint, int RecordCount, int PerFile, bool RemoveDuplicates, bool Debug, string OutputFolder);

public sealed record FdaResult(int Medications, int Files, string Folder, bool Stopped);

public static class OpenFda
{
    const int PageLimit = 1000;
    // OpenFDA refuses skip values above this (HTTP 400), so deeper paging is not possible with skip/limit.
    const int MaxSkip = 25_000;

    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ICD10Converter/2.0");
        return c;
    }

    public static List<FdaEndpoint> DefaultEndpoints() => new()
    {
        new FdaEndpoint { Name = "NDC Directory (recommended)", Url = "https://api.fda.gov/drug/ndc.json", Format = "ndc" },
        new FdaEndpoint { Name = "FDA Approved Drugs", Url = "https://api.fda.gov/drug/drugsfda.json", Format = "drugsfda" },
    };

    static string? First(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var v)) return null;
        return FirstOf(v);
    }

    static string? FirstOf(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in v.EnumerateArray()) return item.ValueKind == JsonValueKind.String ? Nz(item.GetString()) : null;
            return null;
        }
        return v.ValueKind == JsonValueKind.String ? Nz(v.GetString()) : null;
    }

    static string? Nz(string? s) => string.IsNullOrEmpty(s) ? null : s;

    static string[] Ingredients(JsonElement parent)
    {
        var list = new List<string>();
        if (parent.TryGetProperty("active_ingredients", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var ing in arr.EnumerateArray())
            {
                string? n = ing.ValueKind == JsonValueKind.String ? ing.GetString()
                          : ing.ValueKind == JsonValueKind.Object ? First(ing, "name") : null;
                if (!string.IsNullOrEmpty(n)) list.Add(n);
            }
        }
        return list.ToArray();
    }

    static void ParseNdc(JsonElement drug, List<Medication> into)
    {
        var label = First(drug, "brand_name");
        var generic = First(drug, "generic_name");
        label ??= generic;
        generic ??= label;
        if (label == null) return;
        into.Add(new Medication(label, generic!, Ingredients(drug)));
    }

    static void ParseDrugsFda(JsonElement drug, List<Medication> into)
    {
        string? genericBase = drug.TryGetProperty("openfda", out var ofda) ? First(ofda, "generic_name") : null;
        if (!drug.TryGetProperty("products", out var products) || products.ValueKind != JsonValueKind.Array) return;

        foreach (var prod in products.EnumerateArray())
        {
            var label = First(prod, "brand_name") ?? genericBase;
            var generic = genericBase ?? label;
            if (label == null) continue;
            into.Add(new Medication(label, generic!, Ingredients(prod)));
        }
    }

    static string BuildUri(string baseUrl, int limit, int skip) =>
        $"{baseUrl}{(baseUrl.Contains('?') ? '&' : '?')}limit={limit}&skip={skip}";

    static void DumpFirst(JsonElement sample, Action<string> log)
    {
        log("  [DEBUG] Raw properties of first result:");
        if (sample.ValueKind != JsonValueKind.Object) { log("    " + sample.GetRawText()); return; }
        foreach (var p in sample.EnumerateObject())
        {
            string text = p.Value.ValueKind switch
            {
                JsonValueKind.Null => "(null)",
                JsonValueKind.Array => $"(array {p.Value.GetArrayLength()} items): {Trunc(p.Value.GetArrayLength() > 0 ? p.Value[0].GetRawText() : "")}",
                JsonValueKind.Object => $"(object): {Trunc(p.Value.GetRawText())}",
                _ => Trunc(p.Value.ToString()),
            };
            log($"    {p.Name} : {text}");
        }
        static string Trunc(string s) => (s = s.Replace("\r", " ").Replace("\n", " ").Trim()).Length > 300 ? s[..300] + "..." : s;
    }

    public static async Task<FdaResult> RunAsync(
        FdaOptions o, Action<string> log, Action<int, string> progress, CancellationToken ct)
    {
        log("=========================================");
        log("Starting OpenFDA fetch");
        log($"Endpoint     : {o.Endpoint.Name} ({o.Endpoint.Format})");
        log($"API URL      : {o.Endpoint.Url}");
        log($"Target count : {o.RecordCount}");
        log($"Per-file size: {o.PerFile}");
        log($"Remove dupes : {o.RemoveDuplicates}");
        log($"Output       : {o.OutputFolder}");

        if (!Uri.TryCreate(o.Endpoint.Url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException($"Not a valid http(s) URL: {o.Endpoint.Url}");

        bool isFdaFormat = o.Endpoint.Format.Equals("drugsfda", StringComparison.OrdinalIgnoreCase);
        var meds = new List<Medication>();
        int totalFetches = (int)Math.Ceiling(o.RecordCount / (double)PageLimit);
        bool debugDumped = false, stopped = false;

        if (o.RecordCount > MaxSkip + PageLimit)
            log($"Note: OpenFDA only allows paging up to ~{MaxSkip + PageLimit:N0} records; the fetch will stop there.");

        for (int skip = 0; skip < o.RecordCount; skip += PageLimit)
        {
            if (ct.IsCancellationRequested) { stopped = true; break; }

            int batch = skip / PageLimit + 1;
            int fetchNum = Math.Min(PageLimit, o.RecordCount - skip);
            progress(skip * 75 / o.RecordCount, $"Fetching batch {batch} of {totalFetches}");
            log($"Fetching batch {batch} of {totalFetches} (limit={fetchNum} skip={skip})");

            string? body = null;
            bool endOfData = false;
            for (int attempt = 1; attempt <= 3 && body == null && !endOfData; attempt++)
            {
                try
                {
                    using var resp = await Http.GetAsync(BuildUri(o.Endpoint.Url, fetchNum, skip), ct);
                    if (resp.StatusCode == HttpStatusCode.NotFound)
                    {
                        log("  No more results from the API.");
                        endOfData = true;
                    }
                    else if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        log($"  Rate-limited - waiting 60 s (attempt {attempt} of 3)...");
                        await Task.Delay(TimeSpan.FromSeconds(60), ct);
                    }
                    else if (resp.StatusCode == HttpStatusCode.BadRequest)
                    {
                        log($"  API rejected the request (HTTP 400). OpenFDA does not allow skip values above {MaxSkip:N0}; stopping fetch.");
                        endOfData = true;
                    }
                    else
                    {
                        resp.EnsureSuccessStatusCode();
                        body = await resp.Content.ReadAsStringAsync(ct);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { stopped = true; break; }
                catch (Exception ex)
                {
                    log($"  ERROR batch {batch}: {ex.Message}");
                    if (attempt < 3) await Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
                }
            }
            if (stopped || endOfData) break;
            if (body == null) continue;

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array || results.GetArrayLength() == 0)
            {
                log("  No .results in this response - skipping.");
                continue;
            }
            log($"  Received {results.GetArrayLength()} results");

            if (o.Debug && !debugDumped)
            {
                debugDumped = true;
                DumpFirst(results[0], log);
            }

            foreach (var drug in results.EnumerateArray())
            {
                if (isFdaFormat) ParseDrugsFda(drug, meds); else ParseNdc(drug, meds);
            }
            log($"  Running total: {meds.Count}");

            try { await Task.Delay(300, ct); } catch (OperationCanceledException) { stopped = true; break; }
        }

        if (stopped) log("Stopped by user - processing what was collected so far.");
        log($"Fetch phase done - {meds.Count} raw records collected");

        if (o.RemoveDuplicates && meds.Count > 0)
        {
            log("Removing duplicates by GenericName...");
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            meds = meds.Where(m => seen.Add(m.GenericName)).ToList();
            log($"After de-dup: {meds.Count} unique medications");
        }

        if (meds.Count == 0)
        {
            log("WARNING: No medications were collected.");
            return new FdaResult(0, 0, o.OutputFolder, stopped);
        }

        Directory.CreateDirectory(o.OutputFolder);
        int totalFiles = (int)Math.Ceiling(meds.Count / (double)o.PerFile);
        var opts = new JsonSerializerOptions { WriteIndented = true, IndentSize = 4, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

        for (int i = 0; i < totalFiles; i++)
        {
            var chunk = meds.Skip(i * o.PerFile).Take(o.PerFile).ToList();
            string name = $"medications_{i + 1}_of_{totalFiles}.json";
            File.WriteAllBytes(Path.Combine(o.OutputFolder, name),
                JsonSerializer.SerializeToUtf8Bytes(new MedicationsFile { Medications = chunk }, opts));
            log($"Wrote {chunk.Count} medications to {name}");
            progress(75 + (i + 1) * 25 / totalFiles, $"Writing file {i + 1} of {totalFiles}");
        }

        progress(100, "Done!");
        log("=========================================");
        log($"Finished - {meds.Count} medications across {totalFiles} file(s)");
        log($"Output : {o.OutputFolder}");
        return new FdaResult(meds.Count, totalFiles, o.OutputFolder, stopped);
    }
}
