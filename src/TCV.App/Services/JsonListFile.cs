using System.IO;
using System.Text.Json;

namespace TCV.App.Services;

/// <summary>How reading a JSON list file went — the distinction every read-modify-write store needs.</summary>
public enum JsonFileState
{
    /// <summary>Read and parsed.</summary>
    Ok,
    /// <summary>No file (yet): an empty list is the right answer.</summary>
    Missing,
    /// <summary>The file exists but is not valid JSON (hand-edited, half-written).</summary>
    Corrupt,
    /// <summary>The file could not be opened right now (a second window holds it).</summary>
    Unreadable,
}

/// <summary>
/// The reading and writing rules shared by the small portable stores (saved operations, search
/// presets, file-usage history), following <see cref="Workspaces.WorkspaceStore"/>: a locked file is
/// not an empty file, and a corrupt file must not be silently overwritten by the next save.
/// </summary>
/// <remarks>
/// A store that treats "unreadable" or "corrupt" as "empty" and then writes turns one bad moment into
/// permanent loss of everything the user saved. Here a locked file makes the write fail (the caller's
/// in-memory copy stays authoritative), and a corrupt file is copied aside as <c>*.corrupt-….bak</c>
/// before it is replaced, so it can still be repaired by hand.
/// </remarks>
public static class JsonListFile
{
    /// <summary>Reads <paramref name="path"/> as a JSON array of <typeparamref name="T"/>.</summary>
    public static JsonFileState TryRead<T>(string path, JsonSerializerOptions options, out List<T> list)
    {
        list = new List<T>();
        try
        {
            if (!File.Exists(path))
            {
                return JsonFileState.Missing;
            }

            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return JsonFileState.Missing;
            }

            list = JsonSerializer.Deserialize<List<T>>(json, options) ?? new List<T>();
            return JsonFileState.Ok;
        }
        catch (JsonException)
        {
            return JsonFileState.Corrupt;
        }
        catch (Exception)
        {
            return JsonFileState.Unreadable;
        }
    }

    /// <summary>
    /// Copies a corrupt file aside so the next save does not destroy the only copy of what the user
    /// had. Best effort: failing to back up must not block saving new data.
    /// </summary>
    public static void BackupCorrupt(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
            File.Copy(path, $"{path}.corrupt-{stamp}.bak", overwrite: true);
        }
        catch (Exception)
        {
            // nothing more to do; the caller carries on
        }
    }

    /// <summary>Writes through a temporary file, so a crash mid-write leaves the old content intact.</summary>
    public static bool WriteAtomic(string path, string content)
    {
        try
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, content);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
