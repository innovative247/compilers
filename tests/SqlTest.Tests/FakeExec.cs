using SqlTest;

namespace SqlTest.Tests;

// Records every statement; throws on the first statement containing FailOn.
internal sealed class FakeExec : ISqlExec
{
    public readonly List<string> Log = new();
    public string? FailOn;
    public Func<string, object?> OnScalar = _ => 1;
    public Func<string, List<object?[]>> OnRows = _ => new();

    private void Hit(string sql)
    {
        Log.Add(sql);
        if (FailOn != null && sql.Contains(FailOn)) throw new InvalidOperationException("boom");
    }
    public int Exec(string sql) { Hit(sql); return 1; }
    public object? Scalar(string sql) { Hit(sql); return OnScalar(sql); }
    public List<object?[]> Rows(string sql) { Hit(sql); return OnRows(sql); }
}
