using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;

namespace ICD10Converter.Core;

public sealed record DownloadResult(string ExtractPath, int Succeeded, int Failed);

public static class Downloader
{
    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        // cms.gov rejects requests that don't look like a browser.
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        return c;
    }

    public static async Task<DownloadResult> RunAsync(
        AppSettings settings, string folder, Action<string> log, Action<string> status, CancellationToken ct = default)
    {
        int year = settings.Year;
        var extractPath = Path.Combine(folder, $"icd10cm-{year}");
        Directory.CreateDirectory(extractPath);

        int ok = 0, failed = 0;
        var active = settings.Sources.Where(s => s.Enabled).ToList();
        if (active.Count == 0) throw new InvalidOperationException("No download sources are enabled. Enable one on the Settings tab.");

        foreach (var src in active)
        {
            string label = string.IsNullOrWhiteSpace(src.Name) ? src.Url : src.Name;
            try
            {
                var url = src.ExpandUrl(year);
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
                    throw new InvalidOperationException($"Not a valid http(s) URL: {url}");

                log($"Downloading: {label}");
                log($"  {url}");
                var dest = Path.Combine(folder, src.ExpandFileName(year));

                using (var resp = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    resp.EnsureSuccessStatusCode();
                    long? total = resp.Content.Headers.ContentLength;
                    await using var input = await resp.Content.ReadAsStreamAsync(ct);
                    await using var output = File.Create(dest);
                    var buf = new byte[81920];
                    long read = 0;
                    int n;
                    while ((n = await input.ReadAsync(buf, ct)) > 0)
                    {
                        await output.WriteAsync(buf.AsMemory(0, n), ct);
                        read += n;
                        status(total is > 0
                            ? $"Downloading {label}: {read / 1048576.0:0.0} / {total / 1048576.0:0.0} MB"
                            : $"Downloading {label}: {read / 1048576.0:0.0} MB");
                    }
                }
                log($"  Saved {Path.GetFileName(dest)}");

                if (dest.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    log("  Extracting...");
                    ZipFile.ExtractToDirectory(dest, extractPath, overwriteFiles: true);
                    File.Delete(dest);
                }
                else
                {
                    File.Move(dest, Path.Combine(extractPath, Path.GetFileName(dest)), overwrite: true);
                }
                ok++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                failed++;
                log($"  ERROR downloading {label}: {ex.Message}");
            }
        }
        return new DownloadResult(extractPath, ok, failed);
    }

    /// <summary>Finds the codes / addenda text files in an extracted folder.</summary>
    public static (string? Codes, string? Addenda) FindFiles(string folder)
    {
        var txt = Directory.EnumerateFiles(folder, "*.txt", SearchOption.AllDirectories).ToList();
        bool Has(string f, string w) => Path.GetFileName(f).Contains(w, StringComparison.OrdinalIgnoreCase);

        var codes = txt.FirstOrDefault(f => Has(f, "codes") && !Has(f, "addenda") && !Has(f, "order"))
                    ?? txt.FirstOrDefault(f => Has(f, "order"));
        var addenda = txt.FirstOrDefault(f => Has(f, "addenda"));
        return (codes, addenda);
    }
}
