using System.Text.Encodings.Web;
using System.Text.Json;
using ibsCompiler;

namespace CheckApiProcs;

/// <summary>Where a declaration comes from; a downloaded release asset is a later implementation.</summary>
public interface IDeclarationSource
{
    Task<string> ReadAsync();
    string Describe { get; }
}

public class FileDeclarationSource : IDeclarationSource
{
    private readonly string _path;
    public FileDeclarationSource(string path) => _path = path;
    public string Describe => _path;
    public Task<string> ReadAsync() => File.ReadAllTextAsync(_path);
}

public class DeclarationException : Exception
{
    public DeclarationException(string message) : base(message) { }
}

public record DeclParam(string Name, string Type, string? Default, bool Output, bool Required);

public record DeclResultSet(string Name, List<string> Columns);

public class DeclProc
{
    public string Name { get; set; } = "";
    public List<DeclParam>? Parameters { get; set; }
    public List<DeclResultSet>? ResultSets { get; set; }
}

/// <summary>An api3-procs.json file: a hand-kept names list or a recorded declaration (format doc § 1, § 3).</summary>
public class Declaration
{
    public const string FormatVersion = "1.0";

    public string? Version { get; set; }
    public string? Consumer { get; set; }
    public string? Release { get; set; }
    public string? EngineUrl { get; set; }
    public string? EngineVersion { get; set; }
    public string? RecordedAt { get; set; }
    public List<DeclProc> Procedures { get; } = new();

    /// <summary>recorded: every procedure must carry parameters (check mode).</summary>
    public static Declaration Parse(string json, bool recorded)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }); }
        catch (JsonException ex) { throw new DeclarationException($"not valid JSON: {ex.Message}"); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new DeclarationException("top level is not a JSON object");
            var d = new Declaration
            {
                Version = Str(root, "formatVersion"),
                Consumer = Str(root, "consumer"),
                Release = Str(root, "release"),
                RecordedAt = Str(root, "recordedAt"),
            };
            if (Prop(root, "referenceEngine") is { ValueKind: JsonValueKind.Object } eng)
            {
                d.EngineUrl = Str(eng, "url");
                d.EngineVersion = Str(eng, "version");
            }
            if (d.Version == null)
            {
                if (recorded) throw new DeclarationException("formatVersion is missing");
            }
            else if (d.Version.Split('.')[0] != "1")
                throw new DeclarationException($"formatVersion {d.Version} is not supported (this checker reads major version 1)");

            if (Prop(root, "procedures") is not { ValueKind: JsonValueKind.Array } procs)
                throw new DeclarationException("procedures array is missing");
            foreach (var p in procs.EnumerateArray())
            {
                // A hand-kept list may give bare names.
                var proc = p.ValueKind == JsonValueKind.String
                    ? new DeclProc { Name = p.GetString() ?? "" }
                    : new DeclProc { Name = Str(p, "name") ?? "", Parameters = Params(p), ResultSets = Sets(p) };
                if (proc.Name == "") throw new DeclarationException("a procedure has no name");
                if (recorded && proc.Parameters == null)
                    throw new DeclarationException($"{proc.Name} has no parameters: this is a hand-kept names list, not a recorded declaration (record it with --record)");
                d.Procedures.Add(proc);
            }
            return d;
        }
    }

    private static List<DeclParam>? Params(JsonElement proc) =>
        Prop(proc, "parameters") is { ValueKind: JsonValueKind.Array } ps
            ? ps.EnumerateArray().Select(p => new DeclParam(
                Str(p, "name") ?? "", Str(p, "type") ?? "", Str(p, "default"),
                Prop(p, "output") is { ValueKind: JsonValueKind.True },
                Prop(p, "required") is { ValueKind: JsonValueKind.True })).ToList()
            : null;

    private static List<DeclResultSet>? Sets(JsonElement proc) =>
        Prop(proc, "resultSets") is { ValueKind: JsonValueKind.Array } rs
            ? rs.EnumerateArray().Select(s => new DeclResultSet(Str(s, "name") ?? "",
                Prop(s, "columns") is { ValueKind: JsonValueKind.Array } cs
                    ? cs.EnumerateArray().Select(c => c.ToString()).ToList()
                    : new List<string>())).ToList()
            : null;

    /// <summary>Writes the recorded file per format doc § 1, LF line endings regardless of host.</summary>
    public void Write(string path)
    {
        var doc = new
        {
            formatVersion = Version,
            consumer = Consumer,
            release = Release,
            referenceEngine = new { url = EngineUrl, version = EngineVersion },
            recordedAt = RecordedAt,
            procedures = Procedures.Select(p => new
            {
                name = p.Name,
                parameters = (p.Parameters ?? new()).Select(x => new { name = x.Name, type = x.Type, @default = x.Default, output = x.Output, required = x.Required }),
                resultSets = (p.ResultSets ?? new()).Select(s => new { name = s.Name, columns = s.Columns }),
            }),
        };
        var json = JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        // System.Text.Json indents with Environment.NewLine.
        using var w = ibs_compiler_common.OpenSourceWriter(path);
        w.Write(json.Replace("\r\n", "\n"));
        w.WriteLine();
    }

    private static JsonElement? Prop(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        foreach (var p in e.EnumerateObject())
            if (p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return p.Value;
        return null;
    }

    private static string? Str(JsonElement e, string name) => Prop(e, name) switch
    {
        null => null,
        { ValueKind: JsonValueKind.Null } => null,
        { ValueKind: JsonValueKind.String } v => v.GetString(),
        { } v => v.ToString(),
    };
}
