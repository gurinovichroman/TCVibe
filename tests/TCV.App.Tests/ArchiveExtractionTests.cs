using System.IO;
using System.Text;
using FluentAssertions;
using TCV.App.Operations;
using TCV.App.Operations.Conflict;
using TCV.Core.Domain;
using TCV.Core.Interfaces;
using TCV.Core.PluginSdk;

namespace TCV.App.Tests;

/// <summary>
/// Extracting from an archive (F5/F6 out of an archive panel, dragging out, pasting): every name
/// settled first, then one read of the archive for all the files instead of one per file (6.37).
/// </summary>
public sealed class ArchiveExtractionTests : IDisposable
{
    private const string Archive = "fake.7z";
    private static readonly DateTime Stamp = new(2024, 5, 1, 12, 0, 0);

    private readonly string _dest = Path.Combine(Path.GetTempPath(), "tcv-extract-" + Guid.NewGuid().ToString("N"));

    public ArchiveExtractionTests() => Directory.CreateDirectory(_dest);

    public void Dispose() => Directory.Delete(_dest, recursive: true);

    private static readonly ArchiveExtractItem Dir = new("dir", "dir", true, 0, Stamp);

    private static FakeArchiveEngine FolderOfFive() => new FakeArchiveEngine()
        .Add("dir/a.txt", "alpha")
        .Add("dir/b.txt", "bravo")
        .Add("dir/c.txt", "charlie")
        .Add("dir/sub/d.txt", "delta")
        .Add("dir/sub/e.txt", "echo")
        .Add("other.txt", "not selected");

    private string At(params string[] parts) => Path.Combine([_dest, .. parts]);

    private sealed class Run
    {
        public required IReadOnlyList<ArchiveExtractItem> Landed { get; init; }
        public required DelegateOperation Op { get; init; }
    }

    private async Task<Run> ExtractAsync(
        FakeArchiveEngine engine, IReadOnlyList<ArchiveExtractItem> items,
        Func<IFileEntry, IFileEntry, Task<(ConflictResolution, bool)>>? ask = null,
        OperationErrorHandler? onError = null, IProgress<FileProgress>? progress = null,
        CancellationToken ct = default, CopyOptions? options = null, IConflictStrategy? conflicts = null)
    {
        var op = new DelegateOperation(
            new OperationLogDescriptor(OperationKind.Extract, Archive, _dest, items.Count), (_, _) => Task.CompletedTask)
        {
            Progress = progress,
            ErrorHandler = onError,
        };
        ask ??= (source, _) => throw new InvalidOperationException($"No conflict expected for {source.Name}");
        conflicts ??= new AskUserStrategy(async (conflict, _) => (await ask(conflict.Source, conflict.Existing)).Item1);
        var extraction = new ArchiveExtraction(engine, Archive, engine.ListAll(Archive), op, conflicts, options);

        var landed = await extraction.RunAsync(items, _dest, ct);
        return new Run { Landed = landed, Op = op };
    }

    [Fact]
    public async Task RunAsync_Folder_ReadsTheArchiveOnceInsteadOfOncePerFile()
    {
        var engine = FolderOfFive();

        var run = await ExtractAsync(engine, [Dir]);

        engine.ReadEntriesCalls.Should().Be(1);
        engine.ExtractEntryCalls.Should().Be(0);
        engine.Requested.Should().BeEquivalentTo("dir/a.txt", "dir/b.txt", "dir/c.txt", "dir/sub/d.txt", "dir/sub/e.txt");
        File.ReadAllText(At("dir", "a.txt")).Should().Be("alpha");
        File.ReadAllText(At("dir", "sub", "e.txt")).Should().Be("echo");
        File.Exists(At("other.txt")).Should().BeFalse();
        File.GetLastWriteTime(At("dir", "b.txt")).Should().Be(Stamp);
        run.Landed.Should().Equal(Dir);
        run.Op.SkippedCount.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_SeveralSelectedFiles_ReadsTheArchiveOnce()
    {
        var engine = FolderOfFive();
        ArchiveExtractItem[] files =
        [
            new("dir/a.txt", "a.txt", false, 5, Stamp),
            new("dir/c.txt", "c.txt", false, 7, Stamp),
        ];

        var run = await ExtractAsync(engine, files);

        engine.ReadEntriesCalls.Should().Be(1);
        engine.ExtractEntryCalls.Should().Be(0);
        File.ReadAllText(At("a.txt")).Should().Be("alpha");
        File.ReadAllText(At("c.txt")).Should().Be("charlie");
        run.Landed.Should().Equal(files);
    }

    [Fact]
    public async Task RunAsync_ExistingFileSkipped_KeepsItAndTheFolderDidNotLandWhole()
    {
        var engine = FolderOfFive();
        Directory.CreateDirectory(At("dir"));
        File.WriteAllText(At("dir", "b.txt"), "old");

        var run = await ExtractAsync(engine, [Dir], ask: (_, _) => Task.FromResult((ConflictResolution.Skip, false)));

        File.ReadAllText(At("dir", "b.txt")).Should().Be("old");
        File.ReadAllText(At("dir", "a.txt")).Should().Be("alpha");
        engine.Requested.Should().NotContain("dir/b.txt");
        run.Landed.Should().BeEmpty();
        run.Op.SkippedCount.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_Overwrite_ReplacesTheFileAndLeavesNoScratchBehind()
    {
        var engine = FolderOfFive();
        Directory.CreateDirectory(At("dir"));
        File.WriteAllText(At("dir", "b.txt"), "old");

        var run = await ExtractAsync(engine, [Dir], ask: (_, _) => Task.FromResult((ConflictResolution.Overwrite, false)));

        File.ReadAllText(At("dir", "b.txt")).Should().Be("bravo");
        Directory.GetFiles(At("dir"), "*.tcvtmp").Should().BeEmpty();
        run.Landed.Should().Equal(Dir);
        engine.ReadEntriesCalls.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_OverwriteOfAFileHeldOpen_SkipLeavesTheOldFileAndNoScratch()
    {
        var engine = FolderOfFive();
        Directory.CreateDirectory(At("dir"));
        File.WriteAllText(At("dir", "b.txt"), "old");
        File.SetAttributes(At("dir", "b.txt"), FileAttributes.ReadOnly);

        Run run;
        // Held without delete sharing, as an editor or a viewer may: the copy is written, the swap fails.
        using (new FileStream(At("dir", "b.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            run = await ExtractAsync(engine, [Dir],
                ask: (_, _) => Task.FromResult((ConflictResolution.Overwrite, false)),
                onError: (_, _) => Task.FromResult(OperationErrorResolution.Skip));
        }

        File.ReadAllText(At("dir", "b.txt")).Should().Be("old");
        File.GetAttributes(At("dir", "b.txt")).Should().HaveFlag(FileAttributes.ReadOnly);
        Directory.GetFiles(At("dir"), "*.tcvtmp", SearchOption.AllDirectories).Should().BeEmpty();
        run.Landed.Should().BeEmpty();
        run.Op.SkippedCount.Should().Be(1);

        File.SetAttributes(At("dir", "b.txt"), FileAttributes.Normal);   // so Dispose can clean up
    }

    [Fact]
    public async Task RunAsync_Rename_KeepsBothVersions()
    {
        // "Apply to all" is the ask strategy's memory (BuildAskStrategy); here every conflict is asked.
        var engine = FolderOfFive();
        Directory.CreateDirectory(At("dir"));
        File.WriteAllText(At("dir", "a.txt"), "old a");
        File.WriteAllText(At("dir", "b.txt"), "old b");
        var asked = 0;

        var run = await ExtractAsync(engine, [Dir], ask: (_, _) =>
        {
            asked++;
            return Task.FromResult((ConflictResolution.RenameAuto, true));
        });

        asked.Should().Be(2);
        File.ReadAllText(At("dir", "a.txt")).Should().Be("old a");
        File.ReadAllText(At("dir", "a (1).txt")).Should().Be("alpha");
        File.ReadAllText(At("dir", "b (1).txt")).Should().Be("bravo");
        run.Landed.Should().Equal(Dir);
    }

    [Fact]
    public async Task RunAsync_TwoEntriesForOneName_TheSecondIsAskedAboutAndReplacesTheFirst()
    {
        // Two names that are one file on Windows: written one by one, the second used to find the
        // first one there. Settled up front, it still has to.
        var engine = new FakeArchiveEngine().Add("dir/Note.txt", "first").Add("dir/note.txt", "second!");
        IFileEntry? existing = null;

        var run = await ExtractAsync(engine, [Dir], ask: (_, onDisk) =>
        {
            existing = onDisk;
            return Task.FromResult((ConflictResolution.Overwrite, false));
        });

        existing!.Size.Should().Be("first".Length);
        File.ReadAllText(At("dir", "note.txt")).Should().Be("second!");
        engine.ReadEntriesCalls.Should().Be(1);
        engine.ExtractEntryCalls.Should().Be(1);
        run.Landed.Should().Equal(Dir);
    }

    [Fact]
    public async Task RunAsync_EngineLeavesAnEntryOut_ThatEntryIsExtractedOnItsOwn()
    {
        var engine = FolderOfFive();
        engine.LeftOutOfPass.Add("dir/c.txt");

        var run = await ExtractAsync(engine, [Dir]);

        engine.ExtractEntryCalls.Should().Be(1);
        File.ReadAllText(At("dir", "c.txt")).Should().Be("charlie");
        run.Landed.Should().Equal(Dir);
    }

    [Fact]
    public async Task RunAsync_EngineFailsHalfway_TheRestIsExtractedOneByOne()
    {
        var engine = FolderOfFive();
        engine.FailPassAfter = 2;

        var run = await ExtractAsync(engine, [Dir]);

        engine.ExtractEntryCalls.Should().Be(3);
        File.ReadAllText(At("dir", "sub", "e.txt")).Should().Be("echo");
        run.Landed.Should().Equal(Dir);
    }

    [Fact]
    public async Task RunAsync_ReadErrorInThePassAndRetry_ReadsThatEntryAgainOnItsOwn()
    {
        var engine = FolderOfFive();
        engine.BrokenInPass = "dir/b.txt";

        var run = await ExtractAsync(engine, [Dir], onError: (_, _) => Task.FromResult(OperationErrorResolution.Retry));

        engine.ExtractEntryCalls.Should().Be(1);
        File.ReadAllText(At("dir", "b.txt")).Should().Be("bravo");
        run.Landed.Should().Equal(Dir);
    }

    [Fact]
    public async Task RunAsync_ReadErrorInThePassAndSkip_LeavesNoPartialFileAndCountsTheSkip()
    {
        var engine = FolderOfFive();
        engine.BrokenInPass = "dir/b.txt";

        var run = await ExtractAsync(engine, [Dir], onError: (_, _) => Task.FromResult(OperationErrorResolution.Skip));

        File.Exists(At("dir", "b.txt")).Should().BeFalse();
        File.ReadAllText(At("dir", "c.txt")).Should().Be("charlie");
        run.Landed.Should().BeEmpty();
        run.Op.SkippedCount.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_CancelledWhileWriting_LeavesNoHalfWrittenFile()
    {
        var engine = new FakeArchiveEngine().Add("dir/big.bin", new string('x', 400_000)).Add("dir/z.txt", "zulu");
        using var cts = new CancellationTokenSource();
        engine.OnPassRead = path =>
        {
            if (path == "dir/big.bin")
            {
                cts.Cancel();
            }
        };

        var act = () => ExtractAsync(engine, [Dir], ct: cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        File.Exists(At("dir", "big.bin")).Should().BeFalse();
        File.Exists(At("dir", "z.txt")).Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_Folder_ProgressEndsAtTheTotalSize()
    {
        var engine = FolderOfFive();
        var reports = new List<FileProgress>();

        await ExtractAsync(engine, [Dir], progress: new Recorder(reports));

        var total = "alpha".Length + "bravo".Length + "charlie".Length + "delta".Length + "echo".Length;
        reports.Should().NotBeEmpty();
        reports[^1].BytesCopied.Should().Be(total);
        reports[^1].TotalBytes.Should().Be(total);
    }

    // ── The copy dialog's options (6.47) ─────────────────────────────────────

    private sealed class NeverAsked : IConflictStrategy
    {
        public Task<ConflictResolution> ResolveAsync(ConflictInfo conflict, CancellationToken ct) =>
            throw new InvalidOperationException($"{conflict.Source.Name} should have been settled without asking");
    }

    [Fact]
    public async Task RunAsync_SkipChosenInTheDialog_LeavesTakenFilesWithoutAsking()
    {
        var engine = FolderOfFive();
        Directory.CreateDirectory(At("dir"));
        File.WriteAllText(At("dir", "b.txt"), "old");

        var run = await ExtractAsync(engine, [Dir], conflicts: new SkipStrategy());

        File.ReadAllText(At("dir", "b.txt")).Should().Be("old");
        File.ReadAllText(At("dir", "a.txt")).Should().Be("alpha");
        run.Landed.Should().BeEmpty("a move must keep the folder in the archive");
        run.Op.SkippedCount.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_OverwriteChosenInTheDialog_ReplacesWithoutAsking()
    {
        var engine = FolderOfFive();
        Directory.CreateDirectory(At("dir"));
        File.WriteAllText(At("dir", "b.txt"), "old");

        var run = await ExtractAsync(engine, [Dir], conflicts: new OverwriteStrategy());

        File.ReadAllText(At("dir", "b.txt")).Should().Be("bravo");
        run.Landed.Should().Equal(Dir);
    }

    [Fact]
    public async Task RunAsync_OnlyIfNewer_ReplacesOlderFilesAndKeepsNewerOnesWithoutAsking()
    {
        var engine = FolderOfFive();
        Directory.CreateDirectory(At("dir"));
        File.WriteAllText(At("dir", "a.txt"), "older on disk");
        File.SetLastWriteTime(At("dir", "a.txt"), Stamp.AddDays(-1));
        File.WriteAllText(At("dir", "b.txt"), "newer on disk");
        File.SetLastWriteTime(At("dir", "b.txt"), Stamp.AddDays(1));

        var run = await ExtractAsync(engine, [Dir], options: new CopyOptions(OnlyIfNewer: true), conflicts: new NeverAsked());

        File.ReadAllText(At("dir", "a.txt")).Should().Be("alpha");
        File.ReadAllText(At("dir", "b.txt")).Should().Be("newer on disk");
        run.Op.SkippedCount.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_TimestampsKept_FilesAndFoldersGetTheirTimeFromTheArchive()
    {
        var engine = FolderOfFive();

        await ExtractAsync(engine, [Dir], options: new CopyOptions(PreserveTimestamps: true));

        File.GetLastWriteTime(At("dir", "a.txt")).Should().Be(Stamp);
        Directory.GetLastWriteTime(At("dir", "sub")).Should().Be(Stamp);
        Directory.GetLastWriteTime(At("dir")).Should().Be(Stamp, "a folder's time goes last, after its files moved it");
    }

    [Fact]
    public async Task RunAsync_TimestampsNotKept_FilesAndFoldersKeepTheTimeTheyWereWritten()
    {
        var engine = FolderOfFive();
        var before = DateTime.Now.AddMinutes(-1);

        await ExtractAsync(engine, [Dir], options: new CopyOptions(PreserveTimestamps: false));

        File.GetLastWriteTime(At("dir", "a.txt")).Should().BeAfter(before);
        Directory.GetLastWriteTime(At("dir")).Should().BeAfter(before);
    }

    [Fact]
    public async Task RunAsync_SingleFileUnderANewName_IsWrittenAsAFileOfThatName()
    {
        var engine = FolderOfFive();
        var item = new ArchiveExtractItem("dir/a.txt", "renamed.txt", false, 5, Stamp);

        var run = await ExtractAsync(engine, [item]);

        File.ReadAllText(At("renamed.txt")).Should().Be("alpha");
        Directory.Exists(At("renamed.txt")).Should().BeFalse();
        File.Exists(At("a.txt")).Should().BeFalse();
        run.Landed.Should().Equal(item);
    }

    [Fact]
    public async Task RunAsync_FolderUnderANewName_KeepsTheNamesInside()
    {
        var engine = FolderOfFive();

        await ExtractAsync(engine, [Dir with { DisplayName = "copy" }]);

        File.ReadAllText(At("copy", "a.txt")).Should().Be("alpha");
        File.ReadAllText(At("copy", "sub", "e.txt")).Should().Be("echo");
        Directory.Exists(At("dir")).Should().BeFalse();
    }

    private sealed class Recorder(List<FileProgress> reports) : IProgress<FileProgress>
    {
        public void Report(FileProgress value)
        {
            lock (reports)
            {
                reports.Add(value);
            }
        }
    }

    /// <summary>An archive held in memory that counts how it is read.</summary>
    private sealed class FakeArchiveEngine : IArchiveEngine
    {
        private readonly List<(string Path, byte[] Data)> _files = [];

        public int ExtractEntryCalls { get; private set; }
        public int ReadEntriesCalls { get; private set; }
        public List<string> Requested { get; } = [];

        /// <summary>Entries the pass does not hand over, as an engine does with one it cannot match.</summary>
        public HashSet<string> LeftOutOfPass { get; } = [];

        /// <summary>The pass fails on its own after handing over this many entries.</summary>
        public int? FailPassAfter { get; set; }

        /// <summary>Reading this entry inside the pass fails with an IOException.</summary>
        public string? BrokenInPass { get; set; }

        /// <summary>Called on every read from a stream the pass handed over.</summary>
        public Action<string>? OnPassRead { get; set; }

        public FakeArchiveEngine Add(string path, string content)
        {
            _files.Add((path, Encoding.UTF8.GetBytes(content)));
            return this;
        }

        public bool CanWrite => false;

        public IReadOnlyList<ArchiveEntry> ListAll(string archivePath)
        {
            var list = new List<ArchiveEntry>();
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, data) in _files)
            {
                for (var slash = path.IndexOf('/'); slash > 0; slash = path.IndexOf('/', slash + 1))
                {
                    var dir = path[..slash];
                    if (directories.Add(dir))
                    {
                        list.Add(new ArchiveEntry(dir, dir[(dir.LastIndexOf('/') + 1)..], true, 0, Stamp));
                    }
                }

                list.Add(new ArchiveEntry(path, path[(path.LastIndexOf('/') + 1)..], false, data.Length, Stamp));
            }

            return list;
        }

        public Stream ExtractEntry(string archivePath, string entryPath)
        {
            ExtractEntryCalls++;
            var wanted = entryPath.Trim('/');
            var (_, data) = _files.First(f => f.Path == wanted);
            return new MemoryStream(data);
        }

        public void ReadEntries(string archivePath, IReadOnlyList<string> entryPaths, Action<string, Stream> consume,
            CancellationToken ct)
        {
            ReadEntriesCalls++;
            Requested.AddRange(entryPaths);
            var wanted = entryPaths.ToHashSet(StringComparer.Ordinal);
            var handed = 0;
            foreach (var (path, data) in _files)
            {
                if (!wanted.Remove(path) || LeftOutOfPass.Contains(path))
                {
                    continue;
                }

                if (handed == FailPassAfter)
                {
                    throw new InvalidDataException("Damaged block.");
                }

                using var content = new PassStream(this, path, data);
                consume(path, content);
                handed++;
            }
        }

        public void Pack(string archivePath, IEnumerable<(string localPath, string entryName)> files) =>
            throw new NotSupportedException();

        public void ExtractAll(string archivePath, string destDir, IProgress<double>? progress = null) =>
            throw new NotSupportedException();

        public void AddFiles(string archivePath, IEnumerable<(string localPath, string entryName)> files) =>
            throw new NotSupportedException();

        public void DeleteEntry(string archivePath, string entryPath) => throw new NotSupportedException();

        /// <summary>A forward-only stream like the ones a solid archive's reader hands out.</summary>
        private sealed class PassStream(FakeArchiveEngine owner, string path, byte[] data) : MemoryStream(data, writable: false)
        {
            public override bool CanSeek => false;

            public override int Read(byte[] buffer, int offset, int count)
            {
                owner.OnPassRead?.Invoke(path);
                if (path == owner.BrokenInPass)
                {
                    throw new IOException("CRC error.");
                }

                return base.Read(buffer, offset, count);
            }
        }
    }
}
