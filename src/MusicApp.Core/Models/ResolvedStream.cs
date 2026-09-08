namespace MusicApp.Core.Models;

public sealed class ResolvedStream
{
    public required string Url { get; init; }

    public string? Container { get; init; }

    public DateTimeOffset? ExpiresAtUtc { get; init; }

    public string? DashManifest { get; init; }

    public bool IsExpired(TimeSpan safetyMargin) =>
        ExpiresAtUtc is { } expiry && DateTimeOffset.UtcNow >= expiry - safetyMargin;
}
