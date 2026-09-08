namespace MusicApp.Core.Models;

public sealed record DownloadProgress(double Percent, string? Destination, string? Raw)
{
    public bool IsComplete => Percent >= 100.0;
}
