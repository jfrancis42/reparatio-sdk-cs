# reparatio — C# SDK

> **Alpha software.** The API surface may change without notice between versions. Pin to a specific version in production.

C# client library for the [Reparatio](https://reparatio.app) data conversion API.

Inspect, convert, merge, append, and query CSV, Excel, Parquet, JSON, GeoJSON, SQLite, and 30+ other formats with a single method call.

**See also:** [reparatio-cli](https://github.com/jfrancis42/reparatio-cli) (command-line tool) · [reparatio-mcp](https://github.com/jfrancis42/reparatio-mcp) (MCP server for AI assistants)

---

## Requirements

- .NET Framework 4.5+ **or** Mono 4.0+
- `System.Net.Http` (included in .NET 4.5+ and mono-complete)
- `System.Web.Extensions` (included in mono-complete; `System.Web.Extensions.dll` on .NET)

---

## Quick start

```csharp
using Reparatio;
using System.IO;

using (var client = new ReparatioClient("rp_YOUR_KEY"))
{
    // Inspect a file
    var info = await client.InspectAsync("sales.csv");
    Console.WriteLine(info["detected_encoding"]);

    // Convert to Parquet
    var result = await client.ConvertAsync("sales.csv", "parquet");
    File.WriteAllBytes(result.Filename, result.Content);

    // SQL query
    var q = await client.QueryAsync(
        "events.parquet",
        "SELECT region, SUM(revenue) FROM data GROUP BY region ORDER BY 2 DESC");
    File.WriteAllBytes(q.Filename, q.Content);
}
```

---

## Authentication

The API key can be supplied in two ways, in order of precedence:

1. Passed directly: `new ReparatioClient("EXAMPLE-EXAMPLE-EXAMPLE")`
2. Environment variable: `REPARATIO_API_KEY=EXAMPLE-EXAMPLE-EXAMPLE`

Get a key at [reparatio.app](https://reparatio.app) (Professional plan — $79/mo). API access requires the Professional plan; the Standard plan ($29/mo) covers web UI only.

---

## IDisposable

`ReparatioClient` implements `IDisposable` and holds an `HttpClient`. Use it in a `using` block:

```csharp
using (var client = new ReparatioClient("EXAMPLE-EXAMPLE-EXAMPLE"))
{
    var result = await client.ConvertAsync("data.csv", "parquet");
}
```

---

## Reference

### `ReparatioClient` constructors

```csharp
new ReparatioClient()                                         // key from env
new ReparatioClient(string apiKey)                            // 120-second timeout
new ReparatioClient(string apiKey, string baseUrl, TimeSpan timeout)
```

---

### `FormatsAsync() → Task<Dictionary<string, object>>`

List supported input/output formats. No API key required.

```csharp
var f = await client.FormatsAsync();
// f["input"]  → ArrayList of format strings
// f["output"] → ArrayList of format strings
```

---

### `MeAsync() → Task<Dictionary<string, object>>`

Return subscription and usage details for the current API key.

```csharp
var me = await client.MeAsync();
Console.WriteLine(me["email"]);
Console.WriteLine(me["plan"]);
```

---

### `InspectAsync(filePath, ...) → Task<Dictionary<string, object>>`

Detect encoding, count rows, list column types, and return a data preview. No API key required.

```csharp
var info = await client.InspectAsync(
    "data.csv",
    previewRows: 20,
    fixEncoding: true);
Console.WriteLine(info["detected_encoding"]);
Console.WriteLine(info["rows_total"]);
```

**Parameters:**

| Parameter | Type | Default | Description |
|---|---|---|---|
| `filePath` | string | required | Path to the file |
| `noHeader` | bool | `false` | Treat first row as data |
| `fixEncoding` | bool | `true` | Auto-detect and repair encoding |
| `previewRows` | int | `8` | Number of preview rows (1–100) |
| `delimiter` | string | `""` | Custom delimiter (auto-detected if blank) |
| `sheet` | string | `""` | Sheet name for Excel, ODS, or SQLite |
| `ct` | CancellationToken | default | Cancellation token |

---

### `ConvertAsync(filePath, targetFormat, ...) → Task<ReparatioResult>`

Convert a file from any supported input format to any supported output format.
Requires a Professional plan key.

```csharp
// Basic conversion
var result = await client.ConvertAsync("sales.csv", "parquet");
File.WriteAllBytes(result.Filename, result.Content);

// With options
var result = await client.ConvertAsync(
    "big.csv", "csv",
    selectColumns: new[] { "date", "region", "revenue" },
    deduplicate: true,
    nullValues: new[] { "NULL", "N/A", "-" });
```

**Parameters:**

| Parameter | Type | Default | Description |
|---|---|---|---|
| `filePath` | string | required | Path to the file |
| `targetFormat` | string | required | Output format (see [formats](#supported-formats)) |
| `noHeader` | bool | `false` | Treat first row as data |
| `fixEncoding` | bool | `true` | Repair encoding |
| `delimiter` | string | `""` | Custom delimiter for CSV-like input |
| `sheet` | string | `""` | Sheet or table to read |
| `selectColumns` | IList\<string\> | `null` | Columns to include |
| `deduplicate` | bool | `false` | Remove duplicate rows |
| `sampleN` | int | `0` | Random sample of N rows |
| `sampleFrac` | double | `0.0` | Random sample fraction |
| `geometryColumn` | string | `"geometry"` | WKT geometry column for GeoJSON output |
| `castColumnsJson` | string | `"{}"` | Column type overrides as JSON string |
| `nullValues` | IList\<string\> | `null` | Strings to treat as null at load time |
| `encodingOverride` | string | `null` | Force a specific encoding (e.g. `"cp037"` for EBCDIC US) |
| `ct` | CancellationToken | default | Cancellation token |

---

### `BatchConvertAsync(zipPath, targetFormat, ...) → Task<ReparatioResult>`

Convert every file inside a ZIP archive to a common format.
Returns a ZIP archive in `result.Content`. Files that fail to parse are skipped;
their names and errors are URL-decoded in `result.Warning` as a JSON array.
Requires a Professional plan key.

```csharp
var result = await client.BatchConvertAsync("reports.zip", "parquet");
File.WriteAllBytes("converted.zip", result.Content);
if (result.Warning != null)
    Console.WriteLine("Skipped files: " + result.Warning);
```

---

### `MergeAsync(filePath1, filePath2, operation, targetFormat, ...) → Task<ReparatioResult>`

Merge or join two files.
Requires a Professional plan key.

```csharp
var result = await client.MergeAsync(
    "orders.csv", "customers.xlsx",
    "left", "parquet",
    joinOn: "customer_id");
File.WriteAllBytes(result.Filename, result.Content);
```

**Operations:**

| Value | Behaviour |
|---|---|
| `append` | Stack all rows from both files; missing columns filled with null |
| `left` | All rows from file 1; matching columns from file 2 |
| `right` | All rows from file 2; matching columns from file 1 |
| `outer` | All rows from both files; nulls where no match |
| `inner` | Only rows present in both files |

---

### `AppendAsync(filePaths, targetFormat, ...) → Task<ReparatioResult>`

Stack rows from two or more files vertically.
Column mismatches are handled gracefully — missing values are filled with null.
Requires a Professional plan key.

```csharp
var paths = Directory.GetFiles("monthly/", "*.csv");
var result = await client.AppendAsync(paths, "parquet");
File.WriteAllBytes("all_months.parquet", result.Content);
```

Throws `ArgumentException` if fewer than 2 files are provided.

---

### `QueryAsync(filePath, sql, ...) → Task<ReparatioResult>`

Run a SQL query against a file. The file is loaded as a table named `data`.
Requires a Professional plan key.

```csharp
var result = await client.QueryAsync(
    "events.parquet",
    "SELECT region, SUM(revenue) AS total FROM data GROUP BY region ORDER BY total DESC",
    targetFormat: "json");
Console.WriteLine(Encoding.UTF8.GetString(result.Content));
```

| Parameter | Type | Default | Description |
|---|---|---|---|
| `filePath` | string | required | Path to the file |
| `sql` | string | required | SQL query (table name: `data`) |
| `targetFormat` | string | `"csv"` | Output format |
| `noHeader` | bool | `false` | Treat first row as data |
| `fixEncoding` | bool | `true` | Repair encoding |
| `delimiter` | string | `""` | Custom delimiter for CSV-like input |
| `sheet` | string | `""` | Sheet or table to read |
| `ct` | CancellationToken | default | Cancellation token |

---

### `ReparatioResult`

Returned by `ConvertAsync`, `BatchConvertAsync`, `MergeAsync`, `AppendAsync`, and `QueryAsync`.

| Property | Type | Description |
|---|---|---|
| `Content` | byte[] | Raw file content |
| `Filename` | string | Suggested output filename |
| `Warning` | string | Server warning, or `null` |

```csharp
var result = await client.ConvertAsync("data.csv", "parquet");
File.WriteAllBytes(result.Filename, result.Content);
if (result.Warning != null)
    Console.Error.WriteLine("Warning: " + result.Warning);
```

---

## Supported formats

### Input

CSV, TSV, CSV.GZ, CSV.BZ2, CSV.ZST, CSV.ZIP, TSV.GZ, TSV.BZ2, TSV.ZST, TSV.ZIP, GZ (any supported format), ZIP (any supported format), BZ2 (any supported format), ZST (any supported format), Excel (.xlsx / .xls), ODS, JSON, JSON.GZ, JSON.BZ2, JSON.ZST, JSON.ZIP, JSON Lines, GeoJSON, Parquet, Feather, Arrow, ORC, Avro, SQLite, YAML, BSON, SRT, VTT, HTML, Markdown, XML, SQL dump, PDF (text layer)

### Output

CSV, TSV, CSV.GZ, CSV.BZ2, CSV.ZST, CSV.ZIP, TSV.GZ, TSV.BZ2, TSV.ZST, TSV.ZIP, Excel (.xlsx), ODS, JSON, JSON.GZ, JSON.BZ2, JSON.ZST, JSON.ZIP, JSON Lines, JSON Lines.GZ, JSON Lines.BZ2, JSON Lines.ZST, JSON Lines.ZIP, GeoJSON, GeoJSON.GZ, GeoJSON.BZ2, GeoJSON.ZST, GeoJSON.ZIP, Parquet, Feather, Arrow, ORC, Avro, SQLite, YAML, BSON, SRT, VTT

---

## Error handling

All exceptions derive from `ReparatioException`:

| Exception | Cause |
|---|---|
| `AuthenticationException` | Missing, invalid, or expired API key (HTTP 401/403) |
| `InsufficientPlanException` | Operation requires a Professional plan (HTTP 402) |
| `FileTooLargeException` | File exceeds the server's size limit (HTTP 413) |
| `ParseException` | File could not be parsed in the detected format (HTTP 422) |
| `ReparatioException` | Unexpected server error — has `.StatusCode` and `.Message` |

```csharp
try {
    var result = await client.ConvertAsync("bad.csv", "parquet");
} catch (AuthenticationException) {
    Console.Error.WriteLine("Check your API key");
} catch (ParseException ex) {
    Console.Error.WriteLine("Could not read file: " + ex.Message);
} catch (ReparatioException ex) {
    Console.Error.WriteLine($"API error {ex.StatusCode}: {ex.Message}");
}
```

---

## Building from source

```bash
make        # compile Reparatio.dll
make test   # compile and run all 47 tests
```

Requires `mcs` (Mono C# compiler) and `System.Web.Extensions.dll` from `mono-complete`.

---

## Running the Examples

The repository includes 15 runnable examples covering every API method.

```bash
# build everything
make

# run all examples (against the Reparatio production API)
REPARATIO_API_KEY=EXAMPLE-EXAMPLE-EXAMPLE \
mono bin/Examples.exe
```

Set `REPARATIO_API_KEY` to your API key before running.

---

## Privacy

Files are sent to the Reparatio API at `reparatio.app` for processing.
Files are handled in memory and never stored — see the [Privacy Policy](https://reparatio.app).
