using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Tests")]

namespace Reparatio
{
    /// <summary>
    /// Async Reparatio API client.
    ///
    /// <example>
    /// <code>
    /// using (var client = new ReparatioClient("rp_YOUR_KEY")) {
    ///     var info   = await client.InspectAsync("data.csv");
    ///     var result = await client.ConvertAsync("data.csv", "parquet");
    ///     File.WriteAllBytes(result.Filename, result.Content);
    /// }
    /// </code>
    /// </example>
    /// </summary>
    public class ReparatioClient : IDisposable
    {
        private const string DefaultBaseUrl = "https://reparatio.app";
        private static readonly Regex CdFilenameRe =
            new Regex(@"filename=""([^""]+)""", RegexOptions.Compiled);

        private readonly string     _baseUrl;
        private readonly string     _apiKey;
        internal         HttpClient _http;    // internal for test injection
        private bool                _disposed;

        // ── Constructors ─────────────────────────────────────────────────

        public ReparatioClient()
            : this(null, DefaultBaseUrl, TimeSpan.FromSeconds(120)) { }

        public ReparatioClient(string apiKey)
            : this(apiKey, DefaultBaseUrl, TimeSpan.FromSeconds(120)) { }

        public ReparatioClient(string apiKey, string baseUrl, TimeSpan timeout)
        {
            _apiKey  = apiKey ?? Environment.GetEnvironmentVariable("REPARATIO_API_KEY") ?? "";
            _baseUrl = baseUrl.TrimEnd('/');
            _http    = new HttpClient { Timeout = timeout };
            if (!string.IsNullOrEmpty(_apiKey))
                _http.DefaultRequestHeaders.Add("X-API-Key", _apiKey);
        }

        // ── formats ──────────────────────────────────────────────────────

        /// <summary>
        /// List supported input/output formats. No API key required.
        /// Returns a Dictionary with "input" and "output" string lists.
        /// </summary>
        public async Task<Dictionary<string, object>> FormatsAsync(
            CancellationToken ct = default(CancellationToken))
        {
            return await GetJsonAsync("/api/v1/formats", ct).ConfigureAwait(false);
        }

        // ── me ────────────────────────────────────────────────────────────

        /// <summary>Return subscription and usage details for the current API key.</summary>
        public async Task<Dictionary<string, object>> MeAsync(
            CancellationToken ct = default(CancellationToken))
        {
            return await GetJsonAsync("/api/v1/me", ct).ConfigureAwait(false);
        }

        // ── inspect ───────────────────────────────────────────────────────

        /// <summary>
        /// Inspect a file: return schema, encoding, row count, and preview.
        /// No API key required.
        /// </summary>
        public async Task<Dictionary<string, object>> InspectAsync(
            string filePath,
            bool   noHeader    = false,
            bool   fixEncoding = true,
            int    previewRows = 8,
            string delimiter   = "",
            string sheet       = "",
            CancellationToken ct = default(CancellationToken))
        {
            byte[] bytes    = File.ReadAllBytes(filePath);
            string filename = Path.GetFileName(filePath);
            using (var form = new MultipartFormDataContent())
            {
                AddFile(form, "file", bytes, filename);
                form.Add(new StringContent(noHeader    ? "true" : "false"), "no_header");
                form.Add(new StringContent(fixEncoding ? "true" : "false"), "fix_encoding");
                form.Add(new StringContent(previewRows.ToString()),         "preview_rows");
                form.Add(new StringContent(delimiter),                      "delimiter");
                form.Add(new StringContent(sheet),                          "sheet");
                return await PostJsonAsync("/api/v1/inspect", form, ct).ConfigureAwait(false);
            }
        }

        // ── convert ───────────────────────────────────────────────────────

        /// <summary>Convert a file to a different format.</summary>
        public async Task<ReparatioResult> ConvertAsync(
            string       filePath,
            string       targetFormat,
            bool         noHeader         = false,
            bool         fixEncoding      = true,
            string       delimiter        = "",
            string       sheet            = "",
            IList<string> selectColumns   = null,
            bool         deduplicate      = false,
            int          sampleN          = 0,
            double       sampleFrac       = 0.0,
            string       geometryColumn   = "geometry",
            string       castColumnsJson  = "{}",
            IList<string> nullValues      = null,
            string       encodingOverride = null,
            CancellationToken ct = default(CancellationToken))
        {
            byte[] bytes    = File.ReadAllBytes(filePath);
            string filename = Path.GetFileName(filePath);
            using (var form = new MultipartFormDataContent())
            {
                AddFile(form, "file", bytes, filename);
                form.Add(new StringContent(targetFormat),                   "target_format");
                form.Add(new StringContent(noHeader    ? "true" : "false"), "no_header");
                form.Add(new StringContent(fixEncoding ? "true" : "false"), "fix_encoding");
                form.Add(new StringContent(delimiter),                      "delimiter");
                form.Add(new StringContent(sheet),                          "sheet");
                form.Add(new StringContent(ToJsonArray(selectColumns)),     "select_columns");
                form.Add(new StringContent(deduplicate ? "true" : "false"), "deduplicate");
                form.Add(new StringContent(sampleN.ToString()),             "sample_n");
                form.Add(new StringContent(sampleFrac.ToString("G")),       "sample_frac");
                form.Add(new StringContent(geometryColumn),                 "geometry_column");
                form.Add(new StringContent(castColumnsJson),                "cast_columns");
                form.Add(new StringContent(ToJsonArray(nullValues)),        "null_values");
                if (!string.IsNullOrEmpty(encodingOverride))
                    form.Add(new StringContent(encodingOverride), "encoding_override");
                string baseName = Path.GetFileNameWithoutExtension(filename);
                string fallback = baseName + "." + targetFormat;
                return await PostFileAsync("/api/v1/convert", form, fallback,
                                           "X-Reparatio-Warning", false, ct)
                             .ConfigureAwait(false);
            }
        }

        // ── batch-convert ─────────────────────────────────────────────────

        /// <summary>
        /// Convert every file in a ZIP archive to a common format.
        /// Skipped files are listed in ReparatioResult.Warning.
        /// </summary>
        public async Task<ReparatioResult> BatchConvertAsync(
            string       zipPath,
            string       targetFormat,
            bool         noHeader       = false,
            bool         fixEncoding    = true,
            string       delimiter      = "",
            IList<string> selectColumns = null,
            bool         deduplicate    = false,
            int          sampleN        = 0,
            double       sampleFrac     = 0.0,
            string       castColumnsJson = "{}",
            CancellationToken ct = default(CancellationToken))
        {
            byte[] bytes    = File.ReadAllBytes(zipPath);
            string filename = Path.GetFileName(zipPath);
            using (var form = new MultipartFormDataContent())
            {
                AddFile(form, "zip_file", bytes, filename, "application/zip");
                form.Add(new StringContent(targetFormat),                   "target_format");
                form.Add(new StringContent(noHeader    ? "true" : "false"), "no_header");
                form.Add(new StringContent(fixEncoding ? "true" : "false"), "fix_encoding");
                form.Add(new StringContent(delimiter),                      "delimiter");
                form.Add(new StringContent(ToJsonArray(selectColumns)),     "select_columns");
                form.Add(new StringContent(deduplicate ? "true" : "false"), "deduplicate");
                form.Add(new StringContent(sampleN.ToString()),             "sample_n");
                form.Add(new StringContent(sampleFrac.ToString("G")),       "sample_frac");
                form.Add(new StringContent(castColumnsJson),                "cast_columns");
                return await PostFileAsync("/api/v1/batch-convert", form, "converted.zip",
                                           "X-Reparatio-Errors", true, ct)
                             .ConfigureAwait(false);
            }
        }

        // ── merge ─────────────────────────────────────────────────────────

        /// <summary>Merge or join two files.</summary>
        /// <param name="operation">append | left | right | outer | inner</param>
        public async Task<ReparatioResult> MergeAsync(
            string filePath1,
            string filePath2,
            string operation,
            string targetFormat,
            string joinOn         = "",
            bool   noHeader       = false,
            bool   fixEncoding    = true,
            string geometryColumn = "geometry",
            CancellationToken ct  = default(CancellationToken))
        {
            byte[] b1     = File.ReadAllBytes(filePath1);
            byte[] b2     = File.ReadAllBytes(filePath2);
            string fname1 = Path.GetFileName(filePath1);
            string fname2 = Path.GetFileName(filePath2);
            using (var form = new MultipartFormDataContent())
            {
                AddFile(form, "file1", b1, fname1);
                AddFile(form, "file2", b2, fname2);
                form.Add(new StringContent(operation),                      "operation");
                form.Add(new StringContent(targetFormat),                   "target_format");
                form.Add(new StringContent(joinOn),                         "join_on");
                form.Add(new StringContent(noHeader    ? "true" : "false"), "no_header");
                form.Add(new StringContent(fixEncoding ? "true" : "false"), "fix_encoding");
                form.Add(new StringContent(geometryColumn),                 "geometry_column");
                string base1    = Path.GetFileNameWithoutExtension(fname1);
                string base2    = Path.GetFileNameWithoutExtension(fname2);
                string fallback = string.Format("{0}_{1}_{2}.{3}",
                                                base1, operation, base2, targetFormat);
                return await PostFileAsync("/api/v1/merge", form, fallback,
                                           "X-Reparatio-Warning", false, ct)
                             .ConfigureAwait(false);
            }
        }

        // ── append ────────────────────────────────────────────────────────

        /// <summary>Stack rows from ≥2 files vertically.</summary>
        public async Task<ReparatioResult> AppendAsync(
            IList<string> filePaths,
            string        targetFormat,
            bool          noHeader    = false,
            bool          fixEncoding = true,
            CancellationToken ct      = default(CancellationToken))
        {
            if (filePaths == null || filePaths.Count < 2)
                throw new ArgumentException("At least 2 files are required for AppendAsync");
            using (var form = new MultipartFormDataContent())
            {
                foreach (var path in filePaths)
                {
                    byte[] bytes = File.ReadAllBytes(path);
                    AddFile(form, "files", bytes, Path.GetFileName(path));
                }
                form.Add(new StringContent(targetFormat),                   "target_format");
                form.Add(new StringContent(noHeader    ? "true" : "false"), "no_header");
                form.Add(new StringContent(fixEncoding ? "true" : "false"), "fix_encoding");
                return await PostFileAsync("/api/v1/append", form,
                                           "appended." + targetFormat,
                                           "X-Reparatio-Warning", false, ct)
                             .ConfigureAwait(false);
            }
        }

        // ── query ─────────────────────────────────────────────────────────

        /// <summary>Run SQL against a file (table name: data).</summary>
        public async Task<ReparatioResult> QueryAsync(
            string filePath,
            string sql,
            string targetFormat = "csv",
            bool   noHeader     = false,
            bool   fixEncoding  = true,
            string delimiter    = "",
            string sheet        = "",
            CancellationToken ct = default(CancellationToken))
        {
            byte[] bytes    = File.ReadAllBytes(filePath);
            string filename = Path.GetFileName(filePath);
            using (var form = new MultipartFormDataContent())
            {
                AddFile(form, "file", bytes, filename);
                form.Add(new StringContent(sql),                            "sql");
                form.Add(new StringContent(targetFormat),                   "target_format");
                form.Add(new StringContent(noHeader    ? "true" : "false"), "no_header");
                form.Add(new StringContent(fixEncoding ? "true" : "false"), "fix_encoding");
                form.Add(new StringContent(delimiter),                      "delimiter");
                form.Add(new StringContent(sheet),                          "sheet");
                string baseName = Path.GetFileNameWithoutExtension(filename);
                string fallback = baseName + "_query." + targetFormat;
                return await PostFileAsync("/api/v1/query", form, fallback,
                                           null, false, ct)
                             .ConfigureAwait(false);
            }
        }

        // ── IDisposable ───────────────────────────────────────────────────

        public void Dispose()
        {
            if (!_disposed) { _http.Dispose(); _disposed = true; }
        }

        // ── Internal helpers ──────────────────────────────────────────────

        private async Task<Dictionary<string, object>> GetJsonAsync(string path,
                                                                     CancellationToken ct)
        {
            var resp = await _http.GetAsync(_baseUrl + path, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            RaiseForStatus((int)resp.StatusCode, body);
            return ParseJson(body);
        }

        private async Task<Dictionary<string, object>> PostJsonAsync(
            string path, MultipartFormDataContent form, CancellationToken ct)
        {
            var resp = await _http.PostAsync(_baseUrl + path, form, ct).ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            RaiseForStatus((int)resp.StatusCode, body);
            return ParseJson(body);
        }

        private async Task<ReparatioResult> PostFileAsync(string path,
                                                           MultipartFormDataContent form,
                                                           string fallbackFilename,
                                                           string warningHeader,
                                                           bool   urlDecodeWarning,
                                                           CancellationToken ct)
        {
            var resp = await _http.PostAsync(_baseUrl + path, form, ct).ConfigureAwait(false);
            byte[] body = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                RaiseForStatus((int)resp.StatusCode,
                               Encoding.UTF8.GetString(body));
            string filename = FilenameFromHeaders(resp, fallbackFilename);
            string warning  = null;
            if (warningHeader != null)
            {
                IEnumerable<string> vals;
                if (resp.Headers.TryGetValues(warningHeader, out vals))
                {
                    string raw = string.Join("", vals);
                    warning = urlDecodeWarning ? Uri.UnescapeDataString(raw) : raw;
                }
            }
            return new ReparatioResult(body, filename, warning);
        }

        internal static void RaiseForStatus(int status, string body)
        {
            if (status < 400) return;
            string detail = body;
            try {
                var d = ParseJson(body);
                object dv;
                if (d.TryGetValue("detail", out dv) && dv != null)
                    detail = dv.ToString();
            } catch { }
            switch (status) {
                case 401: case 403: throw new AuthenticationException(status, detail);
                case 402:           throw new InsufficientPlanException(detail);
                case 413:           throw new FileTooLargeException(detail);
                case 422:           throw new ParseException(detail);
                default:            throw new ReparatioException(status, detail);
            }
        }

        private static string FilenameFromHeaders(HttpResponseMessage resp, string fallback)
        {
            ContentDispositionHeaderValue cd = resp.Content.Headers.ContentDisposition;
            if (cd != null && cd.FileName != null)
                return cd.FileName.Trim('"');
            IEnumerable<string> cdVals;
            if (resp.Headers.TryGetValues("Content-Disposition", out cdVals))
            {
                var m = CdFilenameRe.Match(string.Join("", cdVals));
                if (m.Success) return m.Groups[1].Value;
            }
            return fallback;
        }

        private static void AddFile(MultipartFormDataContent form,
                                    string fieldName, byte[] bytes, string filename,
                                    string contentType = "application/octet-stream")
        {
            var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            form.Add(content, fieldName, filename);
        }

        internal static Dictionary<string, object> ParseJson(string json)
        {
            var ser = new JavaScriptSerializer();
            return ser.Deserialize<Dictionary<string, object>>(json);
        }

        internal static string ToJsonArray(IList<string> list)
        {
            if (list == null || list.Count == 0) return "[]";
            var sb = new StringBuilder("[");
            for (int i = 0; i < list.Count; i++) {
                if (i > 0) sb.Append(',');
                sb.Append('"');
                sb.Append(list[i].Replace("\\", "\\\\").Replace("\"", "\\\""));
                sb.Append('"');
            }
            sb.Append(']');
            return sb.ToString();
        }
    }
}
