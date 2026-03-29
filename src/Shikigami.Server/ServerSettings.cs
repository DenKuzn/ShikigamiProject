using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shikigami.Server;

public sealed class ServerSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    [JsonPropertyName("show_window_on_startup")]
    public bool ShowWindowOnStartup { get; set; } = true;

    private static string GetFilePath()
    {
        var dir = AppContext.BaseDirectory;
        return Path.Combine(dir, "Settings.json");
    }

    public static ServerSettings Load()
    {
        var path = GetFilePath();
        if (!File.Exists(path))
            return new ServerSettings();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ServerSettings>(json, JsonOptions) ?? new ServerSettings();
        }
        catch
        {
            return new ServerSettings();
        }
    }

    public void Save()
    {
        var path = GetFilePath();
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(path, json);
    }
}
