using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;

namespace TCV.App.Services;

/// <summary>
/// Wraps Windows Shell (Shell.Application COM) and 7-Zip CLI for archive operations.
/// The shell path returns false on failure; the 7-Zip path throws <see cref="SevenZipException"/>
/// carrying 7-Zip's own message.
/// </summary>
public static class WindowsArchiveService
{
    // ---- 7-Zip detection ----

    private static string? _sevenZipExePath;

    /// <summary>
    /// Full path of 7z.exe, or null when it cannot be found. Looks beside the exe first (portable),
    /// then the install location, then PATH. Only a hit is cached, and it is re-checked: a copy
    /// installed (or dropped next to the exe) while the app runs is picked up on the next call.
    /// </summary>
    public static string? GetSevenZipExePath()
    {
        if (_sevenZipExePath is not null && File.Exists(_sevenZipExePath))
        {
            return _sevenZipExePath;
        }

        // In the portable layout "beside the exe" is the root the user sees (TCVibe.exe,
        // and where an older copy kept its 7z.exe), not current\, which every update replaces (TRAP-70).
        var besideProgram = Settings.PortableLayout.Root is { } root
            ? SevenZipLocator.Find(root, [], pathVariable: null)
            : null;
        _sevenZipExePath = besideProgram ?? SevenZipLocator.Find(
            AppContext.BaseDirectory,
            InstalledSevenZipDirectories(),
            Environment.GetEnvironmentVariable("PATH"));
        return _sevenZipExePath;
    }

    public static bool IsSevenZipAvailable() => GetSevenZipExePath() is not null;

    /// <summary>Read-only registry lookup of 7-Zip's install folder; state never goes in the registry (INV-03).</summary>
    private static IEnumerable<string> InstalledSevenZipDirectories()
    {
        foreach (var subKey in new[] { @"SOFTWARE\7-Zip", @"SOFTWARE\WOW6432Node\7-Zip" })
        {
            string? directory = null;
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(subKey);
                directory = key?.GetValue("Path") as string;
            }
            catch (System.Security.SecurityException)
            {
                // Locked-down machine: fall through to PATH.
            }

            if (!string.IsNullOrEmpty(directory))
            {
                yield return directory;
            }
        }
    }

    // ---- ZIP via Shell.Application ----

    /// <summary>
    /// Extracts a ZIP to <paramref name="destDir"/> using Shell.Application CopyHere (silent, no wizard).
    /// The user already chose the destination in our dialog.
    /// </summary>
    /// <remarks>
    /// CopyHere returns as soon as the copy is *queued*, so this waits for the destination to hold
    /// the expected number of files and stop changing. Returns false if the shell stops making
    /// progress before everything has been written.
    /// </remarks>
    public static async Task<bool> ExtractZipSilentAsync(
        string zipPath, string destDir, IProgress<double>? progress, CancellationToken ct)
    {
        int expectedFiles;
        (int Count, long Bytes) baseline;
        try
        {
            // Walking the destination (it may be a whole drive) and reading the central directory is
            // disk work: off the UI thread (INV-06). Only the COM call below needs this (STA) thread.
            (baseline, expectedFiles) = await Task.Run(() =>
            {
                Directory.CreateDirectory(destDir);
                var measured = MeasureDirectory(destDir);
                using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
                return (measured, zip.Entries.Count(e => !string.IsNullOrEmpty(e.Name)));
            }, ct);

            bool StartCopy()
            {
                var shellType = Type.GetTypeFromProgID("Shell.Application");
                if (shellType is null)
                {
                    return false;
                }

                dynamic shell = Activator.CreateInstance(shellType)!;
                var srcFolder  = shell.NameSpace(zipPath);
                var destFolder = shell.NameSpace(destDir);
                if (srcFolder is null || destFolder is null)
                {
                    return false;
                }

                // 4 = no progress dialog, 16 = yes-to-all, 1024 = no error UI
                destFolder.CopyHere(srcFolder.Items(), 4 | 16 | 1024);
                return true;
            }

            // Shell.Application wants an STA thread: the UI thread, whichever thread the caller is on.
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            var started = dispatcher is not null && !dispatcher.CheckAccess()
                ? await dispatcher.InvokeAsync(StartCopy)
                : StartCopy();
            if (!started)
            {
                return false;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }

        return await WaitForShellExtractionAsync(destDir, expectedFiles, baseline, progress, ct);
    }

    private static async Task<bool> WaitForShellExtractionAsync(
        string destDir, int expectedFiles, (int Count, long Bytes) baseline,
        IProgress<double>? progress, CancellationToken ct)
    {
        if (expectedFiles == 0)
        {
            return true;
        }

        var settleTime = TimeSpan.FromMilliseconds(750);
        var stallLimit = TimeSpan.FromSeconds(15);
        // Into an empty folder the file count alone proves the copy finished. Into a folder
        // that already holds files it does not, so give the shell time to get going before
        // an unchanged measurement is allowed to count as "done".
        var startupGrace = baseline.Count == 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(3);

        var elapsed = Stopwatch.StartNew();
        var unchanged = Stopwatch.StartNew();
        var last = baseline;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var snapshot = await Task.Run(() => MeasureDirectory(destDir), ct);
            if (snapshot != last)
            {
                last = snapshot;
                unchanged.Restart();
                progress?.Report(Math.Min(1.0, (double)snapshot.Count / expectedFiles));
            }

            if (snapshot.Count >= expectedFiles
                && unchanged.Elapsed >= settleTime
                && elapsed.Elapsed >= startupGrace)
            {
                return true;
            }

            if (unchanged.Elapsed > stallLimit)
            {
                return false;
            }

            await Task.Delay(250, ct);
        }
    }

    /// <summary>File count and total size below <paramref name="dir"/> — the growth signal we poll on.</summary>
    private static (int Count, long Bytes) MeasureDirectory(string dir)
    {
        try
        {
            int count = 0;
            long bytes = 0;
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                count++;
                try
                {
                    bytes += new FileInfo(file).Length;
                }
                catch (IOException)
                {
                    // File vanished or is locked mid-scan.
                }
            }

            return (count, bytes);
        }
        catch (IOException)
        {
            return (0, 0);
        }
        catch (UnauthorizedAccessException)
        {
            return (0, 0);
        }
    }

    // ---- 7-Zip CLI ----

    /// <summary>
    /// Creates an archive using 7z.exe CLI. Blocks until the process exits.
    /// Pass <paramref name="forceZipFormat"/>=true to write a .zip; otherwise a .7z is written.
    /// A password on a ZIP uses AES-256 (7-Zip's default, ZipCrypto, is weak); on a 7z it also
    /// encrypts the file names.
    /// </summary>
    /// <param name="excludedEntries">
    /// Entry names ("tree/node_modules/x") left out of the archive: 7z enters a junction, so the
    /// caller excludes the links (<see cref="PackEntryCollector.FindLinks"/>).
    /// </param>
    public static async Task PackWith7ZipAsync(
        string archivePath,
        IEnumerable<string> sourceItems,
        ArchiveCompressionLevel compressionLevel,
        string? password,
        bool forceZipFormat,
        IProgress<double>? progress,
        CancellationToken ct,
        IReadOnlyList<string>? excludedEntries = null)
    {
        // "7z a archive --" with no names packs "*" of the process's working directory.
        sourceItems = sourceItems.ToList();
        if (!sourceItems.Any())
        {
            throw new ArgumentException("Nothing to pack.", nameof(sourceItems));
        }

        var exe = GetSevenZipExePath()
            ?? throw new InvalidOperationException("7-Zip is not installed.");

        // In a list file: pnpm's node_modules holds thousands of junctions, past any command line.
        string? excludeList = null;
        if (excludedEntries is { Count: > 0 })
        {
            excludeList = Path.Combine(Path.GetTempPath(), $"tcv-7z-exclude-{Guid.NewGuid():N}.txt");
            await File.WriteAllLinesAsync(excludeList,
                excludedEntries.Select(e => e.Replace('/', '\\')), new UTF8Encoding(false), ct);
        }

        try
        {
            var args = BuildPackArguments(archivePath, sourceItems, compressionLevel, password, forceZipFormat, excludeList);
            // A warning while packing means some file was skipped (locked, unreadable, missing): an
            // archive that silently lacks files is worse than an error, so it fails too.
            await RunProcessAsync(exe, args, password, passwordPrompts: 2, failOnWarning: true, progress, ct);
        }
        finally
        {
            if (excludeList is not null)
            {
                try { File.Delete(excludeList); } catch (IOException) { /* best effort */ }
            }
        }
    }

    /// <summary>
    /// The 7z.exe command line for <see cref="PackWith7ZipAsync"/>. The password is never part of
    /// it (a command line is readable by every process of the user): a bare <c>-p</c> makes 7z ask,
    /// and the runner answers on stdin.
    /// </summary>
    /// <param name="excludeListFile">
    /// A UTF-8 file of entry names to leave out, one per line. 7z anchors each at the archive root:
    /// "tree\loop" excludes that folder and nothing named "loop" elsewhere.
    /// </param>
    public static IReadOnlyList<string> BuildPackArguments(
        string archivePath,
        IEnumerable<string> sourceItems,
        ArchiveCompressionLevel compressionLevel,
        string? password,
        bool forceZipFormat,
        string? excludeListFile = null)
    {
        int mx = compressionLevel switch
        {
            ArchiveCompressionLevel.Store  => 0,
            ArchiveCompressionLevel.Fast   => 3,
            ArchiveCompressionLevel.Normal => 5,
            ArchiveCompressionLevel.Ultra  => 9,
            _                              => 5,
        };

        // -t always explicit: the archive is written under a temporary name, so 7z cannot infer it.
        var args = new List<string> { "a", forceZipFormat ? "-tzip" : "-t7z", $"-mx={mx}", "-bsp1" };

        if (!string.IsNullOrEmpty(password))
        {
            args.Add(forceZipFormat ? "-mem=AES256" : "-mhe=on");
            args.Add("-p");
        }

        if (excludeListFile is not null)
        {
            args.Add("-scsUTF-8");
            args.Add("-x@" + excludeListFile);
        }

        args.Add(archivePath);
        args.Add("--");
        args.AddRange(sourceItems);
        return args;
    }

    /// <summary>Extracts an archive to <paramref name="destDir"/> using 7z.exe CLI.</summary>
    public static async Task ExtractWith7ZipAsync(
        string archivePath,
        string destDir,
        string? password,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        var exe = GetSevenZipExePath()
            ?? throw new InvalidOperationException("7-Zip is not installed.");

        var args = new List<string> { "x", archivePath, "-o" + destDir, "-y", "-bsp1" };
        if (!string.IsNullOrEmpty(password))
        {
            args.Add("-p");
        }

        await RunProcessAsync(exe, args, password, passwordPrompts: 1, failOnWarning: false, progress, ct);
    }

    /// <summary>
    /// Runs 7z.exe: arguments go through ArgumentList (no quoting bugs, no smuggled switches), the
    /// password goes to stdin, both output streams are drained, the process is killed when the
    /// caller cancels, and a failure carries 7-Zip's own words instead of a bare exit code.
    /// </summary>
    private static async Task RunProcessAsync(
        string exe, IReadOnlyList<string> args, string? password, int passwordPrompts, bool failOnWarning,
        IProgress<double>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var psi = new ProcessStartInfo(exe)
        {
            CreateNoWindow         = true,
            UseShellExecute        = false,
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
        };
        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start 7z.exe");
        using var registration = ct.Register(() => KillQuietly(proc));

        var output = new SevenZipOutput(progress);
        var readers = Task.WhenAll(
            Task.Run(() => output.PumpAsync(proc.StandardOutput)),
            Task.Run(() => output.PumpAsync(proc.StandardError)));

        // Answer the password prompt(s), then close stdin so nothing can ever wait on it.
        try
        {
            if (!string.IsNullOrEmpty(password))
            {
                for (var i = 0; i < passwordPrompts; i++)
                {
                    await proc.StandardInput.WriteLineAsync(password);
                }
            }

            proc.StandardInput.Close();
        }
        catch (IOException)
        {
            // 7z already exited (for example on a bad argument); its output explains why.
        }

        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            KillQuietly(proc);
            throw;
        }

        await readers;

        // 7z exit codes: 0=OK, 1=Warning, 2+=error
        if (proc.ExitCode >= (failOnWarning ? 1 : 2))
        {
            throw new SevenZipException(proc.ExitCode, output.Summary());
        }
    }

    private static void KillQuietly(Process proc)
    {
        try
        {
            if (!proc.HasExited)
            {
                proc.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Exited between the check and the kill.
        }
    }
}

/// <summary>A failed 7z.exe run, with 7-Zip's own diagnostics in <see cref="Exception.Message"/>.</summary>
public sealed class SevenZipException : InvalidOperationException
{
    public SevenZipException(int exitCode, string output)
        : base($"7-Zip failed (code {exitCode}): {output}")
    {
        ExitCode = exitCode;
        Output = output;
    }

    public int ExitCode { get; }

    public string Output { get; }

    /// <summary>True when 7-Zip's output says the password was wrong.</summary>
    public bool IsWrongPassword =>
        Output.Contains("Wrong password", StringComparison.OrdinalIgnoreCase) ||
        Output.Contains("encrypted file", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Reads 7-Zip's console output: percentage lines become progress, everything else is kept (the
/// last few lines) to explain a failure.
/// </summary>
public sealed class SevenZipOutput
{
    private const int KeptLines = 12;

    private readonly IProgress<double>? _progress;
    private readonly Queue<string> _tail = new();
    private readonly object _lock = new();

    public SevenZipOutput(IProgress<double>? progress) => _progress = progress;

    public async Task PumpAsync(StreamReader reader)
    {
        string? line;
        while ((line = await reader.ReadLineAsync()) is not null)
        {
            Accept(line);
        }
    }

    public void Accept(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.Length == 0)
        {
            return;
        }

        var percent = trimmed.IndexOf('%');
        if (percent > 0 && double.TryParse(trimmed[..percent].Trim(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            _progress?.Report(Math.Clamp(value / 100.0, 0, 1));
            return;
        }

        lock (_lock)
        {
            _tail.Enqueue(trimmed);
            while (_tail.Count > KeptLines)
            {
                _tail.Dequeue();
            }
        }
    }

    /// <summary>The lines that explain a failure: 7-Zip's ERROR lines, else its last words.</summary>
    public string Summary()
    {
        lock (_lock)
        {
            var errors = _tail.Where(l => l.StartsWith("ERROR", StringComparison.OrdinalIgnoreCase)
                                          || l.Contains("Wrong password", StringComparison.OrdinalIgnoreCase)).ToList();
            return string.Join(" ", errors.Count > 0 ? errors : _tail.TakeLast(3));
        }
    }
}
