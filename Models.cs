using System.Text.Json.Serialization;

namespace FastDl;

/// <summary>Um pedaço do arquivo [Start..End] (inclusivo) baixado por uma conexão.</summary>
public sealed class Segment
{
    public long Start { get; set; }
    public long End { get; set; }
    public long Downloaded { get; set; }

    [JsonIgnore] public bool Active { get; set; }
    [JsonIgnore] public long Remaining => End - (Start + Downloaded) + 1;
}

/// <summary>Estado salvo em disco (.fdl) para permitir retomar o download.</summary>
public sealed class DownloadState
{
    public string Url { get; set; } = "";
    public string FinalUrl { get; set; } = "";
    public string FileName { get; set; } = "";
    public long TotalSize { get; set; }
    public string? ETag { get; set; }
    public string? LastModified { get; set; }
    public List<Segment> Segments { get; set; } = new();
}

public sealed record ProbeResult(
    string FinalUrl,
    string FileName,
    long? TotalSize,
    bool SupportsRanges,
    string? ETag,
    string? LastModified);

public sealed record ProgressInfo(
    long Done,
    long? Total,
    double BytesPerSecond,
    int Connections,
    TimeSpan? Eta);

public enum DownloadOutcome { Completed, Paused, Failed }

public sealed class FatalDownloadException : Exception
{
    public FatalDownloadException(string message, Exception? inner = null) : base(message, inner) { }
}

internal static class Fmt
{
    public static string Bytes(double b)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (b >= 1024 && i < units.Length - 1) { b /= 1024; i++; }
        return i == 0 ? $"{b:0} {units[i]}" : $"{b:0.00} {units[i]}";
    }

    public static string Time(TimeSpan t) =>
        t.TotalHours >= 1
            ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}"
            : $"{t.Minutes:00}:{t.Seconds:00}";
}
