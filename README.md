# ICD-10 Converter

A single-file Windows desktop app that:

- **Converts** CMS ICD-10-CM text files (codes or order files, optional addenda) to hierarchical JSON, with optional splitting into parts under a size limit.
- **Downloads** the CMS release files from URLs you configure.
- **Compares** two versions of the code set and lists added, removed and changed codes (exportable to CSV).
- **Fetches medications** from the free [OpenFDA](https://open.fda.gov/) API and writes them as `Medications` JSON files (`DrugLabel`, `GenericName`, `Ingredients`).

It is a faster, packaged rewrite of the original PowerShell scripts.

## Run it

Download `ICD10Converter.exe` and double-click it. It is self-contained: no install, no .NET, no PowerShell needed. Windows 10/11, 64-bit.

## Tabs

| Tab | What it does |
| --- | --- |
| Convert | Pick a codes/order file (or drop it on the box), optionally an addenda file, choose the output and options, click Convert. |
| Compare versions | Choose an older and a newer version (converted `.json`, a folder of split parts, or CMS `.txt`), then Compare. Every conversion is saved to a history list so older releases are one click away. |
| Validate | Re-checks any generated ICD-10 JSON (single file, several files or a folder of split parts) or OpenFDA medication files, and lets you save the report. |
| FDA medications | Choose an endpoint, how many medications, and an output folder, then Fetch & process. Stop writes whatever was collected so far. |
| Settings | Edit the CMS download URLs (use `{year}` as a placeholder), the release year, the OpenFDA endpoint URLs, default folders and conversion defaults. |

Settings and history are stored in `%AppData%\ICD10Converter`.

## Validation

Every conversion and every OpenFDA fetch is checked automatically. The output files are read back from disk and verified, and a plain-text report (`<output>-validation-report.txt`, or `medications-validation-report.txt`) is saved next to them. The headline is **PASSED**, **PASSED with warnings** or **FAILED**.

ICD-10 checks include:

- every code matches the ICD-10-CM format; no empty descriptions, duplicates, or encoding problems
- every code in the source appears in the output with an identical description, and nothing extra was added (round trip)
- every `GroupPath` points to an existing group that agrees with the code's block and category; no empty or unused groups
- split files: every part is under the size limit, no part is missing, categories are not split across parts, and the `Metadata` counts are correct
- chapter coverage, total size, and a comparison with your last saved snapshot (a large drop is flagged)

Medication checks include required fields, duplicates, medications with no ingredients (the import may reject these), file count and per-file size, and a round-trip count.

Passing validation means the file is structurally consistent and matches the source. It does not prove the source data itself is clinically correct.

## Output formats

ICD-10 conversion:

```json
{
    "Name": "ICD-10-CM",
    "Description": "...",
    "VersionDescription": "v2026",
    "Groups":  [ { "Path": "1/A00-A09", "Descriptions": ["...", "..."] } ],
    "Codes":   [ { "Id": "A00.0", "GroupPath": "1/A00-A09/A00", "Description": "..." } ],
    "ExtensionGroups": []
}
```

Medications:

```json
{
    "Medications": [
        { "DrugLabel": "Brand", "GenericName": "Generic", "Ingredients": ["INGREDIENT"] }
    ]
}
```

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet publish ICD10Converter.csproj -c Release -o publish
```

or run `build.cmd`. The result is one self-contained `publish\ICD10Converter.exe`.

## Notes and limits

- OpenFDA only allows paging up to about 26,000 records per endpoint.
- The downloaded CMS and OpenFDA data are not part of this repository and are subject to their own terms. Check them before redistributing the data itself.
- This tool is not affiliated with CMS, the FDA or any vendor, and is not a medical device. Verify output before using it in any clinical or production system.

## License

MIT. See [LICENSE](LICENSE). The self-contained exe bundles the .NET runtime, which is also MIT-licensed by the .NET Foundation and contributors.
