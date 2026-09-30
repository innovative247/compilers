namespace SqlTest;

/// <summary>
/// One writer session across a bench's warm-up and measured runs: each run keep-restores,
/// and the session is finalised once at the end (design §2.7, R5).
/// </summary>
public sealed class WriterBench : IDisposable
{
    private readonly IDisposable? _owner;
    private bool _dirty;      // a run's test pair is set and no keep-restore has succeeded since
    private bool _restoreFailed; // the run's ERROR already says the journal row is kept
    private bool _finalised;

    public WriterSession Session { get; }

    /// <param name="owner">The control connection; disposed with the bench.</param>
    public WriterBench(WriterSession session, IDisposable? owner = null) { Session = session; _owner = owner; }

    /// <summary>A refused bench (discovery Error) returns its ERROR without a session or snapshot.</summary>
    public static bool OpensSession(TestCase c) => c.IsWriter && c.Error == null;

    public long JournalId => Session.JournalId;

    public void SetTestPair(int testSpid, int testKpid)
    {
        Session.SetTestPair(testSpid, testKpid);
        _dirty = true;
    }

    /// <summary>Null on success, else `restore failed: ...; journal row <id> kept`.</summary>
    public string? RestoreKeep()
    {
        var error = Session.RestoreKeep();
        _restoreFailed = error != null;
        if (error == null) _dirty = false;
        return error;
    }

    /// <summary>
    /// Drops snapshots and item rows, then the journal row. Skipped when the last run's
    /// restore did not succeed: the snapshots are then the only copy of the product rows.
    /// Returns warnings for the caller to print.
    /// </summary>
    public IReadOnlyList<string> Finalise()
    {
        var notes = new List<string>();
        if (_finalised) return notes;
        _finalised = true;
        if (_dirty)
        {
            if (!_restoreFailed) notes.Add($"run ended before its restore; journal row {JournalId} kept");
            return notes;
        }
        var error = Session.Finalise();
        notes.AddRange(Session.Warnings);
        Session.Warnings.Clear();
        if (error != null) notes.Add($"finalise failed: {error}; journal row {JournalId} kept");
        return notes;
    }

    public void Dispose() { try { _owner?.Dispose(); } catch { } }

    /// <summary>
    /// Warm-up plus <paramref name="count"/> runs, stopping after the first non-PASS.
    /// Finalises once whatever the exit, including an exception. Returns the runs made.
    /// </summary>
    public static List<TestResult> Drive(WriterBench? bench, int count, Func<int, TestResult> run, Action<string>? warn = null)
    {
        var ran = new List<TestResult>(count + 1);
        try
        {
            for (int i = 0; i <= count; i++)   // run 0 is the discarded warm-up
            {
                var r = run(i);
                ran.Add(r);
                if (r.Outcome != Outcome.PASS) break;
            }
        }
        finally
        {
            if (bench != null)
                foreach (var w in bench.Finalise()) warn?.Invoke(w);
        }
        return ran;
    }
}
