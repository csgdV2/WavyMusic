using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using MusicApp.Core.Models;

namespace MusicApp.Services;

public sealed class FavoritesService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;

    public FavoritesService(string dataRoot)
    {
        _path = Path.Combine(dataRoot, "albums.json");
        foreach (var album in Load())
            Albums.Add(album);
    }

    public ObservableCollection<AlbumResult> Albums { get; } = new();

    public bool Contains(string? browseId) =>
        browseId is { Length: > 0 } && Albums.Any(a => a.BrowseId == browseId);

    public bool Toggle(AlbumResult album)
    {
        if (Find(album.BrowseId) is { } existing)
        {
            Albums.Remove(existing);
            Save();
            return false;
        }

        Albums.Insert(0, album);
        Save();
        return true;
    }

    public void Remove(string browseId)
    {
        if (Find(browseId) is not { } existing)
            return;

        Albums.Remove(existing);
        Save();
    }

    private AlbumResult? Find(string? browseId) =>
        browseId is { Length: > 0 } ? Albums.FirstOrDefault(a => a.BrowseId == browseId) : null;

    private List<AlbumResult> Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<List<AlbumResult>>(File.ReadAllText(_path), JsonOptions) ?? new()
                : new();
        }
        catch
        {

            return new();
        }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_path, JsonSerializer.Serialize(Albums.ToList(), JsonOptions));
        }
        catch
        {

        }
    }
}
