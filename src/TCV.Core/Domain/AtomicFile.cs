using System.Text;

namespace TCV.Core.Domain;

/// <summary>
/// Crash-safe text files for the small JSON stores that live beside the exe (settings,
/// connections, known hosts). A plain <c>File.WriteAllText</c> truncates first: a crash or a full
/// disk mid-write leaves an empty or half file, and the next start reads that as "no settings".
/// </summary>
public static class AtomicFile
{
    /// <summary>
    /// Writes <paramref name="content"/> to a sibling temp file, then swaps it in. The version it
    /// replaces is kept as <c>path.bak</c>, so one bad save is always recoverable by hand.
    /// </summary>
    public static void WriteAllText(string path, string content)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        File.WriteAllText(temp, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        try
        {
            if (File.Exists(path))
            {
                File.Replace(temp, path, path + ".bak", ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, path);
            }
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// Copies an unreadable file to <c>path.corrupt-yyyyMMdd-HHmmss</c> before anything overwrites
    /// it, and returns the copy's path (null when the copy itself failed).
    /// </summary>
    public static string? PreserveCorrupt(string path)
    {
        try
        {
            var copy = $"{path}.corrupt-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(path, copy, overwrite: true);
            return copy;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a file that another process may be writing at this very moment: a sharing violation
    /// is retried a few times before it is reported. Null means the file does not exist.
    /// </summary>
    /// <exception cref="IOException">The file stayed locked.</exception>
    public static string? ReadAllTextOrNull(string path, int attempts = 4)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch (IOException) when (attempt < attempts)
            {
                Thread.Sleep(75 * attempt);
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Leftover temp file: replaced on the next save.
        }
    }
}
