using System.Text.Json;
using System.Text.Json.Serialization;

namespace ibsCompiler.Configuration
{
    /// <summary>
    /// The wire/disk shape of a profile published to the shared store.
    /// <para>
    /// This is deliberately a SEPARATE type from <see cref="ProfileData"/> rather than
    /// "ProfileData minus a few fields at serialization time". A field added to
    /// ProfileData later is non-shared until someone adds it here on purpose — the
    /// safe default. PASSWORD and SQL_SOURCE have no representation here at all:
    /// one is a secret, the other differs on every machine.
    /// </para>
    /// </summary>
    public class SharedProfile
    {
        [JsonPropertyName("NAME")]
        public string Name { get; set; } = "";

        [JsonPropertyName("ALIASES")]
        public List<string> Aliases { get; set; } = new();

        [JsonPropertyName("PLATFORM")]
        public string Platform { get; set; } = "SYBASE";

        [JsonPropertyName("HOST")]
        public string Host { get; set; } = "";

        [JsonPropertyName("PORT")]
        public int Port { get; set; }

        [JsonPropertyName("DATABASE")]
        public string Database { get; set; } = "";

        /// <summary>
        /// Raw mode: this target has no SBN source tree behind it (Atlas/SRM, a bare
        /// server). That is a fact about the SERVER, not about the developer, so it
        /// travels with the record.
        /// </summary>
        [JsonPropertyName("RAW_MODE")]
        public bool RawMode { get; set; }

        [JsonPropertyName("COMPANY")]
        [JsonConverter(typeof(FlexStringConverter))]
        public string Company { get; set; } = "101";

        [JsonPropertyName("DEFAULT_LANGUAGE")]
        [JsonConverter(typeof(FlexStringConverter))]
        public string DefaultLanguage { get; set; } = "1";

        [JsonPropertyName("DATA_CHARSET")]
        public string DataCharset { get; set; } = "";

        [JsonPropertyName("USERNAME")]
        public string Username { get; set; } = "";

        /// <summary>GitHub login of whoever last published this record.</summary>
        [JsonPropertyName("OWNER")]
        public string Owner { get; set; } = "";

        /// <summary>UTC timestamp of the last publish.</summary>
        [JsonPropertyName("UPDATED")]
        public string Updated { get; set; } = "";

        [JsonPropertyName("NOTE")]
        public string Note { get; set; } = "";
    }

    /// <summary>
    /// One shareable field, described once and reused by every caller: the publish
    /// mapping, the listing, the fetch merge diff, and the headless --accept parser
    /// all read this table. Adding a field to the shared record means adding one row
    /// here and one property to <see cref="SharedProfile"/> — nothing else.
    /// </summary>
    public sealed class ShareField
    {
        public string Key { get; init; } = "";
        public string Label { get; init; } = "";

        /// <summary>Value as it appears in the shared record, for display and comparison.</summary>
        public Func<SharedProfile, string> FromShared { get; init; } = _ => "";

        /// <summary>The same value read off a local profile, for display and comparison.</summary>
        public Func<ProfileData, string> FromLocal { get; init; } = _ => "";

        /// <summary>Copy the shared value onto a local profile.</summary>
        public Action<ProfileData, SharedProfile> Apply { get; init; } = (_, _) => { };

        /// <summary>Copy the local value into a shared record being published.</summary>
        public Action<SharedProfile, ProfileData> Capture { get; init; } = (_, _) => { };
    }

    public static class SharedProfileMap
    {
        /// <summary>
        /// The allowlist. Anything not in this table is never published and never
        /// applied on fetch.
        /// </summary>
        public static readonly IReadOnlyList<ShareField> Fields = new List<ShareField>
        {
            new ShareField
            {
                Key = "PLATFORM", Label = "Platform",
                FromShared = s => s.Platform ?? "",
                FromLocal  = p => p.Platform ?? "",
                Apply   = (p, s) => p.Platform = s.Platform ?? "",
                Capture = (s, p) => s.Platform = (p.Platform ?? "").ToUpperInvariant(),
            },
            new ShareField
            {
                Key = "RAW_MODE", Label = "Raw mode",
                FromShared = s => s.RawMode ? "yes" : "no",
                FromLocal  = p => p.RawMode ? "yes" : "no",
                Apply   = (p, s) => p.RawMode = s.RawMode,
                Capture = (s, p) => s.RawMode = p.RawMode,
            },
            new ShareField
            {
                Key = "HOST", Label = "Host",
                FromShared = s => s.Host ?? "",
                FromLocal  = p => p.Host ?? "",
                Apply   = (p, s) => p.Host = s.Host ?? "",
                Capture = (s, p) => s.Host = p.Host ?? "",
            },
            new ShareField
            {
                Key = "PORT", Label = "Port",
                FromShared = s => s.Port.ToString(),
                FromLocal  = p => p.Port.ToString(),
                Apply   = (p, s) => p.Port = s.Port,
                Capture = (s, p) => s.Port = p.Port,
            },
            new ShareField
            {
                Key = "DATABASE", Label = "Database",
                FromShared = s => s.Database ?? "",
                FromLocal  = p => p.Database ?? "",
                Apply   = (p, s) => p.Database = s.Database ?? "",
                Capture = (s, p) => s.Database = p.Database ?? "",
            },
            new ShareField
            {
                Key = "USERNAME", Label = "Username",
                FromShared = s => s.Username ?? "",
                FromLocal  = p => p.Username ?? "",
                Apply   = (p, s) => p.Username = s.Username ?? "",
                Capture = (s, p) => s.Username = p.Username ?? "",
            },
            new ShareField
            {
                Key = "COMPANY", Label = "Company",
                FromShared = s => s.Company ?? "",
                FromLocal  = p => p.Company ?? "",
                Apply   = (p, s) => p.Company = s.Company ?? "",
                Capture = (s, p) => s.Company = p.Company ?? "",
            },
            new ShareField
            {
                Key = "LANGUAGE", Label = "Language",
                FromShared = s => s.DefaultLanguage ?? "",
                FromLocal  = p => p.DefaultLanguage ?? "",
                Apply   = (p, s) => p.DefaultLanguage = s.DefaultLanguage ?? "",
                Capture = (s, p) => s.DefaultLanguage = p.DefaultLanguage ?? "",
            },
            new ShareField
            {
                Key = "DATA_CHARSET", Label = "Charset",
                FromShared = s => s.DataCharset ?? "",
                FromLocal  = p => p.DataCharset ?? "",
                Apply   = (p, s) => p.DataCharset = s.DataCharset ?? "",
                Capture = (s, p) => s.DataCharset = p.DataCharset ?? "",
            },
            new ShareField
            {
                Key = "ALIASES", Label = "Aliases",
                FromShared = s => string.Join(",", s.Aliases ?? new List<string>()),
                FromLocal  = p => string.Join(",", p.Aliases ?? new List<string>()),
                Apply   = (p, s) => p.Aliases = new List<string>(s.Aliases ?? new List<string>()),
                Capture = (s, p) => s.Aliases = new List<string>(p.Aliases ?? new List<string>()),
            },
        };

        public static ShareField? Field(string key) =>
            Fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));

        /// <summary>Fields that must never reach the shared store, checked by name on every publish.</summary>
        public static readonly string[] BannedKeys = { "PASSWORD", "SQL_SOURCE" };

        /// <summary>
        /// Build the publishable record for a local profile. Every value comes through
        /// the allowlist above; nothing is copied wholesale.
        /// </summary>
        public static SharedProfile Capture(string name, ProfileData local, string owner, string note)
        {
            var shared = new SharedProfile
            {
                Name = name.ToUpperInvariant(),
                Owner = owner ?? "",
                Note = note ?? "",
                Updated = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"),
            };
            foreach (var f in Fields) f.Capture(shared, local);
            return shared;
        }

        private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

        /// <summary>
        /// Serialize a record for publication, refusing outright if a field that must
        /// never be shared has appeared in the payload.
        /// <para>
        /// This fails rather than scrubbing on purpose: a scrub hides the bug that let
        /// the field in, a failure surfaces it. The real defence is structural - the
        /// allowlist above, and <see cref="SharedProfile"/> having no property for a
        /// password at all - so this is a cheap second line, not the first one.
        /// </para>
        /// </summary>
        public static string SerializeForPublish(SharedProfile shared)
        {
            var json = JsonSerializer.Serialize(shared, WriteOptions);

            foreach (var banned in BannedKeys)
            {
                if (json.Contains("\"" + banned + "\"", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"refusing to publish: '{banned}' is not a shareable field but appears in the payload. " +
                        "Nothing has been published.");
                }
            }

            return json;
        }

        public static SharedProfile? Deserialize(string json)
        {
            try
            {
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                return JsonSerializer.Deserialize<SharedProfile>(json, options);
            }
            catch { return null; }
        }
    }
}
