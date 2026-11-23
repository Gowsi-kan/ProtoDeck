using System.Text.Json;
using ProtoDeck.Models;

namespace ProtoDeck.Services;

/// <summary>Persists collections as JSON under the user's config dir (~/.config on Linux, %APPDATA% on Windows).</summary>
public sealed class CollectionStore(string? path = null)
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    // Keep the original directory so existing collections remain available after the rename.
    private readonly string _path = path ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "GrpcWorkbench", "collections.json");

    public List<RequestCollection> Load()
    {
        try
        {
            if (!File.Exists(_path)) return [];
            using var stream = File.OpenRead(_path);
            return JsonSerializer.Deserialize<List<RequestCollection>>(stream, Options) ?? [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public void Save(IEnumerable<RequestCollection> collections)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(collections, Options));
        File.Move(tmp, _path, overwrite: true);
    }
}
