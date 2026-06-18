using System.Collections.Concurrent;
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
    private static readonly Regex NoTranRe        = new(@"^\s*--\s*@no-transaction\b",            RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private readonly ResolvedProfile _profile;
    private readonly Options _opts;

    // Per-session rebuild cache: each distinct capture table is dropped
    // and recreated exactly once on first reference. Eliminates schema
    // drift -- a stale capture table can never silently outlive a change
    // to the source proc's emitted shape. Cached value is the table's
    // column names + .NET types, used downstream for shape-matched
    // filtering during the capture phase.
    private readonly ConcurrentDictionary<string, Lazy<CaptureTableSchema>> _rebuiltTables =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record CaptureTableSchema(
        IReadOnlyList<string> ColumnNames,
        IReadOnlyList<Type>   ColumnTypes)
    {
        public static CaptureTableSchema FromIntrospectedSchema(DataTable schema)
        {
            var names = new List<string>(schema.Rows.Count);
            var types = new List<Type>(schema.Rows.Count);
            foreach (DataRow row in schema.Rows)
            {
                names.Add((string)row["ColumnName"]);
                types.Add((Type)row["DataType"]);
            }
            return new CaptureTableSchema(names, types);
        }
    }

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
        var pretests  = QueryPretests();
        var bodies    = FetchAllProcBodies();   // one round-trip; needed to read per-test directives
        var cases = new List<TestCase>();
        var consumed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Teardown procs (`<base>_teardown`) are cleanup hooks for @no-transaction
        // tests, never standalone tests — consume them up front so they don't get
        // discovered as their own `test_*`.
        var teardowns = procNames
            .Where(n => n.EndsWith("_teardown", StringComparison.OrdinalIgnoreCase))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var t in teardowns) consumed.Add(t);

        // Pair: for each `*_capture`, find matching `*_assert`.
        foreach (var name in procNames)
        {
            if (!name.EndsWith("_capture", StringComparison.OrdinalIgnoreCase)) continue;
            var baseName = name[..^"_capture".Length];
            var assertName = baseName + "_assert";
            if (!procNames.Contains(assertName)) continue;

            var body     = bodies.GetValueOrDefault(name);
            var spec     = ParseCaptureSpecFromBody(body);
            var noTran   = body != null && NoTranRe.IsMatch(body);
            var teardown = teardowns.Contains(baseName + "_teardown") ? baseName + "_teardown" : null;

            cases.Add(new TestCase(baseName, name, assertName, spec,
                                   ResolvePretest(baseName, pretests), noTran, teardown));
            consumed.Add(name);
            consumed.Add(assertName);
        }

        // Singletons: everything not consumed by pairing or teardown.
        foreach (var name in procNames)
        {
            if (consumed.Contains(name)) continue;
            var body     = bodies.GetValueOrDefault(name);
            var noTran   = body != null && NoTranRe.IsMatch(body);
            var teardown = teardowns.Contains(name + "_teardown") ? name + "_teardown" : null;
            cases.Add(new TestCase(name, null, name, null,
                                   ResolvePretest(name, pretests), noTran, teardown));
        }

        return cases.OrderBy(c => c.LogicalName).ToList();
    }

    /// <summary>
    /// Fetch every discovered test proc's full body text in a single round-trip,
    /// keyed by proc name. syscomments splits a proc body across rows (ordered by
    /// colid2, colid); we concatenate per object. Needed because the runner reads
    /// per-test directives (@capture-*, @no-transaction) from the body, and doing
    /// that per-proc would be one connection per test at discovery time.
    /// </summary>
    private Dictionary<string, string> FetchAllProcBodies()
    {
        using var conn = new AseConnection(BuildConnectionString(_opts.Database));
        conn.Open();
        using var cmd = new AseCommand(
            "select o.name, c.text from syscomments c " +
            "inner join sysobjects o on o.id = c.id " +
            "where o.type = 'P' and o.name like @pat escape '\\' " +
            "order by o.name, c.colid2, c.colid",
            conn);
        cmd.Parameters.Add("@pat", _opts.Pattern);

        var map = new Dictionary<string, StringBuilder>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (reader.IsDBNull(1)) continue;
            var name = reader.GetString(0);
            if (!map.TryGetValue(name, out var sb)) { sb = new StringBuilder(); map[name] = sb; }
            sb.Append(reader.GetString(1));
        }
        return map.ToDictionary(kv => kv.Key, kv => kv.Value.ToString(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Discover `pro_test_&lt;area&gt;_pretest` procs, mapping area → proc name.
    /// A pretest is auto-invoked before every test whose name begins
    /// `test_&lt;area&gt;_...`, inside the per-test tran (Go TestMain analog).
    /// Convention: each pretest takes a single `@tstuser_out varchar(8)
    /// output` param; the runner passes the captured value as the test's
    /// first positional parameter.
    /// </summary>
    private Dictionary<string, string> QueryPretests()
    {
        using var conn = new AseConnection(BuildConnectionString(_opts.Database));
        conn.Open();
        using var cmd = new AseCommand(
            "select name from sysobjects " +
            "where type = 'P' and name like 'pro\\_test\\_%\\_pretest' escape '\\'",
            conn);
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);            // pro_test_<area>_pretest
            var area = name["pro_test_".Length..^"_pretest".Length];
            if (area.Length > 0) map[area] = name;
        }
        return map;
    }

    /// <summary>
    /// Longest-prefix match: `test_passcard_verify_key_passcard_pk` resolves
    /// to `pro_test_passcard_verify_pretest` if that area is registered.
    /// Walks segment prefixes longest-first; first hit wins.
    /// </summary>
    private static string? ResolvePretest(string testName, Dictionary<string, string> pretests)
    {
        if (pretests.Count == 0) return null;
        if (!testName.StartsWith("test_", StringComparison.OrdinalIgnoreCase)) return null;
        var segments = testName["test_".Length..].Split('_');
        for (int n = segments.Length; n > 0; n--)
        {
            var candidate = string.Join('_', segments[..n]);
            if (pretests.TryGetValue(candidate, out var proc)) return proc;
        }
        return null;
    }

    /// <summary>
    /// Build the exec batch for a test/capture proc, optionally prefixed with
    /// its pretest. When a pretest exists, the runner declares @tstuser,
    /// captures the pretest's output, and passes it as the proc's first arg —
    /// all in one batch so the variable is in scope.
    /// </summary>
    private static string WithPretest(string? pretest, string execTarget)
        => pretest == null
            ? $"exec {execTarget}"
            : $"declare @tstuser varchar(8)\n" +
              $"exec {pretest} @tstuser_out = @tstuser output\n" +
              $"exec {execTarget} @tstuser";

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

    private static CaptureSpec? ParseCaptureSpecFromBody(string? body)
    {
        if (body == null) return null;

        var intoMatch   = CaptureIntoRe.Match(body);
        var sourceMatch = CaptureSourceRe.Match(body);
        if (!intoMatch.Success || !sourceMatch.Success) return null;

        return new CaptureSpec(
            IntoTable:  intoMatch.Groups[1].Value.Trim(),
            SourceCall: sourceMatch.Groups[1].Value.Trim());
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

        // Read-only report builders do `select ... into #tmp`, which Sybase forbids
        // inside a multi-statement transaction (Msg 226). A `-- @no-transaction`
        // test runs WITHOUT begin tran/rollback so those procs are runnable; since
        // it can't lean on rollback to undo writes, isolation is explicit: clear
        // the capture table and run the `<base>_teardown` hook in Cleanup().
        AseTransaction? tx = tc.NoTransaction ? null : conn.BeginTransaction();

        void Cleanup()
        {
            if (tx != null) { try { tx.Rollback(); } catch { } return; }
            // No-transaction path: undo by hand (best-effort; failures here must not
            // mask the test's own outcome).
            if (tc.Capture != null)
                TryExec(conn, $"delete from {tc.Capture.IntoTable}");
            if (tc.TeardownProc != null)
                TryExec(conn, $"exec {tc.TeardownProc}", _opts.TimeoutSeconds);
        }

        try
        {
            // No-transaction capture tests can't rely on rollback to start clean,
            // so defensively clear any rows a crashed prior test left this session.
            if (tx == null && tc.Capture != null)
                TryExec(conn, $"delete from {tc.Capture.IntoTable}");

            // Paired: pretest runs in the capture batch (it feeds @tstuser to
            // the capture proc); the assert proc only reads the capture table.
            // Singleton: pretest runs in the test batch.
            if (tc.CaptureProc != null && tc.Capture != null)
                RunCapturePhase(conn, tx, tc.CaptureProc, tc.Capture, tc.Pretest, _opts.TimeoutSeconds);

            var assertSql = tc.CaptureProc != null
                ? $"exec {tc.AssertProc}"
                : WithPretest(tc.Pretest, tc.AssertProc);
            using var cmd = new AseCommand(assertSql, conn);
            if (tx != null) cmd.Transaction = tx;
            cmd.CommandTimeout = _opts.TimeoutSeconds;
            cmd.ExecuteNonQuery();

            Cleanup();
            stopwatch.Stop();
            return new TestResult(tc.LogicalName, Outcome.PASS, "",
                stopwatch.Elapsed.TotalSeconds, JoinMessages(messages));
        }
        catch (AseException ex)
        {
            Cleanup();
            stopwatch.Stop();
            return Classify(tc.LogicalName, ex, messages, stopwatch.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            Cleanup();
            stopwatch.Stop();
            var outcome = ex.Message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
                ? Outcome.TIMEOUT : Outcome.ERROR;
            return new TestResult(tc.LogicalName, outcome, ex.Message,
                stopwatch.Elapsed.TotalSeconds, ex.ToString());
        }
    }

    // Best-effort statement on the test connection; swallows errors so cleanup
    // can never turn a PASS into a spurious failure.
    private static void TryExec(AseConnection conn, string sql, int? timeout = null)
    {
        try
        {
            using var cmd = new AseCommand(sql, conn);
            if (timeout is int t) cmd.CommandTimeout = t;
            cmd.ExecuteNonQuery();
        }
        catch { }
    }

    private void RunCapturePhase(AseConnection conn, AseTransaction? tx,
                                 string captureProc, CaptureSpec spec, string? pretest, int timeout)
    {
        // The capture table's schema (column count + per-column .NET
        // type) is the contract. Result sets emitted during the capture
        // proc whose shape doesn't match -- a trigger's `select @insrc,
        // @delrc` (2 unnamed ints), an audit-log emit, an arbitrary
        // debug select -- get filtered out. Only shape-matched result
        // sets are ingested, positionally against the table's column
        // order; reader column names don't have to be aligned.
        var tableSchema = _rebuiltTables[spec.IntoTable].Value;
        var insertSql   = BuildInsertSql(spec.IntoTable, tableSchema.ColumnNames);

        // Pretest (if any) runs in the same batch as the capture proc so the
        // allocated @tstuser is in scope. Its own emits (e.g. tri_users
        // debug select on the &users& INSERT) are shape-filtered out below.
        using var cmd = new AseCommand(WithPretest(pretest, captureProc), conn);
        if (tx != null) cmd.Transaction = tx;
        cmd.CommandTimeout = timeout;

        using var reader = cmd.ExecuteReader();
        do
        {
            if (!ResultSetMatchesSchema(reader, tableSchema)) continue;
            CopyResultSetIntoTable(reader, conn, tx, insertSql);
        } while (reader.NextResult());
    }

    private static bool ResultSetMatchesSchema(IDataReader reader, CaptureTableSchema schema)
    {
        if (reader.FieldCount != schema.ColumnTypes.Count) return false;
        for (int i = 0; i < reader.FieldCount; i++)
            if (reader.GetFieldType(i) != schema.ColumnTypes[i]) return false;
        return true;
    }

    private static void CopyResultSetIntoTable(IDataReader reader, AseConnection conn,
                                                AseTransaction? tx, string insertSql)
    {
        while (reader.Read())
        {
            using var insertCmd = new AseCommand(insertSql, conn);
            if (tx != null) insertCmd.Transaction = tx;
            for (int i = 0; i < reader.FieldCount; i++)
            {
                var val = reader.IsDBNull(i) ? DBNull.Value : reader.GetValue(i);
                insertCmd.Parameters.Add($"@p{i}", val);
            }
            insertCmd.ExecuteNonQuery();
        }
    }

    private static string BuildInsertSql(string targetTable, IReadOnlyList<string> cols)
    {
        var colList = string.Join(", ", cols.Select(QuoteIdent));
        var placeholders = string.Join(", ", Enumerable.Range(0, cols.Count).Select(i => $"@p{i}"));
        return $"insert into {targetTable} ({colList}) values ({placeholders})";
    }

    public void EnsureCaptureTable(CaptureSpec spec)
    {
        // First reference to a given capture table in this Runner
        // instance does a drop-if-exists + create; subsequent references
        // short-circuit. The table outlives individual tests within the
        // session (rows roll back per test via the test tran), but never
        // outlives the session itself -- so the table's schema is always
        // fresh against the source proc's current emitted shape. DDL is
        // outside any test tran (ddl in tran off on sbntest), hence its
        // own connection. The returned schema is cached for downstream
        // shape-matched filtering during the capture phase.
        var lazy = _rebuiltTables.GetOrAdd(spec.IntoTable,
            _ => new Lazy<CaptureTableSchema>(() => RebuildCaptureTable(spec),
                                              LazyThreadSafetyMode.ExecutionAndPublication));
        _ = lazy.Value;
    }

    private CaptureTableSchema RebuildCaptureTable(CaptureSpec spec)
    {
        using var conn = new AseConnection(BuildConnectionString(_opts.Database));
        conn.Open();

        if (TableExists(conn, spec.IntoTable))
        {
            using var dropCmd = new AseCommand($"drop table {spec.IntoTable}", conn);
            dropCmd.ExecuteNonQuery();
        }

        var schema = IntrospectResultSetSchema(conn, spec.SourceCall);
        var ddl = BuildCreateTable(spec.IntoTable, schema);
        using var createCmd = new AseCommand(ddl, conn);
        createCmd.ExecuteNonQuery();
        return CaptureTableSchema.FromIntrospectedSchema(schema);
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
        // `lock datarows` lifts the 254-variable-length-column ceiling that
        // Sybase ASE enforces on allpages-locked tables. Wide-emit procs
        // like g_ma_installations (~280 columns) would otherwise fail to
        // create. Capture tables are single-test scratch space, so the
        // locking scheme has no observable downside.
        sb.AppendLine(") lock datarows");
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
