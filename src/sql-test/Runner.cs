using System.Data;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using AdoNetCore.AseClient;
using ibsCompiler.Configuration;

namespace SqlTest;

/// <summary>
/// Discovers and executes SQL unit-test procs.
///
/// Classification model (priority: FAIL > SKIP > ERROR > PASS):
/// - FAIL is signalled by `raiserror 50001` (the preceding `print 'FAIL: …'`
///   line is captured via InfoMessage and used as the failure message).
/// - SKIP is signalled by `raiserror 50002`.
/// - Any other severity-11+ Sybase error is ERROR.
/// - No exception means PASS.
///
/// Two test shapes are supported:
/// - Singleton: one `test_<name>` proc; runner ExecuteNonQuery's it inside
///   begin tran/rollback tran.
/// - Paired: `test_<name>_capture` + `test_<name>_assert`. The capture proc
///   emits a result set which the runner streams into a permanent capture
///   table (auto-introspected and created on first use via SET FMTONLY ON
///   + IDataReader.GetSchemaTable). The assert proc then runs assertions
///   against the capture table. Both procs run inside the same transaction
///   wrap and the table is cleared by the rollback at end-of-test.
/// </summary>
public class Runner
{
    private const int FailErrorNumber = 50001;
    private const int SkipErrorNumber = 50002;

    private static readonly Regex CaptureIntoRe   = new(@"^\s*--\s*@capture-into\s*:\s*(\S+)",     RegexOptions.IgnoreCase | RegexOptions.Multiline);
    private static readonly Regex CaptureSourceRe = new(@"^\s*--\s*@capture-source\s*:\s*(.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private readonly ResolvedProfile _profile;
    private readonly Options _opts;

    public Runner(ResolvedProfile profile, Options opts)
    {
        _profile = profile;
        _opts = opts;
        // Sybase TDS may negotiate a non-UTF8 charset (e.g. cp850); without this
        // the InfoMessage stream throws "unsupported charset" on connection.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public List<TestCase> Discover()
    {
        var procNames = QueryProcNames();
        var cases = new List<TestCase>();
        var paired = new HashSet<string>();

        // Pair: for each `*_capture`, find matching `*_assert`.
        foreach (var name in procNames)
        {
            if (!name.EndsWith("_capture", StringComparison.OrdinalIgnoreCase)) continue;
            var baseName = name[..^"_capture".Length];
            var assertName = baseName + "_assert";
            if (!procNames.Contains(assertName)) continue;

            var spec = ParseCaptureSpec(name);
            cases.Add(new TestCase(baseName, name, assertName, spec));
            paired.Add(name);
            paired.Add(assertName);
        }

        // Singletons: everything not consumed by pairing.
        foreach (var name in procNames)
        {
            if (paired.Contains(name)) continue;
            cases.Add(new TestCase(name, null, name, null));
        }

        return cases.OrderBy(c => c.LogicalName).ToList();
    }

    private HashSet<string> QueryProcNames()
    {
        using var conn = new AseConnection(BuildConnectionString(_opts.Database));
        conn.Open();
        using var cmd = new AseCommand(
            "select name from sysobjects " +
            "where type = 'P' and name like @pat escape '\\' " +
            "order by name",
            conn);
        cmd.Parameters.Add("@pat", _opts.Pattern);

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) names.Add(reader.GetString(0));
        return names;
    }

    private CaptureSpec? ParseCaptureSpec(string procName)
    {
        var body = FetchProcBody(procName);
        if (body == null) return null;

        var intoMatch   = CaptureIntoRe.Match(body);
        var sourceMatch = CaptureSourceRe.Match(body);
        if (!intoMatch.Success || !sourceMatch.Success) return null;

        return new CaptureSpec(
            IntoTable:  intoMatch.Groups[1].Value.Trim(),
            SourceCall: sourceMatch.Groups[1].Value.Trim());
    }

    private string? FetchProcBody(string procName)
    {
        using var conn = new AseConnection(BuildConnectionString(_opts.Database));
        conn.Open();
        using var cmd = new AseCommand(
            "select text from syscomments " +
            "where id = object_id(@n) order by colid2, colid",
            conn);
        cmd.Parameters.Add("@n", procName);

        var sb = new StringBuilder();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0)) sb.Append(reader.GetString(0));
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    public TestResult RunOne(TestCase tc)
    {
        var stopwatch = Stopwatch.StartNew();
        var messages = new List<AseError>();

        // Step 0 (pre-tran): ensure the capture table exists. DDL must
        // happen outside the test's begin tran/rollback wrap because
        // ddl in tran is off on sbntest.
        if (tc.Capture != null)
        {
            try { EnsureCaptureTable(tc.Capture); }
            catch (Exception ex)
            {
                return new TestResult(tc.LogicalName, Outcome.ERROR,
                    $"capture table setup failed: {ex.Message}",
                    stopwatch.Elapsed.TotalSeconds, ex.ToString());
            }
        }

        using var conn = new AseConnection(BuildConnectionString(_opts.Database));
        conn.InfoMessage += (_, e) =>
        {
            foreach (AseError err in e.Errors)
            {
                if (err.Severity >= 11) continue;
                var msg = err.Message ?? "";
                if (msg.StartsWith("Changed client character set") ||
                    msg.StartsWith("Changed database context") ||
                    msg.StartsWith("Changed language setting"))
                    continue;
                messages.Add(err);
            }
        };

        try { conn.Open(); }
        catch (Exception ex)
        {
            return new TestResult(tc.LogicalName, Outcome.ERROR,
                $"connection failed: {ex.Message}",
                stopwatch.Elapsed.TotalSeconds, ex.ToString());
        }

        using var tx = conn.BeginTransaction();
        try
        {
            if (tc.CaptureProc != null && tc.Capture != null)
                RunCapturePhase(conn, tx, tc.CaptureProc, tc.Capture, _opts.TimeoutSeconds);

            using var cmd = new AseCommand($"exec {tc.AssertProc}", conn, tx);
            cmd.CommandTimeout = _opts.TimeoutSeconds;
            cmd.ExecuteNonQuery();

            try { tx.Rollback(); } catch { }
            stopwatch.Stop();
            return new TestResult(tc.LogicalName, Outcome.PASS, "",
                stopwatch.Elapsed.TotalSeconds, JoinMessages(messages));
        }
        catch (AseException ex)
        {
            try { tx.Rollback(); } catch { }
            stopwatch.Stop();
            return Classify(tc.LogicalName, ex, messages, stopwatch.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            try { tx.Rollback(); } catch { }
            stopwatch.Stop();
            var outcome = ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                ? Outcome.TIMEOUT : Outcome.ERROR;
            return new TestResult(tc.LogicalName, outcome, ex.Message,
                stopwatch.Elapsed.TotalSeconds, ex.ToString());
        }
    }

    private void RunCapturePhase(AseConnection conn, AseTransaction tx,
                                 string captureProc, CaptureSpec spec, int timeout)
    {
        using var cmd = new AseCommand($"exec {captureProc}", conn, tx);
        cmd.CommandTimeout = timeout;

        // Read the test's result set(s) and write rows into the capture table.
        // Multiple result sets are concatenated into the same target.
        using var reader = cmd.ExecuteReader();
        do
        {
            if (reader.FieldCount == 0) continue;
            CopyResultSetIntoTable(reader, conn, tx, spec.IntoTable);
        } while (reader.NextResult());
    }

    private void CopyResultSetIntoTable(IDataReader reader, AseConnection conn,
                                         AseTransaction tx, string targetTable)
    {
        var cols = new string[reader.FieldCount];
        var placeholders = new string[reader.FieldCount];
        for (int i = 0; i < reader.FieldCount; i++)
        {
            cols[i] = QuoteIdent(reader.GetName(i));
            placeholders[i] = $"@p{i}";
        }
        var insertSql =
            $"insert into {targetTable} ({string.Join(", ", cols)}) " +
            $"values ({string.Join(", ", placeholders)})";

        while (reader.Read())
        {
            using var insertCmd = new AseCommand(insertSql, conn, tx);
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var val = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
                insertCmd.Parameters.Add($"@p{i}", val);
            }
            insertCmd.ExecuteNonQuery();
        }
    }

    public void EnsureCaptureTable(CaptureSpec spec)
    {
        using var conn = new AseConnection(BuildConnectionString(_opts.Database));
        conn.Open();

        if (TableExists(conn, spec.IntoTable)) return;

        var schema = IntrospectResultSetSchema(conn, spec.SourceCall);
        var ddl = BuildCreateTable(spec.IntoTable, schema);
        using var cmd = new AseCommand(ddl, conn);
        cmd.ExecuteNonQuery();
    }

    private static bool TableExists(AseConnection conn, string tableName)
    {
        using var cmd = new AseCommand(
            "select 1 from sysobjects where type = 'U' and name = @n", conn);
        cmd.Parameters.Add("@n", tableName);
        return cmd.ExecuteScalar() != null;
    }

    /// <summary>
    /// Issue `set fmtonly on; exec <source>; set fmtonly off` and return
    /// the result-set schema. FMTONLY tells the server to return column
    /// metadata without executing the proc body. Works on both Sybase ASE
    /// and SQL Server (deprecated on the latter but still functional;
    /// future enhancement: switch MSSQL to sp_describe_first_result_set).
    /// </summary>
    private DataTable IntrospectResultSetSchema(AseConnection conn, string sourceCall)
    {
        var sql = $"set fmtonly on\n{sourceCall}\nset fmtonly off";
        using var cmd = new AseCommand(sql, conn);
        using var reader = cmd.ExecuteReader(CommandBehavior.SchemaOnly);
        var schema = reader.GetSchemaTable()
            ?? throw new InvalidOperationException(
                $"FMTONLY returned no schema for: {sourceCall}");
        return schema;
    }

    private static string BuildCreateTable(string tableName, DataTable schema)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"create table {tableName} (");
        for (int i = 0; i < schema.Rows.Count; i++)
        {
            var row      = schema.Rows[i];
            var name     = (string)row["ColumnName"];
            var type     = (Type)row["DataType"];
            var size     = row["ColumnSize"]      is DBNull ? -1 : Convert.ToInt32(row["ColumnSize"]);
            var prec     = row["NumericPrecision"] is DBNull ? 0  : Convert.ToInt32(row["NumericPrecision"]);
            var scale    = row["NumericScale"]    is DBNull ? 0  : Convert.ToInt32(row["NumericScale"]);
            var nullable = row["AllowDBNull"]     is DBNull || Convert.ToBoolean(row["AllowDBNull"]);

            sb.Append($"  {QuoteIdent(name)} {MapType(type, size, prec, scale)}");
            sb.Append(nullable ? " null" : " not null");
            sb.AppendLine(i == schema.Rows.Count - 1 ? "" : ",");
        }
        sb.AppendLine(")");
        return sb.ToString();
    }

    private static string MapType(Type t, int size, int prec, int scale)
    {
        if (t == typeof(int))      return "int";
        if (t == typeof(long))     return "bigint";
        if (t == typeof(short))    return "smallint";
        if (t == typeof(byte))     return "tinyint";
        if (t == typeof(bool))     return "bit";
        if (t == typeof(decimal))  return prec > 0 ? $"numeric({prec},{scale})" : "numeric(18,4)";
        if (t == typeof(double))   return "float";
        if (t == typeof(float))    return "real";
        if (t == typeof(DateTime)) return "datetime";
        if (t == typeof(byte[]))   return size > 0 ? $"varbinary({size})" : "varbinary(8000)";
        // string / fallback
        if (size <= 0 || size > 8000) return "varchar(8000)";
        return $"varchar({size})";
    }

    private static string QuoteIdent(string name)
    {
        // Sybase identifiers: reject control chars; otherwise return bare.
        // For column names with spaces or reserved words, wrap in brackets.
        if (name.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '#')) return name;
        return $"[{name.Replace("]", "]]")}]";
    }

    public string PrintCaptureDdl(CaptureSpec spec)
    {
        using var conn = new AseConnection(BuildConnectionString(_opts.Database));
        conn.Open();
        var schema = IntrospectResultSetSchema(conn, spec.SourceCall);
        return BuildCreateTable(spec.IntoTable, schema);
    }

    public void DropCaptureTable(string tableName)
    {
        using var conn = new AseConnection(BuildConnectionString(_opts.Database));
        conn.Open();
        if (!TableExists(conn, tableName)) return;
        using var cmd = new AseCommand($"drop table {tableName}", conn);
        cmd.ExecuteNonQuery();
    }

    private TestResult Classify(string name, AseException ex, List<AseError> info, double duration)
    {
        foreach (AseError err in ex.Errors)
        {
            if (err.MessageNumber == FailErrorNumber)
            {
                var msg = LastMatching(info, "FAIL:") ?? "FAIL";
                return new TestResult(name, Outcome.FAIL, msg, duration, JoinMessages(info, ex.Errors));
            }
            if (err.MessageNumber == SkipErrorNumber)
            {
                var msg = LastMatching(info, "SKIP:") ?? "SKIP";
                return new TestResult(name, Outcome.SKIP, msg, duration, JoinMessages(info, ex.Errors));
            }
        }

        var first = ex.Errors.Count > 0 ? ex.Errors[0] : null;
        var headline = first != null
            ? $"Msg {first.MessageNumber}, Level {first.Severity}: {first.Message}"
            : ex.Message;
        return new TestResult(name, Outcome.ERROR, headline, duration, JoinMessages(info, ex.Errors));
    }

    private static string? LastMatching(List<AseError> messages, string prefix)
    {
        for (int i = messages.Count - 1; i >= 0; i--)
        {
            var m = (messages[i].Message ?? "").TrimEnd();
            if (m.StartsWith(prefix)) return m;
        }
        return null;
    }

    private static string JoinMessages(List<AseError> info, AseErrorCollection? errors = null)
    {
        var sb = new StringBuilder();
        foreach (var m in info)
            sb.AppendLine((m.Message ?? "").TrimEnd());
        if (errors != null)
            foreach (AseError e in errors)
                sb.AppendLine($"Msg {e.MessageNumber}, Level {e.Severity}: {(e.Message ?? "").TrimEnd()}");
        return sb.ToString();
    }

    private string BuildConnectionString(string database)
    {
        var sb = new StringBuilder();
        sb.Append($"Data Source={_profile.Host}");
        sb.Append($";Port={_profile.Port}");
        sb.Append($";User ID={_profile.User}");
        sb.Append($";Password={_profile.Pass}");
        if (!string.IsNullOrEmpty(database))
            sb.Append($";Database={database}");
        sb.Append(";Pooling=false");
        return sb.ToString();
    }
}
