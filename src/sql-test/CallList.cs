using System.Text.Json;

namespace SqlTest;

public sealed record CallEntry(string Name, string Sql, bool NoTran, IReadOnlyList<RestoreSpec> Restore, int? ExpectRows);

public sealed class CallListException(string message) : Exception(message);

/// <summary>JSON list of calls: `{ "schema": 1, "database": ..., "calls": [ { "name", "sql", "no_tran", "restore", "expect_rows" } ] }`.</summary>
public sealed record CallList(int Schema, string? Database, IReadOnlyList<CallEntry> Calls)
{
    public const int SupportedSchema = 1;

    private static readonly HashSet<string> TopFields = new(StringComparer.Ordinal) { "schema", "database", "calls" };
    private static readonly HashSet<string> EntryFields = new(StringComparer.Ordinal) { "name", "sql", "no_tran", "restore", "expect_rows" };

    public static CallList Load(string path)
    {
        string json;
        try { json = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new CallListException($"{path}: cannot read call list: {ex.Message}");
        }
        return Parse(json, path);
    }

    public static CallList Parse(string json, string source)
    {
        try { return ParseCore(json); }
        catch (CallListException ex) { throw new CallListException($"{source}: {ex.Message}"); }
        catch (JsonException ex) { throw new CallListException($"{source}: invalid JSON: {ex.Message}"); }
    }

    private static CallList ParseCore(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw Fail("call list must be a JSON object");
        RefuseUnknown(root, TopFields, "call list");

        // Unknown fields are refused, so a schema bump is the only way to add one.
        if (!root.TryGetProperty("schema", out var schemaEl))
            throw Fail($"unsupported call-list schema (missing) (this sql-test reads {SupportedSchema})");
        if (schemaEl.ValueKind != JsonValueKind.Number || !schemaEl.TryGetInt32(out var schema) || schema != SupportedSchema)
            throw Fail($"unsupported call-list schema {schemaEl.GetRawText()} (this sql-test reads {SupportedSchema})");

        string? database = null;
        if (root.TryGetProperty("database", out var dbEl) && dbEl.ValueKind != JsonValueKind.Null)
        {
            if (dbEl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(dbEl.GetString()))
                throw Fail("database must be a non-empty string");
            database = dbEl.GetString()!.Trim();
        }

        if (!root.TryGetProperty("calls", out var callsEl) || callsEl.ValueKind != JsonValueKind.Array || callsEl.GetArrayLength() == 0)
            throw Fail("calls must be a non-empty array");

        var calls = new List<CallEntry>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int index = 0;
        foreach (var e in callsEl.EnumerateArray())
        {
            index++;
            if (e.ValueKind != JsonValueKind.Object) throw Fail($"call #{index} must be an object");

            if (!e.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(nameEl.GetString()))
                throw Fail($"call #{index}: name is missing or empty");
            var name = nameEl.GetString()!.Trim();
            RefuseUnknown(e, EntryFields, $"call {name}");
            if (!names.Add(name)) throw Fail($"duplicate call name {name}");

            if (!e.TryGetProperty("sql", out var sqlEl) || sqlEl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(sqlEl.GetString()))
                throw Fail($"call {name}: sql is missing or blank");

            bool noTran = false;
            if (e.TryGetProperty("no_tran", out var ntEl))
            {
                if (ntEl.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw Fail($"call {name}: no_tran must be true or false");
                noTran = ntEl.GetBoolean();
            }

            var restore = new List<RestoreSpec>();
            if (e.TryGetProperty("restore", out var rEl))
            {
                if (rEl.ValueKind != JsonValueKind.Array) throw Fail($"call {name}: restore must be an array of strings");
                foreach (var line in rEl.EnumerateArray())
                {
                    if (line.ValueKind != JsonValueKind.String) throw Fail($"call {name}: restore must be an array of strings");
                    var text = line.GetString()!;
                    var spec = WriterJournal.ParseRestoreLine(text)
                        ?? throw Fail($"call {name}: invalid restore line: {text}");
                    restore.Add(spec);
                }
                // Restores only make sense outside the rollback wrap.
                if (restore.Count > 0 && !noTran) throw Fail($"call {name}: restore requires no_tran");
            }

            int? expectRows = null;
            if (e.TryGetProperty("expect_rows", out var xEl))
            {
                if (xEl.ValueKind != JsonValueKind.Number || !xEl.TryGetInt32(out var n) || n < 0)
                    throw Fail($"call {name}: expect_rows must be a non-negative integer");
                expectRows = n;
            }

            calls.Add(new CallEntry(name, sqlEl.GetString()!, noTran, restore, expectRows));
        }
        return new CallList(schema, database, calls);
    }

    private static void RefuseUnknown(JsonElement obj, HashSet<string> known, string where)
    {
        foreach (var p in obj.EnumerateObject())
            if (!known.Contains(p.Name)) throw Fail($"{where}: unknown field {p.Name}");
    }

    private static CallListException Fail(string message) => new(message);
}
