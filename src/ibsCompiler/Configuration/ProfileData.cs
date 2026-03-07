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
            get => _port > 0 ? _port : (Platform?.ToUpper() == "MSSQL" ? 1433 : 5000);
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

        [JsonPropertyName("ALIASES")]
        public List<string> Aliases { get; set; } = new();
    }

    public class SettingsFile
    {
        [JsonPropertyName("Profiles")]
        public Dictionary<string, ProfileData> Profiles { get; set; } = new();
    }
}
