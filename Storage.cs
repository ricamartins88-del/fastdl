using System.Text.Json;
using System.Text.Json.Serialization;

namespace FastDl;

/// <summary>Lê e grava JSON em %AppData%\FastDl com troca atômica (nunca deixa arquivo pela metade).</summary>
public static class Storage
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Dir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FastDl");

    public static T? Load<T>(string fileName) where T : class
    {
        try
        {
            string path = Path.Combine(Dir, fileName);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Opts);
        }
        catch
        {
            return null;
        }
    }

    public static void Save<T>(string fileName, T value)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            string path = Path.Combine(Dir, fileName);
            string tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Opts));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // falha ao salvar não pode derrubar o app
        }
    }
}

public sealed class AppSettings
{
    public string DownloadFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public int ConnectionsPerDownload { get; set; } = 8;
    public int MaxSimultaneous { get; set; } = 3;

    public static AppSettings Load() => Storage.Load<AppSettings>("settings.json") ?? new AppSettings();
    public void Save() => Storage.Save("settings.json", this);
}

/// <summary>Formato salvo em disco para cada item da lista.</summary>
public sealed class ItemData
{
    public Guid Id { get; set; }
    public string Url { get; set; } = "";
    public string OutputDir { get; set; } = "";
    public string FileName { get; set; } = "";
    public int Connections { get; set; } = 8;
    public DownloadStatus Status { get; set; }
    public long Done { get; set; }
    public long Total { get; set; }
    public string? FinalPath { get; set; }
}

public static class SharedHttp
{
    public static readonly HttpClient Client = DownloadEngine.CreateHttpClient();
}
