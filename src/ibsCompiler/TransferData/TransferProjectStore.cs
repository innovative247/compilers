using System.Text.Json;
using System.Text.Json.Nodes;
using ibsCompiler.Configuration;

namespace ibsCompiler.TransferData
{
    /// <summary>
    /// Reads/writes transfer projects from the "data_transfer" section of settings.json.
    /// Uses JsonNode for partial read/write — never touches the "Profiles" section.
    /// </summary>
    public class TransferProjectStore
    {
        private readonly string _settingsPath;
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        public TransferProjectStore()
        {
            _settingsPath = ProfileManager.FindSettingsFile()
                ?? Path.Combine(AppContext.BaseDirectory, "settings.json");
        }

        public TransferProjectStore(string settingsPath)
        {
            _settingsPath = settingsPath;
        }

        public Dictionary<string, TransferProjectConfig> LoadAll()
        {
            if (!File.Exists(_settingsPath))
                return new Dictionary<string, TransferProjectConfig>();

            try
            {
                var root = JsonNode.Parse(File.ReadAllText(_settingsPath));
                var dt = root?["data_transfer"];
                if (dt == null)
                    return new Dictionary<string, TransferProjectConfig>();

                return dt.Deserialize<Dictionary<string, TransferProjectConfig>>(JsonOpts)
                    ?? new Dictionary<string, TransferProjectConfig>();
            }
            catch
            {
                return new Dictionary<string, TransferProjectConfig>();
            }
        }

        public TransferProjectConfig? Load(string projectName)
        {
            var all = LoadAll();
            return all.TryGetValue(projectName, out var config) ? config : null;
        }

        public void Save(string projectName, TransferProjectConfig config)
        {
            JsonNode root;
            if (File.Exists(_settingsPath))
            {
                try
                {
                    root = JsonNode.Parse(File.ReadAllText(_settingsPath)) ?? new JsonObject();
                }
                catch
                {
                    root = new JsonObject();
                }
            }
            else
            {
                root = new JsonObject();
            }

            // Ensure data_transfer section exists
            if (root["data_transfer"] == null)
                root["data_transfer"] = new JsonObject();

            // Serialize the project config and insert
            var configNode = JsonSerializer.SerializeToNode(config, JsonOpts);
            root["data_transfer"]![projectName] = configNode;

            File.WriteAllText(_settingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        public void Delete(string projectName)
        {
            if (!File.Exists(_settingsPath)) return;

            try
            {
                var root = JsonNode.Parse(File.ReadAllText(_settingsPath));
                var dt = root?["data_transfer"] as JsonObject;
                if (dt != null && dt.ContainsKey(projectName))
                {
                    dt.Remove(projectName);
                    File.WriteAllText(_settingsPath, root!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                }
            }
            catch { }
        }

        public List<string> ListProjects()
        {
            return LoadAll().Keys.ToList();
        }

        public string SettingsPath => _settingsPath;
    }
}
