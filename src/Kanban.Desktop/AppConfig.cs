using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kanban.Desktop;

public sealed class AppConfig
{
    public static string DefaultVaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "KanbanVault");

    public string VaultRoot { get; set; } = DefaultVaultRoot;
    public int Port { get; set; } = 37650;
    public string Token { get; set; } = "";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Kanban");

    public static string SettingsPath => Path.Combine(Dir, "settings.json");
    public static string ApiInfoPath => Path.Combine(Dir, "api.json");

    public static AppConfig Load()
    {
        Directory.CreateDirectory(Dir);
        AppConfig config;
        if (File.Exists(SettingsPath))
        {
            try
            {
                config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(SettingsPath), Json) ?? new AppConfig();
            }
            catch (JsonException)
            {
                config = new AppConfig();
            }
        }
        else
        {
            config = new AppConfig();
        }

        if (string.IsNullOrWhiteSpace(config.VaultRoot))
            config.VaultRoot = DefaultVaultRoot;
        if (string.IsNullOrWhiteSpace(config.Token))
            config.Token = Guid.NewGuid().ToString("N");

        config.Save();
        config.WriteApiInfo();
        return config;
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, Json));
    }

    public void WriteApiInfo()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(ApiInfoPath, JsonSerializer.Serialize(new { port = Port, token = Token }, Json));
    }
}
