using SqlTest;

namespace SqlTest.Tests;

public class CallListTests
{
    private const string Valid = """
        {
          "schema": 1,
          "database": "sbntest",
          "calls": [
            { "name": "one", "sql": "exec {proc} @n = 1" },
            { "name": "two", "sql": "exec {proc} @n = 2", "no_tran": true,
              "restore": ["sbntest..t where id = 1 max 10", "sbntest..u where k > 0 allow-triggers"],
              "expect_rows": 3 }
          ]
        }
        """;

    private static string Refusal(string json) =>
        Assert.Throws<CallListException>(() => CallList.Parse(json, "calls.json")).Message;

    [Fact]
    public void Valid_file_parses()
    {
        var c = CallList.Parse(Valid, "calls.json");
        Assert.Equal(1, c.Schema);
        Assert.Equal("sbntest", c.Database);
        Assert.Equal(2, c.Calls.Count);
        Assert.Equal("one", c.Calls[0].Name);
        Assert.Equal("exec {proc} @n = 1", c.Calls[0].Sql);
        Assert.False(c.Calls[0].NoTran);
        Assert.Empty(c.Calls[0].Restore);
        Assert.Null(c.Calls[0].ExpectRows);
        Assert.True(c.Calls[1].NoTran);
    }

    [Fact]
    public void Restore_options_are_carried_through()
    {
        var r = CallList.Parse(Valid, "calls.json").Calls[1].Restore;
        Assert.Equal(2, r.Count);
        Assert.Equal(new RestoreSpec("sbntest", "t", "id = 1", false, 10), r[0]);
        Assert.Equal(new RestoreSpec("sbntest", "u", "k > 0", true, WriterJournal.DefaultMaxRows), r[1]);
    }

    [Fact]
    public void Expect_rows_is_parsed_and_otherwise_ignored() =>
        Assert.Equal(3, CallList.Parse(Valid, "calls.json").Calls[1].ExpectRows);

    [Fact]
    public void Database_is_optional() =>
        Assert.Null(CallList.Parse("""{"schema":1,"calls":[{"name":"a","sql":"x"}]}""", "c").Database);

    [Fact]
    public void Schema_2_is_refused() =>
        Assert.Contains("unsupported call-list schema 2 (this sql-test reads 1)",
            Refusal("""{"schema":2,"calls":[{"name":"a","sql":"x"}]}"""));

    [Fact]
    public void Missing_schema_is_refused() =>
        Assert.Contains("unsupported call-list schema", Refusal("""{"calls":[{"name":"a","sql":"x"}]}"""));

    [Theory]
    [InlineData("""{"schema":1}""")]
    [InlineData("""{"schema":1,"calls":[]}""")]
    public void Missing_or_empty_calls_are_refused(string json) =>
        Assert.Contains("calls must be a non-empty array", Refusal(json));

    [Fact]
    public void Duplicate_names_in_different_case_are_refused() =>
        Assert.Contains("duplicate call name One",
            Refusal("""{"schema":1,"calls":[{"name":"one","sql":"x"},{"name":"One","sql":"y"}]}"""));

    [Fact]
    public void Missing_name_is_refused() =>
        Assert.Contains("name is missing or empty", Refusal("""{"schema":1,"calls":[{"name":" ","sql":"x"}]}"""));

    [Fact]
    public void Blank_sql_is_refused() =>
        Assert.Contains("call a: sql is missing or blank", Refusal("""{"schema":1,"calls":[{"name":"a","sql":"  "}]}"""));

    [Fact]
    public void No_tran_must_be_bool() =>
        Assert.Contains("no_tran must be true or false",
            Refusal("""{"schema":1,"calls":[{"name":"a","sql":"x","no_tran":"yes"}]}"""));

    [Fact]
    public void Restore_must_be_array_of_strings() =>
        Assert.Contains("restore must be an array of strings",
            Refusal("""{"schema":1,"calls":[{"name":"a","sql":"x","no_tran":true,"restore":[1]}]}"""));

    [Fact]
    public void Restore_without_no_tran_is_refused() =>
        Assert.Contains("call a: restore requires no_tran",
            Refusal("""{"schema":1,"calls":[{"name":"a","sql":"x","restore":["db..t where id = 1"]}]}"""));

    [Fact]
    public void Bad_restore_line_is_refused() =>
        Assert.Contains("call a: invalid restore line: db..t id = 1",
            Refusal("""{"schema":1,"calls":[{"name":"a","sql":"x","no_tran":true,"restore":["db..t id = 1"]}]}"""));

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("\"3\"")]
    public void Expect_rows_must_be_non_negative_int(string v) =>
        Assert.Contains("expect_rows must be a non-negative integer",
            Refusal($$"""{"schema":1,"calls":[{"name":"a","sql":"x","expect_rows":{{v}}}]}"""));

    [Fact]
    public void Unknown_entry_field_is_refused() =>
        Assert.Contains("call a: unknown field tran",
            Refusal("""{"schema":1,"calls":[{"name":"a","sql":"x","tran":false}]}"""));

    [Fact]
    public void Unknown_top_field_is_refused() =>
        Assert.Contains("unknown field extra",
            Refusal("""{"schema":1,"extra":1,"calls":[{"name":"a","sql":"x"}]}"""));

    [Fact]
    public void Refusal_names_the_source() =>
        Assert.StartsWith("calls.json: ", Refusal("""{"schema":2}"""));

    [Fact]
    public void Invalid_json_is_refused() =>
        Assert.Contains("invalid JSON", Refusal("{"));
}
