using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Reparatio;

// ── Minimal test framework ────────────────────────────────────────────────────

static class Assert
{
    public static void Equal<T>(T expected, T actual, string ctx = "")
    {
        if (!Equals(expected, actual))
            Fail(string.Format("Expected <{0}> but got <{1}>{2}",
                 expected, actual, ctx.Length > 0 ? "  [" + ctx + "]" : ""));
    }
    public static void True(bool cond, string ctx = "")
    {
        if (!cond) Fail("Expected true" + (ctx.Length > 0 ? "  [" + ctx + "]" : ""));
    }
    public static void Null(object v, string ctx = "")
    {
        if (v != null) Fail("Expected null, got <" + v + ">" +
                             (ctx.Length > 0 ? "  [" + ctx + "]" : ""));
    }
    public static void NotNull(object v, string ctx = "")
    {
        if (v == null) Fail("Expected non-null" + (ctx.Length > 0 ? "  [" + ctx + "]" : ""));
    }
    public static void Throws<T>(Action action, string ctx = "") where T : Exception
    {
        try {
            action();
            Fail(typeof(T).Name + " not thrown" +
                 (ctx.Length > 0 ? "  [" + ctx + "]" : ""));
        } catch (T) { /* expected */ }
        catch (AggregateException ae) when (ae.InnerException is T) { /* expected */ }
        catch (Exception ex) {
            Fail("Expected " + typeof(T).Name + " but got " + ex.GetType().Name +
                 ": " + ex.Message + (ctx.Length > 0 ? "  [" + ctx + "]" : ""));
        }
    }
    private static void Fail(string msg) { throw new Exception("ASSERTION FAILED: " + msg); }
}

static class TestRunner
{
    static int _pass, _fail;

    public static void Run(string name, Action test)
    {
        try { test(); Console.WriteLine("  PASS  " + name); _pass++; }
        catch (Exception e) {
            Console.WriteLine("  FAIL  " + name);
            Console.WriteLine("        " + e.Message);
            _fail++;
        }
    }

    public static void RunAsync(string name, Func<Task> test)
        => Run(name, () => test().GetAwaiter().GetResult());

    public static int Summary()
    {
        Console.WriteLine();
        Console.WriteLine(string.Format("Results: {0} passed, {1} failed out of {2} tests.",
                                        _pass, _fail, _pass + _fail));
        return _fail > 0 ? 1 : 0;
    }
}

// ── Fake HttpMessageHandler ───────────────────────────────────────────────────

class FakeHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;
    public HttpRequestMessage LastRequest;

    public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) { _fn = fn; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage req, CancellationToken ct)
    {
        LastRequest = req;
        return Task.FromResult(_fn(req));
    }

    public static HttpResponseMessage Json(string body, int status = 200)
    {
        var r = new HttpResponseMessage((HttpStatusCode)status);
        r.Content = new StringContent(body, Encoding.UTF8, "application/json");
        return r;
    }

    public static HttpResponseMessage Bytes(byte[] body, int status = 200,
        string contentDisposition = null, string warningHeader = null)
    {
        var r = new HttpResponseMessage((HttpStatusCode)status);
        r.Content = new ByteArrayContent(body);
        if (contentDisposition != null)
            r.Content.Headers.TryAddWithoutValidation(
                "Content-Disposition", contentDisposition);
        if (warningHeader != null)
            r.Headers.TryAddWithoutValidation("X-Reparatio-Warning", warningHeader);
        return r;
    }
}

static class ClientFactory
{
    public static ReparatioClient WithHandler(FakeHandler h, string apiKey = "rp_test")
    {
        var c = new ReparatioClient(apiKey, "https://reparatio.app",
                                    TimeSpan.FromSeconds(30));
        c._http.Dispose();
        c._http = new HttpClient(h);
        if (!string.IsNullOrEmpty(apiKey))
            c._http.DefaultRequestHeaders.Add("X-API-Key", apiKey);
        return c;
    }
}

static class Fixtures
{
    public static readonly byte[] CsvBytes =
        Encoding.UTF8.GetBytes("id,name\n1,Alice\n2,Bob\n");
    public static readonly byte[] ZipMagic = { 0x50, 0x4B, 0x03, 0x04 };

    public const string FormatsJson =
        "{\"input\":[\"csv\",\"xlsx\"],\"output\":[\"csv\",\"parquet\"]}";
    public const string MeJson =
        "{\"email\":\"user@example.com\",\"plan\":\"pro\",\"request_count\":42}";
    public const string InspectJson =
        "{\"filename\":\"data.csv\",\"detected_encoding\":\"utf-8\",\"rows_total\":100}";

    public static string TempCsv(string name = "data.csv")
    {
        string p = Path.Combine(Path.GetTempPath(), name);
        File.WriteAllBytes(p, CsvBytes);
        return p;
    }
    public static string TempZip()
    {
        string p = Path.Combine(Path.GetTempPath(), "archive.zip");
        File.WriteAllBytes(p, ZipMagic);
        return p;
    }
}

class Program
{
    static int Main()
    {
        Console.WriteLine("Running Reparatio C# SDK tests...\n");

        // ── Exception hierarchy ────────────────────────────────────────

        TestRunner.Run("Exception_StatusAndMessage", () => {
            var e = new ReparatioException(404, "not found");
            Assert.Equal(404, e.StatusCode);
            Assert.Equal("not found", e.Message);
        });
        TestRunner.Run("AuthException_IsReparatioException", () => {
            var e = new AuthenticationException(401, "bad key");
            Assert.Equal(401, e.StatusCode);
            Assert.True(e is ReparatioException);
        });
        TestRunner.Run("AuthException_403", () =>
            Assert.Equal(403, new AuthenticationException(403,"x").StatusCode));
        TestRunner.Run("PlanException_402", () =>
            Assert.Equal(402, new InsufficientPlanException("x").StatusCode));
        TestRunner.Run("SizeException_413", () =>
            Assert.Equal(413, new FileTooLargeException("x").StatusCode));
        TestRunner.Run("ParseException_422", () =>
            Assert.Equal(422, new ParseException("x").StatusCode));

        // ── ReparatioResult ────────────────────────────────────────────

        TestRunner.Run("Result_Accessors", () => {
            var r = new ReparatioResult(Fixtures.CsvBytes, "out.csv", "warn");
            Assert.Equal("out.csv", r.Filename);
            Assert.Equal("warn",    r.Warning);
            Assert.Equal(Fixtures.CsvBytes.Length, r.Content.Length);
        });
        TestRunner.Run("Result_NullWarning", () =>
            Assert.Null(new ReparatioResult(new byte[0], "x.csv").Warning));

        // ── RaiseForStatus ─────────────────────────────────────────────

        TestRunner.Run("RaiseForStatus_200_OK", () =>
            ReparatioClient.RaiseForStatus(200, "{}"));
        TestRunner.Run("RaiseForStatus_401", () =>
            Assert.Throws<AuthenticationException>(
                () => ReparatioClient.RaiseForStatus(401, "{\"detail\":\"bad key\"}")));
        TestRunner.Run("RaiseForStatus_403", () =>
            Assert.Throws<AuthenticationException>(
                () => ReparatioClient.RaiseForStatus(403, "{\"detail\":\"forbidden\"}")));
        TestRunner.Run("RaiseForStatus_402", () =>
            Assert.Throws<InsufficientPlanException>(
                () => ReparatioClient.RaiseForStatus(402, "{\"detail\":\"Pro required\"}")));
        TestRunner.Run("RaiseForStatus_413", () =>
            Assert.Throws<FileTooLargeException>(
                () => ReparatioClient.RaiseForStatus(413, "{\"detail\":\"too large\"}")));
        TestRunner.Run("RaiseForStatus_422", () =>
            Assert.Throws<ParseException>(
                () => ReparatioClient.RaiseForStatus(422, "{\"detail\":\"bad format\"}")));
        TestRunner.Run("RaiseForStatus_500", () =>
            Assert.Throws<ReparatioException>(
                () => ReparatioClient.RaiseForStatus(500, "{\"detail\":\"oops\"}")));
        TestRunner.Run("RaiseForStatus_NonJson", () => {
            try { ReparatioClient.RaiseForStatus(503, "Service Unavailable"); }
            catch (ReparatioException e) {
                Assert.Equal("Service Unavailable", e.Message);
            }
        });
        TestRunner.Run("RaiseForStatus_StatusPreserved", () => {
            try { ReparatioClient.RaiseForStatus(503, "{\"detail\":\"down\"}"); }
            catch (ReparatioException e) { Assert.Equal(503, e.StatusCode); }
        });

        // ── ToJsonArray ────────────────────────────────────────────────

        TestRunner.Run("ToJsonArray_Null",   () =>
            Assert.Equal("[]", ReparatioClient.ToJsonArray(null)));
        TestRunner.Run("ToJsonArray_Empty",  () =>
            Assert.Equal("[]", ReparatioClient.ToJsonArray(new string[0])));
        TestRunner.Run("ToJsonArray_Single", () =>
            Assert.Equal("[\"id\"]", ReparatioClient.ToJsonArray(new[]{"id"})));
        TestRunner.Run("ToJsonArray_Multi",  () =>
            Assert.Equal("[\"a\",\"b\"]", ReparatioClient.ToJsonArray(new[]{"a","b"})));
        TestRunner.Run("ToJsonArray_Escapes", () => {
            string r = ReparatioClient.ToJsonArray(new[]{"say \"hi\""});
            Assert.True(r.Contains("\\\"hi\\\""), "escapes inner quotes");
        });

        // ── ParseJson ──────────────────────────────────────────────────

        TestRunner.Run("ParseJson_Roundtrip", () => {
            var d = ReparatioClient.ParseJson("{\"key\":\"val\",\"n\":42}");
            Assert.Equal("val", (string)d["key"]);
            Assert.Equal(42, (int)d["n"]);
        });

        // ── formats() ─────────────────────────────────────────────────

        TestRunner.RunAsync("Formats_ReturnsDict", async () => {
            var h = new FakeHandler(_ => FakeHandler.Json(Fixtures.FormatsJson));
            using (var c = ClientFactory.WithHandler(h)) {
                var r = await c.FormatsAsync();
                Assert.True(r.ContainsKey("input"),  "has input");
                Assert.True(r.ContainsKey("output"), "has output");
            }
        });
        TestRunner.RunAsync("Formats_SendsApiKey", async () => {
            HttpRequestMessage req = null;
            var h = new FakeHandler(r => { req = r; return FakeHandler.Json(Fixtures.FormatsJson); });
            using (var c = ClientFactory.WithHandler(h, "rp_mykey")) {
                await c.FormatsAsync();
                IEnumerable<string> vals;
                Assert.True(req.Headers.TryGetValues("X-API-Key", out vals));
                Assert.Equal("rp_mykey", string.Join("", vals));
            }
        });
        TestRunner.RunAsync("Formats_UsesGetMethod", async () => {
            HttpMethod method = null;
            var h = new FakeHandler(r => { method = r.Method;
                                           return FakeHandler.Json(Fixtures.FormatsJson); });
            using (var c = ClientFactory.WithHandler(h)) {
                await c.FormatsAsync();
                Assert.Equal(HttpMethod.Get, method);
            }
        });
        TestRunner.RunAsync("Formats_CorrectUrl", async () => {
            string url = null;
            var h = new FakeHandler(r => { url = r.RequestUri.ToString();
                                           return FakeHandler.Json(Fixtures.FormatsJson); });
            using (var c = ClientFactory.WithHandler(h)) {
                await c.FormatsAsync();
                Assert.True(url.EndsWith("/api/v1/formats"), "correct path");
            }
        });
        TestRunner.RunAsync("Formats_401_Throws", async () => {
            var h = new FakeHandler(_ => FakeHandler.Json("{\"detail\":\"bad key\"}", 401));
            using (var c = ClientFactory.WithHandler(h)) {
                Assert.Throws<AuthenticationException>(() => c.FormatsAsync().Wait());
            }
        });
        TestRunner.RunAsync("Formats_402_Throws", async () => {
            var h = new FakeHandler(_ => FakeHandler.Json("{\"detail\":\"Pro\"}", 402));
            using (var c = ClientFactory.WithHandler(h)) {
                Assert.Throws<InsufficientPlanException>(() => c.FormatsAsync().Wait());
            }
        });
        TestRunner.RunAsync("Formats_500_Throws", async () => {
            var h = new FakeHandler(_ => FakeHandler.Json("{\"detail\":\"err\"}", 500));
            using (var c = ClientFactory.WithHandler(h)) {
                Assert.Throws<ReparatioException>(() => c.FormatsAsync().Wait());
            }
        });

        // ── me() ──────────────────────────────────────────────────────

        TestRunner.RunAsync("Me_ReturnsDict", async () => {
            var h = new FakeHandler(_ => FakeHandler.Json(Fixtures.MeJson));
            using (var c = ClientFactory.WithHandler(h)) {
                var r = await c.MeAsync();
                Assert.Equal("user@example.com", (string)r["email"]);
                Assert.Equal(42, (int)r["request_count"]);
            }
        });

        // ── convert() ─────────────────────────────────────────────────

        TestRunner.RunAsync("Convert_ReturnsResult", async () => {
            string tmp = Fixtures.TempCsv("conv.csv");
            try {
                var h = new FakeHandler(_ =>
                    FakeHandler.Bytes(Fixtures.CsvBytes, 200,
                                      "attachment; filename=\"output.parquet\""));
                using (var c = ClientFactory.WithHandler(h)) {
                    var r = await c.ConvertAsync(tmp, "parquet");
                    Assert.Equal("output.parquet", r.Filename);
                    Assert.Equal(Fixtures.CsvBytes.Length, r.Content.Length);
                    Assert.Null(r.Warning);
                }
            } finally { if (File.Exists(tmp)) File.Delete(tmp); }
        });
        TestRunner.RunAsync("Convert_FallbackFilename", async () => {
            string tmp = Fixtures.TempCsv("mydata.csv");
            try {
                var h = new FakeHandler(_ => FakeHandler.Bytes(Fixtures.CsvBytes));
                using (var c = ClientFactory.WithHandler(h)) {
                    var r = await c.ConvertAsync(tmp, "json");
                    Assert.Equal("mydata.json", r.Filename);
                }
            } finally { if (File.Exists(tmp)) File.Delete(tmp); }
        });
        TestRunner.RunAsync("Convert_WarningHeader", async () => {
            string tmp = Fixtures.TempCsv("warn.csv");
            try {
                var h = new FakeHandler(_ =>
                    FakeHandler.Bytes(Fixtures.CsvBytes, 200, null, "truncated to 10000 rows"));
                using (var c = ClientFactory.WithHandler(h)) {
                    var r = await c.ConvertAsync(tmp, "csv");
                    Assert.Equal("truncated to 10000 rows", r.Warning);
                }
            } finally { if (File.Exists(tmp)) File.Delete(tmp); }
        });
        TestRunner.RunAsync("Convert_SendsTargetFormat", async () => {
            string tmp = Fixtures.TempCsv("tf.csv");
            try {
                string body = null;
                var h = new FakeHandler(req => {
                    body = req.Content.ReadAsStringAsync().Result;
                    return FakeHandler.Bytes(Fixtures.CsvBytes);
                });
                using (var c = ClientFactory.WithHandler(h)) {
                    await c.ConvertAsync(tmp, "xlsx");
                    Assert.True(body.Contains("xlsx"), "target_format in body");
                }
            } finally { if (File.Exists(tmp)) File.Delete(tmp); }
        });
        TestRunner.RunAsync("Convert_SendsNullValues", async () => {
            string tmp = Fixtures.TempCsv("nv.csv");
            try {
                string body = null;
                var h = new FakeHandler(req => {
                    body = req.Content.ReadAsStringAsync().Result;
                    return FakeHandler.Bytes(Fixtures.CsvBytes);
                });
                using (var c = ClientFactory.WithHandler(h)) {
                    await c.ConvertAsync(tmp, "csv", nullValues: new[]{"NULL","NA"});
                    Assert.True(body.Contains("NULL"), "null_values in body");
                }
            } finally { if (File.Exists(tmp)) File.Delete(tmp); }
        });
        TestRunner.RunAsync("Convert_SendsEncodingOverride", async () => {
            string tmp = Fixtures.TempCsv("eo.csv");
            try {
                string body = null;
                var h = new FakeHandler(req => {
                    body = req.Content.ReadAsStringAsync().Result;
                    return FakeHandler.Bytes(Fixtures.CsvBytes);
                });
                using (var c = ClientFactory.WithHandler(h)) {
                    await c.ConvertAsync(tmp, "csv", encodingOverride: "cp037");
                    Assert.True(body.Contains("cp037"), "encoding_override in body");
                }
            } finally { if (File.Exists(tmp)) File.Delete(tmp); }
        });
        TestRunner.RunAsync("Convert_401_Throws", async () => {
            string tmp = Fixtures.TempCsv("auth.csv");
            try {
                var h = new FakeHandler(_ => FakeHandler.Bytes(
                    Encoding.UTF8.GetBytes("{\"detail\":\"bad key\"}"), 401));
                using (var c = ClientFactory.WithHandler(h, "bad")) {
                    Assert.Throws<AuthenticationException>(
                        () => c.ConvertAsync(tmp, "parquet").Wait());
                }
            } finally { if (File.Exists(tmp)) File.Delete(tmp); }
        });

        // ── append() ──────────────────────────────────────────────────

        TestRunner.RunAsync("Append_RequiresTwoFiles", async () => {
            using (var c = new ReparatioClient("rp_k")) {
                Assert.Throws<ArgumentException>(
                    () => c.AppendAsync(new[]{"one.csv"}, "csv").Wait());
            }
        });
        TestRunner.RunAsync("Append_NullFiles_Throws", async () => {
            using (var c = new ReparatioClient("rp_k")) {
                Assert.Throws<ArgumentException>(
                    () => c.AppendAsync(null, "csv").Wait());
            }
        });

        // ── query() ───────────────────────────────────────────────────

        TestRunner.RunAsync("Query_FallbackFilename", async () => {
            string tmp = Fixtures.TempCsv("sales.csv");
            try {
                var h = new FakeHandler(_ => FakeHandler.Bytes(Fixtures.CsvBytes));
                using (var c = ClientFactory.WithHandler(h)) {
                    var r = await c.QueryAsync(tmp, "SELECT 1", "parquet");
                    Assert.Equal("sales_query.parquet", r.Filename);
                }
            } finally { if (File.Exists(tmp)) File.Delete(tmp); }
        });
        TestRunner.RunAsync("Query_SendsSql", async () => {
            string tmp = Fixtures.TempCsv("qsql.csv");
            const string sql = "SELECT region, SUM(revenue) FROM data GROUP BY region";
            try {
                string body = null;
                var h = new FakeHandler(req => {
                    body = req.Content.ReadAsStringAsync().Result;
                    return FakeHandler.Bytes(Fixtures.CsvBytes);
                });
                using (var c = ClientFactory.WithHandler(h)) {
                    await c.QueryAsync(tmp, sql, "csv");
                    Assert.True(body.Contains("region"), "sql in body");
                }
            } finally { if (File.Exists(tmp)) File.Delete(tmp); }
        });

        // ── merge() ───────────────────────────────────────────────────

        TestRunner.RunAsync("Merge_FallbackFilename", async () => {
            string f1 = Fixtures.TempCsv("sales.csv");
            string f2 = Fixtures.TempCsv("costs.csv");
            try {
                var h = new FakeHandler(_ => FakeHandler.Bytes(Fixtures.CsvBytes));
                using (var c = ClientFactory.WithHandler(h)) {
                    var r = await c.MergeAsync(f1, f2, "outer", "csv");
                    Assert.Equal("sales_outer_costs.csv", r.Filename);
                }
            } finally { File.Delete(f1); File.Delete(f2); }
        });
        TestRunner.RunAsync("Merge_SendsOperation", async () => {
            string f1 = Fixtures.TempCsv("a.csv");
            string f2 = Fixtures.TempCsv("b.csv");
            try {
                string body = null;
                var h = new FakeHandler(req => {
                    body = req.Content.ReadAsStringAsync().Result;
                    return FakeHandler.Bytes(Fixtures.CsvBytes);
                });
                using (var c = ClientFactory.WithHandler(h)) {
                    await c.MergeAsync(f1, f2, "inner", "csv", joinOn: "id");
                    Assert.True(body.Contains("inner"), "operation in body");
                    Assert.True(body.Contains("id"),    "join_on in body");
                }
            } finally { File.Delete(f1); File.Delete(f2); }
        });

        // ── batch-convert() ───────────────────────────────────────────

        TestRunner.RunAsync("BatchConvert_ReturnsResult", async () => {
            string tmp = Fixtures.TempZip();
            try {
                var h = new FakeHandler(_ => FakeHandler.Bytes(
                    Fixtures.ZipMagic, 200,
                    "attachment; filename=\"converted.zip\""));
                using (var c = ClientFactory.WithHandler(h)) {
                    var r = await c.BatchConvertAsync(tmp, "parquet");
                    Assert.Equal("converted.zip", r.Filename);
                }
            } finally { if (File.Exists(tmp)) File.Delete(tmp); }
        });
        TestRunner.RunAsync("BatchConvert_ErrorsHeader_InWarning", async () => {
            string tmp = Fixtures.TempZip();
            try {
                var resp = new HttpResponseMessage(HttpStatusCode.OK);
                resp.Content = new ByteArrayContent(Fixtures.ZipMagic);
                resp.Headers.TryAddWithoutValidation("X-Reparatio-Errors",
                    "%5B%7B%22file%22%3A%22bad.txt%22%7D%5D");
                var h = new FakeHandler(_ => resp);
                using (var c = ClientFactory.WithHandler(h)) {
                    var r = await c.BatchConvertAsync(tmp, "csv");
                    Assert.True(r.Warning.Contains("bad.txt"), "warning contains filename");
                }
            } finally { if (File.Exists(tmp)) File.Delete(tmp); }
        });

        // ── inspect() ─────────────────────────────────────────────────

        TestRunner.RunAsync("Inspect_ReturnsDict", async () => {
            string tmp = Fixtures.TempCsv("insp.csv");
            try {
                var h = new FakeHandler(_ => FakeHandler.Json(Fixtures.InspectJson));
                using (var c = ClientFactory.WithHandler(h)) {
                    var r = await c.InspectAsync(tmp);
                    Assert.Equal("utf-8", (string)r["detected_encoding"]);
                    Assert.Equal(100, (int)r["rows_total"]);
                }
            } finally { if (File.Exists(tmp)) File.Delete(tmp); }
        });

        return TestRunner.Summary();
    }
}
