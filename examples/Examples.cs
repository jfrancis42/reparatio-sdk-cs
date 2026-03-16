/*
 * Reparatio C# SDK — runnable examples.
 *
 * Compile and run:
 *
 *   mcs -target:exe -out:bin/Examples.exe \
 *       -r:bin/Reparatio.dll \
 *       -r:System.dll -r:System.Core.dll \
 *       -r:System.Net.Http.dll \
 *       -r:System.Web.Extensions.dll \
 *       -r:System.Runtime.Serialization.dll \
 *       examples/Examples.cs
 *
 *   REPARATIO_API_KEY=rp_... mono bin/Examples.exe
 *
 * The examples target the Reparatio production API at reparatio.app.
 * Set REPARATIO_API_KEY to your API key before running.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;
using Reparatio;

// ── Configuration ─────────────────────────────────────────────────────────────

static class Config
{
    public static readonly string ApiKey =
        Environment.GetEnvironmentVariable("REPARATIO_API_KEY") ?? "EXAMPLE-EXAMPLE-EXAMPLE";
}

// ── Helpers ───────────────────────────────────────────────────────────────────

static class Helpers
{
    /// <summary>
    /// Write bytes to a temp file and return its path.
    /// Call File.Delete(path) in a finally block when done.
    /// </summary>
    public static string TempFile(byte[] bytes, string extension)
    {
        string path = Path.Combine(Path.GetTempPath(),
                                   Guid.NewGuid().ToString("N") + extension);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Build a minimal in-memory ZIP containing named CSV entries.</summary>
    public static byte[] BuildZip(string[] names, string[] csvContents)
    {
        using (var ms = new MemoryStream())
        {
            // Write a valid ZIP using raw ZIP format (works without System.IO.Compression)
            // We'll use a helper that writes each entry manually.
            WriteZip(ms, names, csvContents);
            return ms.ToArray();
        }
    }

    private struct ZipEntry
    {
        public long   Offset;
        public byte[] NameBytes;
        public byte[] Data;
        public ZipEntry(long offset, byte[] nameBytes, byte[] data)
        { Offset = offset; NameBytes = nameBytes; Data = data; }
    }

    private static void WriteZip(Stream output, string[] names, string[] contents)
    {
        // Build via GZip is unavailable for multi-file ZIPs without dotnet.
        // Instead write the PKZip format manually (store-only, no compression).
        var localHeaders = new List<ZipEntry>();
        foreach (var pair in ZipPairs(names, contents))
        {
            long   offset    = output.Position;
            byte[] nameBytes = Encoding.UTF8.GetBytes(pair.Key);
            byte[] data      = Encoding.UTF8.GetBytes(pair.Value);
            WriteLocalFileHeader(output, nameBytes, data);
            localHeaders.Add(new ZipEntry(offset, nameBytes, data));
        }
        long cdOffset = output.Position;
        foreach (var entry in localHeaders)
            WriteCentralDir(output, entry.NameBytes, entry.Data, entry.Offset);
        long cdSize = output.Position - cdOffset;
        WriteEndOfCentralDir(output, localHeaders.Count, cdSize, cdOffset);
    }

    private static IEnumerable<KeyValuePair<string,string>> ZipPairs(string[] names, string[] contents)
    {
        for (int i = 0; i < names.Length; i++)
            yield return new KeyValuePair<string,string>(names[i], contents[i]);
    }

    private static uint Crc32(byte[] data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte b in data)
        {
            crc ^= b;
            for (int j = 0; j < 8; j++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }
        return crc ^ 0xFFFFFFFF;
    }

    private static void WriteLocalFileHeader(Stream s, byte[] name, byte[] data)
    {
        uint crc = Crc32(data);
        var b = new BinaryWriter(s, Encoding.UTF8, true);
        b.Write(0x04034b50u);            // local file header signature
        b.Write((ushort)20);             // version needed
        b.Write((ushort)0);              // general purpose bit flag
        b.Write((ushort)0);              // compression method: store
        b.Write((ushort)0);              // last mod time
        b.Write((ushort)0);              // last mod date
        b.Write(crc);                    // crc-32
        b.Write((uint)data.Length);      // compressed size
        b.Write((uint)data.Length);      // uncompressed size
        b.Write((ushort)name.Length);    // file name length
        b.Write((ushort)0);              // extra field length
        b.Write(name);
        b.Write(data);
    }

    private static void WriteCentralDir(Stream s, byte[] name, byte[] data, long localOffset)
    {
        uint crc = Crc32(data);
        var b = new BinaryWriter(s, Encoding.UTF8, true);
        b.Write(0x02014b50u);            // central directory file header signature
        b.Write((ushort)20);             // version made by
        b.Write((ushort)20);             // version needed
        b.Write((ushort)0);              // general purpose bit flag
        b.Write((ushort)0);              // compression method
        b.Write((ushort)0);              // last mod time
        b.Write((ushort)0);              // last mod date
        b.Write(crc);
        b.Write((uint)data.Length);
        b.Write((uint)data.Length);
        b.Write((ushort)name.Length);
        b.Write((ushort)0);              // extra field length
        b.Write((ushort)0);              // file comment length
        b.Write((ushort)0);              // disk number start
        b.Write((ushort)0);              // internal file attributes
        b.Write(0u);                     // external file attributes
        b.Write((uint)localOffset);      // relative offset of local header
        b.Write(name);
    }

    private static void WriteEndOfCentralDir(Stream s, int count, long cdSize, long cdOffset)
    {
        var b = new BinaryWriter(s, Encoding.UTF8, true);
        b.Write(0x06054b50u);            // end of central dir signature
        b.Write((ushort)0);              // disk number
        b.Write((ushort)0);              // disk with start of CD
        b.Write((ushort)count);          // entries on this disk
        b.Write((ushort)count);          // total entries
        b.Write((uint)cdSize);           // size of central directory
        b.Write((uint)cdOffset);         // offset of start of central directory
        b.Write((ushort)0);              // comment length
    }

    public struct ZipReadEntry
    {
        public string Name;
        public byte[] Data;
        public ZipReadEntry(string name, byte[] data) { Name = name; Data = data; }
    }

    /// <summary>Parse the raw bytes of a ZIP and return a list of entries.</summary>
    public static List<ZipReadEntry> ReadZip(byte[] zipBytes)
    {
        var results = new List<ZipReadEntry>();
        using (var ms = new MemoryStream(zipBytes))
        using (var br = new BinaryReader(ms))
        {
            while (ms.Position + 4 <= ms.Length)
            {
                uint sig = br.ReadUInt32();
                if (sig != 0x04034b50u) break;  // stop at first non-local entry
                br.ReadUInt16();  // version needed
                br.ReadUInt16();  // flags
                ushort compression = br.ReadUInt16();
                br.ReadUInt16();  // mod time
                br.ReadUInt16();  // mod date
                br.ReadUInt32();  // crc
                uint compSize   = br.ReadUInt32();
                br.ReadUInt32();  // uncompressed size (not needed)
                ushort nameLen  = br.ReadUInt16();
                ushort extraLen = br.ReadUInt16();
                string entryName = Encoding.UTF8.GetString(br.ReadBytes(nameLen));
                br.ReadBytes(extraLen);
                byte[] entryData = br.ReadBytes((int)compSize);
                if (compression == 8)
                {
                    // deflate — decompress
                    using (var raw = new MemoryStream(entryData))
                    using (var def = new DeflateStream(raw, CompressionMode.Decompress))
                    using (var out2 = new MemoryStream())
                    {
                        def.CopyTo(out2);
                        entryData = out2.ToArray();
                    }
                }
                results.Add(new ZipReadEntry(entryName, entryData));
            }
        }
        return results;
    }
}

// ── Runner ────────────────────────────────────────────────────────────────────

static class ExampleRunner
{
    static int _pass, _fail;

    public static void Run(string name, Func<Task> body)
    {
        Console.WriteLine("\n" + new string('─', 60));
        Console.WriteLine("  " + name);
        Console.WriteLine(new string('─', 60));
        try
        {
            body().GetAwaiter().GetResult();
            Console.WriteLine("PASS");
            _pass++;
        }
        catch (Exception ex)
        {
            // Unwrap AggregateException from .GetAwaiter().GetResult()
            Exception inner = ex;
            while (inner is AggregateException ae && ae.InnerException != null)
                inner = ae.InnerException;
            Console.WriteLine("FAIL: " + inner.GetType().Name + ": " + inner.Message);
            _fail++;
        }
    }

    public static int Summary()
    {
        Console.WriteLine("\n" + new string('═', 60));
        int total = _pass + _fail;
        Console.WriteLine(string.Format("Results: {0}/{1} passed", _pass, total));
        Console.WriteLine(new string('═', 60));
        return _fail > 0 ? 1 : 0;
    }
}

// ── Examples ──────────────────────────────────────────────────────────────────

static class Examples
{
    // ── 1. FormatsAsync() — no key required ───────────────────────────────────

    public static async Task Ex01_Formats()
    {
        using (var client = new ReparatioClient())
        {
            var f = await client.FormatsAsync();

            var inputFormats  = (System.Collections.ArrayList)f["input"];
            var outputFormats = (System.Collections.ArrayList)f["output"];

            Console.WriteLine(string.Format("Input formats  ({0}): {1} ...",
                inputFormats.Count,
                string.Join(", ", new[] {
                    (string)inputFormats[0], (string)inputFormats[1],
                    (string)inputFormats[2], (string)inputFormats[3]
                })));
            Console.WriteLine(string.Format("Output formats ({0}): {1} ...",
                outputFormats.Count,
                string.Join(", ", new[] {
                    (string)outputFormats[0], (string)outputFormats[1],
                    (string)outputFormats[2], (string)outputFormats[3]
                })));

            // Assertions
            bool hasInputCsv = false, hasOutputParquet = false;
            foreach (object o in inputFormats)
                if ((string)o == "csv") { hasInputCsv = true; break; }
            foreach (object o in outputFormats)
                if ((string)o == "parquet") { hasOutputParquet = true; break; }

            if (!hasInputCsv)      throw new Exception("'csv' not in input formats");
            if (!hasOutputParquet) throw new Exception("'parquet' not in output formats");
        }
    }

    // ── 2. MeAsync() — account info ───────────────────────────────────────────

    public static async Task Ex02_Me()
    {
        using (var client = new ReparatioClient(Config.ApiKey))
        {
            var me = await client.MeAsync();

            string email  = (string)me["email"];
            string plan   = (string)me["plan"];
            bool   active = (bool)me["active"];

            Console.WriteLine("Email:      " + email);
            Console.WriteLine("Plan:       " + plan);
            Console.WriteLine("Active:     " + active);

            if (!active)
                throw new Exception("Account is not active");
            if (string.IsNullOrEmpty(email))
                throw new Exception("email is empty");
        }
    }

    // ── 3. InspectAsync() — CSV from file path ────────────────────────────────

    public static async Task Ex03_Inspect_CsvFilePath()
    {
        byte[] csv = Encoding.UTF8.GetBytes(
            "country,county\nEngland,Kent\nEngland,Essex\nWales,Gwent\n");

        string tmp = Helpers.TempFile(csv, ".csv");
        try
        {
            using (var client = new ReparatioClient(Config.ApiKey))
            {
                var info = await client.InspectAsync(tmp, previewRows: 3);

                int   rows     = (int)info["rows"];
                var   cols     = (System.Collections.ArrayList)info["columns"];
                var   preview  = (System.Collections.ArrayList)info["preview"];
                string enc     = (string)info["detected_encoding"];

                Console.WriteLine("Filename:  " + (string)info["filename"]);
                Console.WriteLine("Rows:      " + rows);
                Console.WriteLine("Encoding:  " + enc);
                Console.WriteLine(string.Format("Columns ({0}):", cols.Count));
                foreach (Dictionary<string,object> col in cols)
                    Console.WriteLine(string.Format("  {0,-25} {1}", col["name"], col["dtype"]));
                Console.WriteLine("Preview row 0: " + FormatDict(
                    (Dictionary<string,object>)preview[0]));

                if (rows <= 0)      throw new Exception("rows should be > 0");
                if (cols.Count < 1) throw new Exception("expected at least 1 column");
            }
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    // ── 4. InspectAsync() — raw byte[] (in-memory CSV) ───────────────────────
    //
    // The C# SDK takes a file path, so we write the bytes to a temp file.
    // This pattern is idiomatic in .NET when your data originates in memory.

    public static async Task Ex04_Inspect_ByteArray()
    {
        byte[] csvBytes = Encoding.UTF8.GetBytes(
            "id,name,score\n1,Alice,95\n2,Bob,87\n3,Carol,92\n");

        string tmp = Helpers.TempFile(csvBytes, ".csv");
        try
        {
            using (var client = new ReparatioClient(Config.ApiKey))
            {
                var info = await client.InspectAsync(tmp, previewRows: 3);

                int rows = (int)info["rows"];
                var cols = (System.Collections.ArrayList)info["columns"];

                Console.WriteLine("Rows:    " + rows);
                Console.Write("Columns: ");
                var names = new List<string>();
                foreach (Dictionary<string,object> col in cols)
                    names.Add((string)col["name"]);
                Console.WriteLine(string.Join(", ", names));

                var preview = (System.Collections.ArrayList)info["preview"];
                Console.WriteLine("Preview: " + FormatDict(
                    (Dictionary<string,object>)preview[0]));

                if (rows != 3)
                    throw new Exception("Expected 3 rows, got " + rows);
                if (names.Count != 3 ||
                    names[0] != "id" || names[1] != "name" || names[2] != "score")
                    throw new Exception("Unexpected column names: " +
                                        string.Join(", ", names));
            }
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    // ── 5. InspectAsync() — TSV (tab-separated) ───────────────────────────────

    public static async Task Ex05_Inspect_Tsv()
    {
        byte[] tsv = Encoding.UTF8.GetBytes(
            "city\tpopulation\tregion\n" +
            "London\t9000000\tEngland\n" +
            "Manchester\t553000\tEngland\n" +
            "Cardiff\t362000\tWales\n");

        string tmp = Helpers.TempFile(tsv, ".tsv");
        try
        {
            using (var client = new ReparatioClient(Config.ApiKey))
            {
                var info = await client.InspectAsync(tmp, previewRows: 2);

                int rows = (int)info["rows"];
                var cols = (System.Collections.ArrayList)info["columns"];

                Console.WriteLine("Filename: " + (string)info["filename"]);
                Console.WriteLine("Rows:     " + rows);
                var colNames = new List<string>();
                foreach (Dictionary<string,object> col in cols)
                    colNames.Add((string)col["name"]);
                Console.WriteLine("Columns:  " + string.Join(", ", colNames));

                if (rows <= 0)
                    throw new Exception("Expected rows > 0, got " + rows);
                if (cols.Count < 1)
                    throw new Exception("Expected at least 1 column");
            }
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    // ── 6. ConvertAsync() — CSV → Parquet (verify PAR1 magic bytes) ───────────

    public static async Task Ex06_Convert_CsvToParquet()
    {
        byte[] csv = Encoding.UTF8.GetBytes(
            "country,county\nEngland,Kent\nEngland,Essex\nWales,Gwent\n");

        string tmp = Helpers.TempFile(csv, ".csv");
        try
        {
            using (var client = new ReparatioClient(Config.ApiKey))
            {
                var result = await client.ConvertAsync(tmp, "parquet");

                Console.WriteLine("Output filename: " + result.Filename);
                Console.WriteLine(string.Format("Output size:     {0:N0} bytes",
                                                result.Content.Length));

                if (!result.Filename.EndsWith(".parquet"))
                    throw new Exception("Filename should end with .parquet, got: " +
                                        result.Filename);
                if (result.Content.Length == 0)
                    throw new Exception("Content is empty");

                // Parquet magic bytes: PAR1 at the start of the file
                if (result.Content[0] != 0x50 || result.Content[1] != 0x41 ||
                    result.Content[2] != 0x52 || result.Content[3] != 0x31)
                    throw new Exception("Not a valid Parquet file (expected PAR1 magic bytes)");

                Console.WriteLine("Magic bytes: PAR1 - valid Parquet");
            }
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    // ── 7. ConvertAsync() — CSV → JSON Lines ─────────────────────────────────

    public static async Task Ex07_Convert_CsvToJsonl()
    {
        byte[] csv = Encoding.UTF8.GetBytes(
            "city,population,region\n" +
            "London,9000000,England\n" +
            "Manchester,553000,England\n" +
            "Cardiff,362000,Wales\n");

        string tmp = Helpers.TempFile(csv, ".csv");
        try
        {
            using (var client = new ReparatioClient(Config.ApiKey))
            {
                var result = await client.ConvertAsync(tmp, "jsonl");

                string text  = Encoding.UTF8.GetString(result.Content);
                string[] lines = text.Split(new[]{'\n'}, StringSplitOptions.RemoveEmptyEntries);

                Console.WriteLine("Output filename: " + result.Filename);
                Console.WriteLine("Lines:           " + lines.Length);
                Console.WriteLine("First record:    " + lines[0]);

                if (!result.Filename.EndsWith(".jsonl"))
                    throw new Exception("Filename should end with .jsonl");
                if (lines.Length == 0)
                    throw new Exception("No output lines");

                // Each line must be parseable as a JSON object (starts with '{')
                string first = lines[0].Trim();
                if (!first.StartsWith("{") || !first.EndsWith("}"))
                    throw new Exception("First line is not a JSON object: " + first);
            }
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    // ── 8. ConvertAsync() — select + rename columns + gzip output ─────────────

    public static async Task Ex08_Convert_SelectColumnsGzip()
    {
        byte[] csv = Encoding.UTF8.GetBytes(
            "country,county\nEngland,Kent\nEngland,Essex\nWales,Gwent\n");

        string tmp = Helpers.TempFile(csv, ".csv");
        try
        {
            using (var client = new ReparatioClient(Config.ApiKey))
            {
                // Select only the two columns and compress output
                var result = await client.ConvertAsync(
                    tmp,
                    "csv.gz",
                    selectColumns: new[] { "country", "county" });

                Console.WriteLine("Output filename: " + result.Filename);
                Console.WriteLine(string.Format("Output size:     {0:N0} bytes (compressed)",
                                                result.Content.Length));

                if (!result.Filename.EndsWith(".gz"))
                    throw new Exception("Expected .gz extension, got: " + result.Filename);
                if (result.Content.Length == 0)
                    throw new Exception("Content is empty");

                // GZip magic bytes: 1f 8b
                if (result.Content[0] != 0x1F || result.Content[1] != 0x8B)
                    throw new Exception("Not a valid gzip file (expected 1F 8B magic bytes)");

                Console.WriteLine("Magic bytes: 1F 8B - valid gzip");

                // Decompress and verify column headers are present
                using (var compressedStream = new MemoryStream(result.Content))
                using (var gzip = new GZipStream(compressedStream, CompressionMode.Decompress))
                using (var decompressed = new MemoryStream())
                {
                    gzip.CopyTo(decompressed);
                    string csvText  = Encoding.UTF8.GetString(decompressed.ToArray());
                    string firstLine = csvText.Split('\n')[0].Trim();
                    Console.WriteLine("Decompressed header: " + firstLine);

                    if (!firstLine.Contains("country") || !firstLine.Contains("county"))
                        throw new Exception("Expected 'country' and 'county' columns in header");
                }
            }
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    // ── 9. ConvertAsync() — deduplicate + sample ──────────────────────────────

    public static async Task Ex09_Convert_DeduplicateSample()
    {
        // Build a CSV with deliberate duplicates: 2 unique rows, repeated 10x each
        var sb = new StringBuilder();
        sb.AppendLine("name,value");
        for (int i = 0; i < 10; i++) { sb.AppendLine("Alice,1"); sb.AppendLine("Bob,2"); }
        byte[] csvBytes = Encoding.UTF8.GetBytes(sb.ToString());

        string tmp = Helpers.TempFile(csvBytes, ".csv");
        try
        {
            using (var client = new ReparatioClient(Config.ApiKey))
            {
                // First confirm raw row count
                var info = await client.InspectAsync(tmp);
                Console.WriteLine("Raw rows (with duplicates): " + info["rows"]);

                // Deduplicate collapses 20 rows → 2 unique rows; sample_n=1 picks 1
                var result = await client.ConvertAsync(tmp, "csv",
                    deduplicate: true,
                    sampleN:     1);

                string text = Encoding.UTF8.GetString(result.Content);
                string[] lines = text.Split(new[]{'\n'},
                                            StringSplitOptions.RemoveEmptyEntries);
                int dataRows = lines.Length - 1;  // subtract header
                Console.WriteLine("After dedup + sample_n=1: " + dataRows + " data row(s)");

                if (dataRows != 1)
                    throw new Exception("Expected exactly 1 data row after dedup+sample, got "
                                        + dataRows);
            }
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    // ── 10. ConvertAsync() — castColumns type overrides ───────────────────────

    public static async Task Ex10_Convert_CastColumns()
    {
        byte[] csvBytes = Encoding.UTF8.GetBytes(
            "id,amount,event_date\n1,19.99,2025-01-15\n2,34.50,2025-02-20\n3,7.00,2025-03-01\n");

        // castColumnsJson: JSON object mapping column names → {"type":...}
        string castJson = "{\"id\":{\"type\":\"Int32\"}," +
                          "\"amount\":{\"type\":\"Float64\"}," +
                          "\"event_date\":{\"type\":\"Date\",\"format\":\"%Y-%m-%d\"}}";

        string tmp = Helpers.TempFile(csvBytes, ".csv");
        try
        {
            using (var client = new ReparatioClient(Config.ApiKey))
            {
                var result = await client.ConvertAsync(tmp, "parquet",
                    castColumnsJson: castJson);

                Console.WriteLine("Output filename: " + result.Filename);
                Console.WriteLine(string.Format("Output size:     {0:N0} bytes",
                                                result.Content.Length));

                if (result.Content[0] != 0x50 || result.Content[1] != 0x41 ||
                    result.Content[2] != 0x52 || result.Content[3] != 0x31)
                    throw new Exception("Not a valid Parquet file (expected PAR1 magic bytes)");

                Console.WriteLine("Magic bytes: PAR1 - valid Parquet");

                // Round-trip: inspect the Parquet to verify column types
                string parquetTmp = Helpers.TempFile(result.Content, ".parquet");
                try
                {
                    var info = await client.InspectAsync(parquetTmp);
                    var cols = (System.Collections.ArrayList)info["columns"];

                    var typeMap = new Dictionary<string, string>();
                    foreach (Dictionary<string,object> col in cols)
                        typeMap[(string)col["name"]] = (string)col["dtype"];

                    Console.WriteLine("Column types after cast:");
                    foreach (var kv in typeMap)
                        Console.WriteLine(string.Format("  {0}: {1}", kv.Key, kv.Value));

                    // Int32 may be widened to Int64 by the server; both are acceptable
                    if (typeMap["id"] != "Int32" && typeMap["id"] != "Int64")
                        throw new Exception("Expected id to be Int32 or Int64, got: "
                                            + typeMap["id"]);
                    if (typeMap["amount"] != "Float64")
                        throw new Exception("Expected amount to be Float64, got: "
                                            + typeMap["amount"]);
                    // Parquet stores Date with integer encoding; Polars may surface it
                    // as Date or String depending on schema inference depth
                    if (typeMap["event_date"] != "Date" && typeMap["event_date"] != "String")
                        throw new Exception("Expected event_date to be Date or String, got: "
                                            + typeMap["event_date"]);
                }
                finally { if (File.Exists(parquetTmp)) File.Delete(parquetTmp); }
            }
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    // ── 11. QueryAsync() — SQL aggregation, parse result ─────────────────────

    public static async Task Ex11_Query()
    {
        byte[] csvBytes = Encoding.UTF8.GetBytes(
            "region,product,revenue\n" +
            "North,Widget,100\n" +
            "South,Widget,200\n" +
            "North,Gadget,150\n" +
            "South,Gadget,300\n" +
            "North,Widget,120\n");

        string tmp = Helpers.TempFile(csvBytes, ".csv");
        try
        {
            using (var client = new ReparatioClient(Config.ApiKey))
            {
                var result = await client.QueryAsync(
                    tmp,
                    "SELECT region, SUM(revenue) AS total " +
                    "FROM data GROUP BY region ORDER BY total DESC",
                    targetFormat: "csv");

                string text  = Encoding.UTF8.GetString(result.Content);
                string[] lines = text.Split(new[]{'\n'},
                                            StringSplitOptions.RemoveEmptyEntries);

                Console.WriteLine("Query result (" + (lines.Length - 1) + " rows):");
                foreach (string line in lines)
                    Console.WriteLine("  " + line);

                // Parse CSV result manually: header + 2 data rows
                if (lines.Length < 3)
                    throw new Exception("Expected at least 2 data rows + header");

                string[] header = lines[0].Split(',');
                int regionIdx = Array.IndexOf(header, "region");
                int totalIdx  = Array.IndexOf(header, "total");
                if (regionIdx < 0) throw new Exception("'region' column not found in result");
                if (totalIdx  < 0) throw new Exception("'total' column not found in result");

                // First row should be South (higher total: 500 vs 370)
                string[] firstDataRow = lines[1].Split(',');
                string topRegion = firstDataRow[regionIdx];
                int    topTotal  = int.Parse(firstDataRow[totalIdx]);

                Console.WriteLine("Top region: " + topRegion + " with total=" + topTotal);

                if (topRegion != "South")
                    throw new Exception("Expected South as top region, got: " + topRegion);
                if (topTotal != 500)
                    throw new Exception("Expected South total=500, got: " + topTotal);
            }
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    // ── 12. AppendAsync() — stack three in-memory CSVs ───────────────────────

    public static async Task Ex12_Append()
    {
        // Three monthly sales files with matching schema
        byte[] jan = Encoding.UTF8.GetBytes(
            "date,region,revenue\n2025-01-01,North,100\n2025-01-02,South,200\n");
        byte[] feb = Encoding.UTF8.GetBytes(
            "date,region,revenue\n2025-02-01,North,150\n2025-02-02,South,180\n");
        byte[] mar = Encoding.UTF8.GetBytes(
            "date,region,revenue\n2025-03-01,North,120\n2025-03-02,South,210\n");

        string tmpJan = Helpers.TempFile(jan, ".csv");
        string tmpFeb = Helpers.TempFile(feb, ".csv");
        string tmpMar = Helpers.TempFile(mar, ".csv");
        try
        {
            using (var client = new ReparatioClient(Config.ApiKey))
            {
                var result = await client.AppendAsync(
                    new[] { tmpJan, tmpFeb, tmpMar },
                    "csv");

                string text  = Encoding.UTF8.GetString(result.Content);
                string[] lines = text.Split(new[]{'\n'},
                                            StringSplitOptions.RemoveEmptyEntries);
                int dataRows = lines.Length - 1;

                Console.WriteLine("Output filename:             " + result.Filename);
                Console.WriteLine("Total rows (incl. header):   " + lines.Length);
                Console.WriteLine("Header:                      " + lines[0]);

                if (dataRows != 6)
                    throw new Exception("Expected 6 data rows (2 per file × 3), got "
                                        + dataRows);
            }
        }
        finally
        {
            foreach (string p in new[]{tmpJan, tmpFeb, tmpMar})
                if (File.Exists(p)) File.Delete(p);
        }
    }

    // ── 13. MergeAsync() — inner join two CSVs on a key column ───────────────

    public static async Task Ex13_Merge()
    {
        byte[] orders = Encoding.UTF8.GetBytes(
            "order_id,customer_id,amount\n" +
            "1001,C1,50.00\n" +
            "1002,C2,75.00\n" +
            "1003,C1,30.00\n" +
            "1004,C3,90.00\n");     // C3 has no matching customer → excluded by inner join

        byte[] customers = Encoding.UTF8.GetBytes(
            "customer_id,name,city\n" +
            "C1,Alice,Boston\n" +
            "C2,Bob,Chicago\n");

        string tmpOrders    = Helpers.TempFile(orders,    ".csv");
        string tmpCustomers = Helpers.TempFile(customers, ".csv");
        try
        {
            using (var client = new ReparatioClient(Config.ApiKey))
            {
                var result = await client.MergeAsync(
                    tmpOrders,
                    tmpCustomers,
                    operation:    "inner",
                    targetFormat: "csv",
                    joinOn:       "customer_id");

                string text  = Encoding.UTF8.GetString(result.Content);
                string[] lines = text.Split(new[]{'\n'},
                                            StringSplitOptions.RemoveEmptyEntries);

                Console.WriteLine("Output filename:           " + result.Filename);
                Console.WriteLine("Result rows (incl header): " + lines.Length);
                Console.WriteLine("Header: " + lines[0]);
                for (int i = 1; i < lines.Length; i++)
                    Console.WriteLine("  " + lines[i]);

                // C3 (order 1004) has no customer → inner join yields 3 matched rows
                int dataRows = lines.Length - 1;
                if (dataRows != 3)
                    throw new Exception("Expected 3 joined rows (C1, C2, C1), got "
                                        + dataRows);
            }
        }
        finally
        {
            if (File.Exists(tmpOrders))    File.Delete(tmpOrders);
            if (File.Exists(tmpCustomers)) File.Delete(tmpCustomers);
        }
    }

    // ── 14. BatchConvertAsync() — ZIP of CSVs → ZIP of Parquets ──────────────

    public static async Task Ex14_BatchConvert()
    {
        // Build a ZIP in memory containing two small CSVs
        byte[] zipBytes = Helpers.BuildZip(
            new[] { "sales_jan.csv", "sales_feb.csv" },
            new[] {
                "date,amount\n2025-01-01,100\n2025-01-02,200\n",
                "date,amount\n2025-02-01,150\n2025-02-02,180\n"
            });

        string tmpZip = Helpers.TempFile(zipBytes, ".zip");
        try
        {
            using (var client = new ReparatioClient(Config.ApiKey))
            {
                var result = await client.BatchConvertAsync(tmpZip, "parquet");

                Console.WriteLine("Output filename: " + result.Filename);
                Console.WriteLine(string.Format("Output size:     {0:N0} bytes",
                                                result.Content.Length));

                if (result.Warning != null)
                    Console.WriteLine("Warnings:        " + result.Warning);

                // The returned ZIP magic bytes: PK (50 4B)
                if (result.Content[0] != 0x50 || result.Content[1] != 0x4B)
                    throw new Exception("Output is not a ZIP file (expected PK magic bytes)");

                // Unpack and verify each Parquet file
                var entries = Helpers.ReadZip(result.Content);
                Console.WriteLine("Files in output ZIP:");
                foreach (var entry in entries)
                {
                    bool isParquet = entry.Data.Length >= 4 &&
                                     entry.Data[0] == 0x50 && entry.Data[1] == 0x41 &&
                                     entry.Data[2] == 0x52 && entry.Data[3] == 0x31;
                    Console.WriteLine(string.Format("  {0}: {1:N0} bytes — {2}",
                        entry.Name, entry.Data.Length,
                        isParquet ? "valid Parquet (PAR1)" : "NOT a valid Parquet!"));
                    if (!isParquet)
                        throw new Exception(entry.Name + " is not a valid Parquet file");
                }

                if (entries.Count < 1)
                    throw new Exception("Expected at least 1 Parquet file in output ZIP");
            }
        }
        finally { if (File.Exists(tmpZip)) File.Delete(tmpZip); }
    }

    // ── 15. Error handling — bad key throws AuthenticationException ───────────

    public static async Task Ex15_ErrorHandling()
    {
        byte[] csvBytes = Encoding.UTF8.GetBytes("a,b\n1,2\n3,4\n");
        string tmp = Helpers.TempFile(csvBytes, ".csv");
        try
        {
            // Bad API key → AuthenticationException (HTTP 401)
            using (var badClient = new ReparatioClient("rp_invalid_key_xyz"))
            {
                try
                {
                    await badClient.ConvertAsync(tmp, "parquet");
                    throw new Exception("Expected AuthenticationException but no exception was thrown");
                }
                catch (AuthenticationException ex)
                {
                    Console.WriteLine(string.Format(
                        "Bad key caught:  AuthenticationException (HTTP {0}): {1}",
                        ex.StatusCode, ex.Message));
                    if (ex.StatusCode != 401 && ex.StatusCode != 403)
                        throw new Exception("Expected status 401 or 403, got " + ex.StatusCode);
                }
            }

            // Unparseable binary garbage → ReparatioException (HTTP 422 ParseException)
            byte[] garbage = new byte[] {
                0x00, 0x01, 0x02, 0x03, 0xDE, 0xAD, 0xBE, 0xEF };
            string tmpGarbage = Helpers.TempFile(garbage, ".parquet");
            try
            {
                using (var client = new ReparatioClient(Config.ApiKey))
                {
                    try
                    {
                        await client.ConvertAsync(tmpGarbage, "csv");
                        throw new Exception("Expected ReparatioException but no exception was thrown");
                    }
                    catch (ParseException ex)
                    {
                        Console.WriteLine(string.Format(
                            "Bad file caught: ParseException (HTTP {0}): {1}",
                            ex.StatusCode, ex.Message));
                    }
                    catch (ReparatioException ex) when (ex.StatusCode >= 400)
                    {
                        Console.WriteLine(string.Format(
                            "Bad file caught: ReparatioException (HTTP {0}): {1}",
                            ex.StatusCode, ex.Message));
                    }
                }
            }
            finally { if (File.Exists(tmpGarbage)) File.Delete(tmpGarbage); }
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    // ── Utility ───────────────────────────────────────────────────────────────

    private static string FormatDict(Dictionary<string,object> d)
    {
        var pairs = new List<string>();
        foreach (var kv in d)
            pairs.Add(kv.Key + "=" + (kv.Value ?? "null"));
        return "{" + string.Join(", ", pairs) + "}";
    }
}

// ── Entry point ───────────────────────────────────────────────────────────────

class Program
{
    static int Main()
    {
        Console.WriteLine("Reparatio C# SDK Examples");
        Console.WriteLine("Server: https://reparatio.app");
        Console.WriteLine(string.Format("Mono:   {0}", Environment.Version));

        ExampleRunner.Run("01. FormatsAsync()  — list supported formats (no key)",
            Examples.Ex01_Formats);
        ExampleRunner.Run("02. MeAsync()        — account / subscription info",
            Examples.Ex02_Me);
        ExampleRunner.Run("03. InspectAsync()   — CSV from file path",
            Examples.Ex03_Inspect_CsvFilePath);
        ExampleRunner.Run("04. InspectAsync()   — raw byte[] (in-memory CSV)",
            Examples.Ex04_Inspect_ByteArray);
        ExampleRunner.Run("05. InspectAsync()   — TSV (tab-separated)",
            Examples.Ex05_Inspect_Tsv);
        ExampleRunner.Run("06. ConvertAsync()   — CSV → Parquet (verify PAR1 bytes)",
            Examples.Ex06_Convert_CsvToParquet);
        ExampleRunner.Run("07. ConvertAsync()   — CSV → JSON Lines",
            Examples.Ex07_Convert_CsvToJsonl);
        ExampleRunner.Run("08. ConvertAsync()   — select + gzip output",
            Examples.Ex08_Convert_SelectColumnsGzip);
        ExampleRunner.Run("09. ConvertAsync()   — deduplicate + sample",
            Examples.Ex09_Convert_DeduplicateSample);
        ExampleRunner.Run("10. ConvertAsync()   — castColumns type overrides",
            Examples.Ex10_Convert_CastColumns);
        ExampleRunner.Run("11. QueryAsync()     — SQL aggregation, parse CSV result",
            Examples.Ex11_Query);
        ExampleRunner.Run("12. AppendAsync()    — stack three in-memory CSVs",
            Examples.Ex12_Append);
        ExampleRunner.Run("13. MergeAsync()     — inner join two CSVs on key column",
            Examples.Ex13_Merge);
        ExampleRunner.Run("14. BatchConvertAsync() — ZIP of CSVs → ZIP of Parquets",
            Examples.Ex14_BatchConvert);
        ExampleRunner.Run("15. Error handling   — bad key → AuthenticationException",
            Examples.Ex15_ErrorHandling);

        return ExampleRunner.Summary();
    }
}
