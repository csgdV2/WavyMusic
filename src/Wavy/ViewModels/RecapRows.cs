using Wavy.Core.Models;

namespace Wavy.ViewModels;

public sealed record RecapSongRow(int Rank, Track Track, string PlaysText, string TimeText);
public sealed record RecapArtistRow(int Rank, string Name, string Detail, string? BrowseId, string? ThumbnailUrl = null);
