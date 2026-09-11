using System.Text.Json;
using System.Text.Json.Serialization;

namespace ibsCompiler.Configuration
{
    /// <summary>
    /// Reads JSON values that may be either strings or numbers into a C# string property.
    /// settings.json uses bare integers for COMPANY and DEFAULT_LANGUAGE.
    /// </summary>
    public class FlexStringConverter : JsonConverter<string>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number)
            {
                if (reader.TryGetInt64(out var l)) return l.ToString();
                if (reader.TryGetDouble(out var d)) return d.ToString();
            }
            return reader.GetString();
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value);
        }
    }

    public class ProfileData
    {
        [JsonPropertyName("COMPANY")]
        [JsonConverter(typeof(FlexStringConverter))]
        public string Company { get; set; } = "101";

        [JsonPropertyName("DEFAULT_LANGUAGE")]
        [JsonConverter(typeof(FlexStringConverter))]
        public string DefaultLanguage { get; set; } = "1";

        [JsonPropertyName("PLATFORM")]
        public string Platform { get; set; } = "SYBASE";

        [JsonPropertyName("HOST")]
        public string Host { get; set; } = "";

        [JsonPropertyName("PORT")]
        public int Port
        {
            get => _port > 0 ? _port : ibs_compiler_common.DefaultPort(ibs_compiler_common.ParsePlatform(Platform));
            set => _port = value;
        }
        private int _port;

        [JsonPropertyName("USERNAME")]
        public string Username { get; set; } = "sbn0";

        [JsonPropertyName("PASSWORD")]
        public string Password { get; set; } = "ibsibs";

        [JsonPropertyName("SQL_SOURCE")]
        public string SqlSource { get; set; } = "";

        [JsonPropertyName("RAW_MODE")]
        public bool RawMode { get; set; }

        [JsonPropertyName("DATABASE")]
        public string Database { get; set; } = "";

        // Actual code page of char/varchar data, when it differs from the server's
        // declared default charset (e.g. Windows-1252 data stored in a cp850-declared
        // ASE). Used to reinterpret message text on export/import. Empty = trust the
        // server charset (legacy behaviour).
        [JsonPropertyName("DATA_CHARSET")]
        public string DataCharset { get; set; } = "";

        [JsonPropertyName("ALIASES")]
        public List<string> Aliases { get; set; } = new();

        // Provenance for a profile fetched from the shared store — NOT a subscription.
        // The shared listing uses these to say "your copy is older than the shared one";
        // nothing ever acts on that automatically.
        [JsonPropertyName("SHARED_FROM")]
        public string SharedFrom { get; set; } = "";

        [JsonPropertyName("SHARED_UPDATED")]
        public string SharedUpdated { get; set; } = "";
    }

    public class SettingsFile
    {
        [JsonPropertyName("Profiles")]
        public Dictionary<string, ProfileData> Profiles { get; set; } = new();

        /// <summary>
        /// How this developer is credited as OWNER on a shared profile. Without it the
        /// GitHub login is used, which is a handle nobody else necessarily recognizes —
        /// a work email is more use to the colleague reading the listing. Omitted from
        /// settings.json until it is actually set.
        /// </summary>
        [JsonPropertyName("SHARED_OWNER")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public string SharedOwner { get; set; } = "";
    }
}
