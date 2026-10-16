using System.IO;
using System.Text;
using FluentAssertions;
using Moq;
using TCV.App.Viewer;
using TCV.App.ViewModels.Dialogs;
using TCV.Core.Domain;
using TCV.Core.Interfaces;

namespace TCV.App.Tests;

/// <summary>The viewer's window over a big file, its encoding choice, find logic and render limits.</summary>
public class ViewerWindowTests
{
    private const int Window = FileViewerDialogViewModel.WindowBytes;

    private sealed class BytesSource(byte[] all) : IViewerSource
    {
        public long Length => all.Length;
        public bool Disposed { get; private set; }

        public Task<byte[]> ReadAsync(long offset, int count, IProgress<long>? progress, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var take = (int)Math.Min(count, all.Length - offset);
            return Task.FromResult(all.AsSpan((int)offset, take).ToArray());
        }

        public void Dispose() => Disposed = true;
    }

    /// <summary>A log with numbered lines, longer than three windows, so paging has somewhere to go.</summary>
    private static byte[] Log(int lines = 220_000)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < lines; i++)
        {
            sb.Append("line ").Append(i.ToString("D7")).Append(" some log text\n");
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static FileViewerDialogViewModel Open(byte[] all, string name = "app.log")
    {
        var head = all.Length > Window ? all[..Window] : all;
        return new FileViewerDialogViewModel(name, head, all.Length > head.Length, all.Length, null, new BytesSource(all));
    }

    // ── Windows over a big file ───────────────────────────────────────────────

    [Fact]
    public void SmallFile_HasNothingToNavigate()
    {
        var vm = Open(Encoding.UTF8.GetBytes("tiny\n"));

        vm.CanNavigate.Should().BeFalse();
    }

    [Fact]
    public void BigFile_OpensOnItsFirstWindow_AndSaysWhichPartThatIs()
    {
        var vm = Open(Log());

        vm.CanNavigate.Should().BeTrue();
        vm.WindowOffset.Should().Be(0);
        vm.TextContent.Should().StartWith("line 0000000");
        vm.StatusText.Should().Contain("Viewer_Range");
        vm.ShowLineNumbers.Should().BeTrue();
    }

    [Fact]
    public async Task GoToEnd_ShowsTheTail_StartingOnAWholeLine()
    {
        var all = Log();
        var vm = Open(all);

        await vm.GoToEndCommand.ExecuteAsync(null);

        vm.WindowOffset.Should().BeGreaterThan(all.Length - Window - 1);
        vm.TextContent.Should().StartWith("line ", "the partial first line of the window is skipped");
        vm.TextContent.TrimEnd().Should().EndWith("line 0219999 some log text");
        vm.ShowLineNumbers.Should().BeFalse("a window from the middle cannot know its line numbers");
    }

    [Fact]
    public async Task PageForwardThenBack_ReturnsToTheStart()
    {
        var vm = Open(Log());

        await vm.PageForwardCommand.ExecuteAsync(null);
        vm.WindowOffset.Should().BeGreaterThan(0);
        vm.TextContent.Should().StartWith("line ");

        await vm.PageBackCommand.ExecuteAsync(null);
        vm.WindowOffset.Should().Be(0);
        vm.PageBackCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task PageForward_ThroughTheWholeFile_ShowsEveryLineWhole()
    {
        var all = Log();
        var vm = Open(all);
        var seen = new HashSet<string>();

        while (true)
        {
            foreach (var line in vm.TextContent.Split('\n'))
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^line \d{7} some log text$"))
                {
                    seen.Add(line);
                }
            }

            if (!vm.PageForwardCommand.CanExecute(null))
            {
                break;
            }

            await vm.PageForwardCommand.ExecuteAsync(null);
        }

        seen.Should().HaveCount(220_000, "the line cut by a window edge must be shown whole in the next window");
    }

    [Fact]
    public async Task PageBack_EndsWhereTheCurrentWindowStarts()
    {
        var all = Log();
        var vm = Open(all);
        await vm.GoToEndCommand.ExecuteAsync(null);
        var tailStart = vm.WindowOffset;

        await vm.PageBackCommand.ExecuteAsync(null);

        (vm.WindowOffset + Encoding.UTF8.GetByteCount(vm.TextContent)).Should().BeGreaterThanOrEqualTo(tailStart);
    }

    [Fact]
    public async Task GoToEnd_Utf16LittleEndian_StartsOnACodeUnitNotHalfOfOne()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 150_000; i++)
        {
            sb.Append("строка ").Append(i.ToString("D6")).Append(" текст\n");
        }

        var all = Encoding.Unicode.GetBytes(sb.ToString());
        var vm = Open(all, "unicode.log");
        vm.ShowTextCommand.Execute(null);   // mostly Cyrillic, too few NULs for auto-detection: chosen by hand
        vm.SelectedEncoding = vm.Encodings.First(e => e.Name == "UTF-16 LE");

        await vm.GoToEndCommand.ExecuteAsync(null);

        (vm.WindowOffset % 2).Should().Be(0);
        vm.TextContent.Should().StartWith("строка ");
        vm.TextContent.TrimEnd().Should().EndWith("строка 149999 текст");
    }

    [Fact]
    public void FirstAndLastLineStart_Utf16LittleEndian_FindWholeLineFeeds()
    {
        // "Њ" is U+040A: its low byte is 0x0A, which is not a line feed.
        var bytes = Encoding.Unicode.GetBytes("Њa\nb\nc");

        ViewerContent.FirstLineStart(bytes, Encoding.Unicode).Should().Be(6);
        ViewerContent.LastLineStart(bytes, Encoding.Unicode).Should().Be(10);
        ViewerContent.FirstLineStart(Encoding.UTF8.GetBytes("ab\ncd"), Encoding.UTF8).Should().Be(3);
        ViewerContent.LastLineStart(Encoding.UTF8.GetBytes("no break"), Encoding.UTF8).Should().Be(0);
    }

    [Fact]
    public async Task FindBeyondWindow_MatchLaterInTheFile_MovesToTheWindowHoldingIt()
    {
        var vm = Open(Log());

        var next = await vm.FindBeyondWindowAsync("line 0200000 some");

        next.Should().BeGreaterThanOrEqualTo(0);
        var index = TextFinder.Find(vm.TextContent, "line 0200000 some", next, forward: true, out var wrapped);
        wrapped.Should().BeFalse();
        LineAt(vm.TextContent, index).Should().Be("line 0200000 some log text");
        vm.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task FindBeyondWindow_NoMatchAnywhere_KeepsTheWindow()
    {
        var vm = Open(Log());

        var next = await vm.FindBeyondWindowAsync("no such text");

        next.Should().Be(-1);
        vm.WindowOffset.Should().Be(0);
    }

    [Fact]
    public async Task FindBeyondWindow_MatchInTheLineTheWindowEdgeCut_IsNotFoundAgain()
    {
        // The window shows the start of the line its edge cut through, and the next window starts with that
        // line: a match already on screen there must not be stepped onto a second time.
        var vm = Open(Log());
        var cutLine = vm.TextContent[(vm.TextContent.LastIndexOf('\n') + 1)..];   // "line 0155344 som"
        var number = int.Parse(cutLine[5..12]);
        var query = cutLine[..10];                                               // "line 01553": lines 155300-155399
        (number % 100).Should().BeLessThan(99, "the line after must share the prefix");

        var next = await vm.FindBeyondWindowAsync(query);

        next.Should().BeGreaterThanOrEqualTo(0);
        var index = TextFinder.Find(vm.TextContent, query, next, forward: true, out _);
        LineAt(vm.TextContent, index).Should().StartWith($"line {number + 1:D7}");
    }

    [Fact]
    public async Task FindBeyondWindow_OnlyMatchAlreadyOnScreen_KeepsTheWindow()
    {
        var vm = Open(Log());
        var cutLine = vm.TextContent[(vm.TextContent.LastIndexOf('\n') + 1)..];

        var next = await vm.FindBeyondWindowAsync(cutLine[..12]);   // "line 0155344": that line alone

        next.Should().Be(-1);
        vm.WindowOffset.Should().Be(0);
    }

    [Fact]
    public async Task FindBeyondWindow_MatchAcrossTheWindowEdgeInALongLine_IsFound()
    {
        // A minified bundle: one line longer than the window, cut wherever the window ends (6.46).
        var all = Encoding.UTF8.GetBytes(new string('x', 3 * Window).Insert(Window - 3, "NEEDLE"));
        var vm = Open(all);
        TextFinder.Find(vm.TextContent, "needle", 0, forward: true, out _).Should().Be(-1, "the window shows only its first half");

        var next = await vm.FindBeyondWindowAsync("needle");

        next.Should().BeGreaterThanOrEqualTo(0);
        var index = TextFinder.Find(vm.TextContent, "needle", next, forward: true, out var wrapped);
        wrapped.Should().BeFalse();
        vm.TextContent.Substring(index, 6).Should().Be("NEEDLE");
        (vm.WindowOffset + index).Should().Be(Window - 3, "the text before the match is plain ASCII");
    }

    [Fact]
    public async Task FindBeforeWindow_MatchAcrossTheWindowEdgeInALongLine_IsFound()
    {
        var all = Encoding.UTF8.GetBytes(new string('x', 3 * Window).Insert(Window - 3, "NEEDLE"));
        var vm = Open(all);
        await vm.MoveWindowAsync(Window, alignToLine: false, exact: true);   // starts inside "NEEDLE"
        vm.TextContent.Should().StartWith("DLE");

        var limit = await vm.FindBeforeWindowAsync("needle");

        limit.Should().BeGreaterThanOrEqualTo(0);
        var index = TextFinder.Find(vm.TextContent, "needle", limit, forward: false, out var wrapped);
        wrapped.Should().BeFalse();
        (vm.WindowOffset + index).Should().Be(Window - 3);
    }

    [Fact]
    public async Task FindBeyondWindow_StartedOnTheDispatcher_SearchesTheWindowsOffIt()
    {
        // Each window searched is up to 4 MB to decode (INV-06). Only the window then shown is read on
        // the dispatcher, the way paging reads it.
        var all = Log();
        var source = new ContextRecordingSource(all);
        var ui = new RecordingContext();
        var previous = SynchronizationContext.Current;
        Task<int> search;
        SynchronizationContext.SetSynchronizationContext(ui);
        try
        {
            var vm = new FileViewerDialogViewModel("app.log", all[..Window], true, all.Length, null, source);
            search = vm.FindBeyondWindowAsync("line 0200000 some");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        (await search).Should().BeGreaterThanOrEqualTo(0);
        source.Seen.Should().HaveCountGreaterThan(1);
        source.Seen.SkipLast(1).Should().NotContain(ui);
        source.Seen[^1].Should().BeSameAs(ui, "the window found is shown from the dispatcher");
    }

    /// <summary>Stands in for the dispatcher: whatever is posted to it runs with it installed.</summary>
    private sealed class RecordingContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) =>
            ThreadPool.QueueUserWorkItem(_ =>
            {
                SetSynchronizationContext(this);
                d(state);
            });
    }

    /// <summary>A <see cref="BytesSource"/> that notes the context each read was asked for on.</summary>
    private sealed class ContextRecordingSource(byte[] all) : IViewerSource
    {
        private readonly BytesSource _inner = new(all);
        private readonly List<SynchronizationContext?> _seen = [];

        public long Length => _inner.Length;

        public IReadOnlyList<SynchronizationContext?> Seen
        {
            get
            {
                lock (_seen)
                {
                    return [.. _seen];
                }
            }
        }

        public Task<byte[]> ReadAsync(long offset, int count, IProgress<long>? progress, CancellationToken ct)
        {
            lock (_seen)
            {
                _seen.Add(SynchronizationContext.Current);
            }

            return _inner.ReadAsync(offset, count, progress, ct);
        }

        public void Dispose() => _inner.Dispose();
    }

    [Fact]
    public async Task FindBeforeWindow_MatchEarlierInTheFile_MovesToTheNearestOne()
    {
        var vm = Open(Log());
        await vm.GoToEndCommand.ExecuteAsync(null);
        var tailStart = vm.WindowOffset;
        var lineBefore = (int)(tailStart / LogLineBytes) - 1;   // the tail starts on a whole line

        // Every line holds the text: the nearest match is on the line just before the tail.
        var limit = await vm.FindBeforeWindowAsync("some log text");

        limit.Should().BeGreaterThanOrEqualTo(0);
        vm.WindowOffset.Should().BeLessThan(tailStart);
        vm.IsBusy.Should().BeFalse();
        var index = TextFinder.Find(vm.TextContent, "some log text", limit, forward: false, out var wrapped);
        wrapped.Should().BeFalse();
        LineAt(vm.TextContent, index).Should().Be($"line {lineBefore:D7} some log text");
    }

    [Fact]
    public async Task FindBeforeWindow_MatchNearTheStartOfTheFile_IsFound()
    {
        var vm = Open(Log());
        await vm.GoToEndCommand.ExecuteAsync(null);

        var limit = await vm.FindBeforeWindowAsync("LINE 0000042 some");   // case is ignored, as in the window

        limit.Should().BeGreaterThanOrEqualTo(0);
        vm.WindowOffset.Should().Be(0);
        var index = TextFinder.Find(vm.TextContent, "LINE 0000042 some", limit, forward: false, out _);
        LineAt(vm.TextContent, index).Should().Be("line 0000042 some log text");
    }

    [Fact]
    public async Task FindBeforeWindow_NoMatchEarlier_KeepsTheWindow()
    {
        var vm = Open(Log());
        await vm.GoToEndCommand.ExecuteAsync(null);
        var tailStart = vm.WindowOffset;

        var limit = await vm.FindBeforeWindowAsync("line 0219999");   // the last line: only in the tail

        limit.Should().Be(-1);
        vm.WindowOffset.Should().Be(tailStart);
        vm.IsBusy.Should().BeFalse();
    }

    [Fact]
    public void FindBeforeWindow_AtTheFirstWindow_IsUnavailable()
    {
        Open(Log()).CanFindBeforeWindow.Should().BeFalse("there is nothing before the start of the file");
    }

    [Fact]
    public async Task FindBeforeWindow_InHex_IsUnavailable()
    {
        var vm = Open(Log());
        await vm.GoToEndCommand.ExecuteAsync(null);
        vm.ShowHexCommand.Execute(null);

        vm.CanFindBeforeWindow.Should().BeFalse();
    }

    /// <summary>Bytes per line of <see cref="Log"/>: "line 0000000 some log text\n".</summary>
    private const int LogLineBytes = 27;

    private static string LineAt(string text, int index)
    {
        index.Should().BeGreaterThanOrEqualTo(0);
        var start = text.LastIndexOf('\n', index) + 1;
        var end = text.IndexOf('\n', index);
        return text[start..(end < 0 ? text.Length : end)];
    }

    // ── Find previous, block by block (pure) ──────────────────────────────────

    /// <summary>Numbered rows, each holding its own number once: "row 000 abcdefghij\n".</summary>
    private static byte[] Rows(int count, Encoding encoding)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            sb.Append("row ").Append(i.ToString("D3")).Append(" abcdefghij\n");
        }

        return encoding.GetBytes(sb.ToString());
    }

    private static string Decode(byte[] all, TextBlock block, Encoding encoding) =>
        encoding.GetString(all, (int)block.Start, (int)(block.End - block.Start));

    [Theory]
    [InlineData(50)]    // smaller than two rows: every row is cut by some block edge
    [InlineData(64)]
    [InlineData(4096)]
    public async Task FindBackwardAsync_EveryRow_IsFoundWholeWhereverTheBlockEdgesFall(int blockBytes)
    {
        var all = Rows(200, Encoding.UTF8);
        var source = new BytesSource(all);

        for (var row = 0; row < 200; row++)
        {
            var query = $"row {row:D3} abc";
            var block = await TextBlockSearch.FindBackwardAsync(source, all.Length, query, null, blockBytes, null, default);

            block.Should().NotBeNull(query);
            Decode(all, block!.Value, Encoding.UTF8).Should().Contain(query);
            (block.Value.End - block.Value.Start).Should().BeLessThanOrEqualTo(blockBytes);
        }
    }

    [Fact]
    public async Task FindBackwardAsync_SeveralMatches_ReturnsTheBlockOfTheNearest()
    {
        var all = Rows(200, Encoding.UTF8);
        var end = Encoding.UTF8.GetByteCount("row 000 abcdefghij\n") * 150;   // the start of row 150

        var block = await TextBlockSearch.FindBackwardAsync(new BytesSource(all), end, "abcdefghij", null, 64, null, default);

        block.Should().NotBeNull();
        block!.Value.End.Should().Be(end, "the row just before the end is the nearest match");
        Decode(all, block.Value, Encoding.UTF8).Should().EndWith("row 149 abcdefghij\n");
        all[(int)block.Value.Start - 1].Should().Be((byte)'\n', "a block's text starts on a whole line");
    }

    [Fact]
    public async Task FindBackwardAsync_NothingBeforeTheEnd_ReturnsNull()
    {
        var all = Rows(200, Encoding.UTF8);
        var end = Encoding.UTF8.GetByteCount("row 000 abcdefghij\n") * 100;

        var block = await TextBlockSearch.FindBackwardAsync(new BytesSource(all), end, "row 150", null, 64, null, default);

        block.Should().BeNull("row 150 lies after the end searched back from");
    }

    [Theory]
    [InlineData(997)]    // across the end searched back from (6.46)
    [InlineData(999)]
    [InlineData(994)]    // ends right at it
    public async Task FindBackwardAsync_OneLongLine_FindsAMatchStartingBeforeTheEnd(int at)
    {
        var all = Encoding.UTF8.GetBytes(new string('x', 10_000).Insert(at, "NEEDLE"));

        var block = await TextBlockSearch.FindBackwardAsync(new BytesSource(all), 1_000, "needle", null, 64, null, default);

        block.Should().NotBeNull();
        Decode(all, block!.Value, Encoding.UTF8).Should().Contain("NEEDLE");
    }

    [Fact]
    public async Task FindBackwardAsync_MatchStartingAtTheEnd_DoesNotCount()
    {
        // It is in the window searched back from, not before it.
        var all = Encoding.UTF8.GetBytes(new string('x', 10_000).Insert(1_000, "NEEDLE"));

        var block = await TextBlockSearch.FindBackwardAsync(new BytesSource(all), 1_000, "needle", null, 64, null, default);

        block.Should().BeNull();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(61)]
    [InlineData(62)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(5_000)]
    [InlineData(9_940)]   // across the edge of the first block read
    [InlineData(9_990)]
    public async Task FindBackwardAsync_OneLineLongerThanABlock_FindsAMatchAcrossABlockEdge(int at)
    {
        var text = new string('x', 10_000).Insert(at, "NEEDLE");
        var all = Encoding.UTF8.GetBytes(text);

        var block = await TextBlockSearch.FindBackwardAsync(new BytesSource(all), all.Length, "needle", null, 64, null, default);

        block.Should().NotBeNull();
        Decode(all, block!.Value, Encoding.UTF8).Should().Contain("NEEDLE");
    }

    [Fact]
    public async Task FindBackwardAsync_Utf8WithoutLineBreaks_JudgesEachBlockAsUtf8()
    {
        // Three-byte characters and no line breaks: blocks start in the middle of a character, and a
        // block judged on its raw bytes would fail strict UTF-8 and be read as Latin-1 (TRAP-33).
        var text = string.Concat(Enumerable.Repeat("日本語のテキスト", 400)).Insert(1_000, "иголка");
        var all = new UTF8Encoding(false).GetBytes(text);

        var block = await TextBlockSearch.FindBackwardAsync(new BytesSource(all), all.Length, "ИГОЛКА", null, 256, null, default);

        block.Should().NotBeNull();
    }

    [Fact]
    public async Task FindBackwardAsync_Utf16_KeepsBlocksOnCodeUnits()
    {
        var all = Rows(200, Encoding.Unicode);

        var block = await TextBlockSearch.FindBackwardAsync(new BytesSource(all), all.Length, "row 007 abc", Encoding.Unicode, 101, null, default);

        block.Should().NotBeNull();
        (block!.Value.Start % 2).Should().Be(0);
        Decode(all, block.Value, Encoding.Unicode).Should().Contain("row 007 abc");
    }

    [Fact]
    public async Task FindBackwardAsync_Cancelled_Throws()
    {
        var all = Rows(200, Encoding.UTF8);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var search = () => TextBlockSearch.FindBackwardAsync(new BytesSource(all), all.Length, "row 001", null, 64, null, cts.Token);

        await search.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void Cut_BlockWithALineBreak_StartsOnTheNextLineAndHandsTheCutLineBack()
    {
        var bytes = Encoding.UTF8.GetBytes("ail of a line\nnext line\n");

        var (textStart, previousEnd) = TextBlockSearch.Cut(bytes, 1000, Encoding.UTF8, 8);

        textStart.Should().Be(14);
        previousEnd.Should().Be(1014, "the line cut by the block start is searched whole with the block before");
    }

    [Fact]
    public void Cut_BlockWithoutALineBreak_OverlapsTheBlockBefore()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('x', 100));

        var (textStart, previousEnd) = TextBlockSearch.Cut(bytes, 1000, Encoding.UTF8, 8);

        textStart.Should().Be(0);
        previousEnd.Should().Be(1008);
    }

    [Fact]
    public void Cut_LineBreakOnlyAtTheVeryEnd_CountsAsNoLineBreak()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('x', 99) + "\n");

        TextBlockSearch.Cut(bytes, 1000, Encoding.UTF8, 8).Should().Be((0, 1008L));
    }

    [Fact]
    public void Cut_OverlapAsLongAsTheBlock_StillMovesBack()
    {
        var bytes = Encoding.UTF8.GetBytes(new string('x', 16));

        TextBlockSearch.Cut(bytes, 1000, Encoding.UTF8, 64).PreviousEnd.Should().Be(1000);
    }

    [Fact]
    public void Cut_StartOfTheFile_HasNothingBefore()
    {
        TextBlockSearch.Cut(Encoding.UTF8.GetBytes("ab\ncd"), 0, Encoding.UTF8, 8).Should().Be((0, 0L));
    }

    // ── Find next, window by window (pure) ────────────────────────────────────

    /// <summary>The text a window read at <paramref name="start"/> shows, cut as the viewer cuts it.</summary>
    private static string WindowText(byte[] all, long start, int windowBytes, Encoding encoding)
    {
        var bytes = all[(int)start..(int)Math.Min(all.Length, start + windowBytes)];
        var shown = start + bytes.Length < all.Length ? ViewerContent.TrimIncompleteTail(bytes, encoding) : bytes;
        return encoding.GetString(shown);
    }

    [Theory]
    [InlineData(50)]    // smaller than three rows: every row is cut by some window edge
    [InlineData(64)]
    [InlineData(4096)]
    public async Task FindForwardAsync_EveryRow_IsFoundInAWindowThatShowsItWhole(int windowBytes)
    {
        var all = Rows(200, Encoding.UTF8);
        var source = new BytesSource(all);

        for (var row = 0; row < 200; row++)
        {
            var query = $"row {row:D3} abc";
            var found = await TextBlockSearch.FindForwardAsync(source, 0, 0, all.Length, query, null, windowBytes, null, default);

            found.Should().NotBeNull(query);
            WindowText(all, found!.Value.Start, windowBytes, Encoding.UTF8).Should().Contain(query);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(58)]    // ends right at the edge of the first window
    [InlineData(59)]    // across it
    [InlineData(60)]
    [InlineData(63)]
    [InlineData(64)]    // starts right at it
    [InlineData(100)]   // across a later edge: windows overlap by the query, so those fall every 40 bytes
    [InlineData(5_000)]
    [InlineData(9_994)]
    public async Task FindForwardAsync_OneLineLongerThanAWindow_FindsAMatchAcrossAWindowEdge(int at)
    {
        var text = new string('x', 10_000).Insert(at, "NEEDLE");
        var all = Encoding.UTF8.GetBytes(text);

        var found = await TextBlockSearch.FindForwardAsync(new BytesSource(all), 0, 0, all.Length, "needle", null, 64, null, default);

        found.Should().NotBeNull();
        WindowText(all, found!.Value.Start, 64, Encoding.UTF8).Should().Contain("NEEDLE");
    }

    [Fact]
    public async Task FindForwardAsync_Utf8WithoutLineBreaks_KeepsWindowsOnCharacters()
    {
        // Three-byte characters and no line breaks: a window that goes back over the edge must start on a
        // character, or the text it shows would open with a replacement character (TRAP-33).
        var text = string.Concat(Enumerable.Repeat("日本語のテキスト", 400)).Insert(1_000, "иголка");
        var all = new UTF8Encoding(false).GetBytes(text);

        var found = await TextBlockSearch.FindForwardAsync(new BytesSource(all), 0, 0, all.Length, "ИГОЛКА", null, 256, null, default);

        found.Should().NotBeNull();
        (all[found!.Value.Start] & 0xC0).Should().NotBe(0x80, "a window starts on the first byte of a character");
        WindowText(all, found.Value.Start, 256, Encoding.UTF8).Should().Contain("иголка");
    }

    [Fact]
    public async Task After_WindowInALongLine_GoesOnBeforeItsEndSoAMatchAcrossItIsSeen()
    {
        var all = Encoding.UTF8.GetBytes(new string('x', 1_000).Insert(61, "NEEDLE"));
        var (start, seen) = TextBlockSearch.After(all[..64], 0, Encoding.UTF8, 64, "needle");

        seen.Should().Be(64);
        start.Should().BeLessThan(61, "the next window must hold the match that the edge cut");
        var found = await TextBlockSearch.FindForwardAsync(new BytesSource(all), start, seen, all.Length, "needle", null, 64, null, default);
        found.Should().Be(new WindowMatch(start, seen));
    }

    [Fact]
    public async Task FindForwardAsync_MatchWhollyBeforeSeen_DoesNotCount()
    {
        // The first window shows rows 0-2 whole and "row 003" of the fourth; the next starts on row 3.
        var all = Rows(200, Encoding.UTF8);
        var (start, seen) = TextBlockSearch.After(all[..64], 0, Encoding.UTF8, 64, "row 003");
        start.Should().Be(57);
        seen.Should().Be(64);

        var onScreen = await TextBlockSearch.FindForwardAsync(new BytesSource(all), start, seen, all.Length, "row 003", null, 64, null, default);
        var acrossTheEdge = await TextBlockSearch.FindForwardAsync(new BytesSource(all), start, seen, all.Length, "row 003 abc", null, 64, null, default);

        onScreen.Should().BeNull("the window already showed it, and it occurs nowhere else");
        acrossTheEdge.Should().Be(new WindowMatch(57, 64));
    }

    [Fact]
    public async Task FindForwardAsync_SeveralMatches_ReturnsTheFirstWindowFromTheStart()
    {
        var all = Rows(200, Encoding.UTF8);
        var start = Encoding.UTF8.GetByteCount("row 000 abcdefghij\n") * 150;   // the start of row 150

        var found = await TextBlockSearch.FindForwardAsync(new BytesSource(all), start, start, all.Length, "abcdefghij", null, 64, null, default);

        found.Should().Be(new WindowMatch(start, start));
    }

    [Fact]
    public async Task FindForwardAsync_NothingAfterTheStart_ReturnsNull()
    {
        var all = Rows(200, Encoding.UTF8);
        var start = Encoding.UTF8.GetByteCount("row 000 abcdefghij\n") * 100;

        var found = await TextBlockSearch.FindForwardAsync(new BytesSource(all), start, start, all.Length, "row 050", null, 64, null, default);

        found.Should().BeNull("row 050 lies before the start searched from");
    }

    [Fact]
    public async Task FindForwardAsync_Utf16_KeepsWindowsOnCodeUnits()
    {
        var all = Rows(200, Encoding.Unicode);

        var found = await TextBlockSearch.FindForwardAsync(new BytesSource(all), 0, 0, all.Length, "row 193 abc", Encoding.Unicode, 101, null, default);

        found.Should().NotBeNull();
        (found!.Value.Start % 2).Should().Be(0);
        WindowText(all, found.Value.Start, 101, Encoding.Unicode).Should().Contain("row 193 abc");
    }

    [Fact]
    public async Task FindForwardAsync_ReportsEachWindowRead()
    {
        var all = Rows(200, Encoding.UTF8);
        var reported = new List<long>();

        await TextBlockSearch.FindForwardAsync(
            new BytesSource(all), 0, 0, all.Length, "row 199", null, 1024, new SynchronousProgress(reported.Add), default);

        reported.Should().StartWith(0L).And.BeInAscendingOrder().And.HaveCountGreaterThan(1);
    }

    [Fact]
    public async Task FindForwardAsync_Cancelled_Throws()
    {
        var all = Rows(200, Encoding.UTF8);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var search = () => TextBlockSearch.FindForwardAsync(new BytesSource(all), 0, 0, all.Length, "row 199", null, 64, null, cts.Token);

        await search.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public void NextWindowStart_WindowWithLineBreaks_StartsAtTheLineItsEdgeCut()
    {
        var bytes = Encoding.UTF8.GetBytes("ab\ncd\nef");

        TextBlockSearch.NextWindowStart(bytes, 1000, Encoding.UTF8, 64).Should().Be(1006);
    }

    [Fact]
    public void NextWindowStart_NoLineBreak_GoesOnAfterTheLastWholeCharacter()
    {
        var bytes = Encoding.UTF8.GetBytes("日本")
            .Concat(Encoding.UTF8.GetBytes("語").Take(1)).ToArray();   // the window edge cut "語"

        TextBlockSearch.NextWindowStart(bytes, 1000, Encoding.UTF8, 64).Should().Be(1006);
    }

    /// <summary>Reports at once: <see cref="Progress{T}"/> posts, so a test would read the list too early.</summary>
    private sealed class SynchronousProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    [Fact]
    public async Task PageForward_AtTheLastWindow_IsUnavailable()
    {
        var vm = Open(Log());
        await vm.GoToEndCommand.ExecuteAsync(null);

        vm.PageForwardCommand.CanExecute(null).Should().BeFalse();
    }

    [Theory]
    [InlineData("50%")]
    [InlineData("0x100000")]
    [InlineData("2MB")]
    public async Task GoToOffset_AcceptsBytesHexPercentAndSizes(string input)
    {
        var vm = Open(Log());
        vm.OffsetInput = input;

        await vm.GoToOffsetCommand.ExecuteAsync(null);

        vm.WindowOffset.Should().BeGreaterThan(0);
        vm.FindStatus.Should().BeEmpty();
    }

    [Fact]
    public async Task GoToOffset_Nonsense_IsReportedAndTheWindowStays()
    {
        var vm = Open(Log());
        vm.OffsetInput = "somewhere";

        await vm.GoToOffsetCommand.ExecuteAsync(null);

        vm.WindowOffset.Should().Be(0);
        vm.FindStatus.Should().Contain("Viewer_BadOffset");
    }

    [Fact]
    public async Task HexView_ShowsRealFileOffsets_AfterAJump()
    {
        var vm = Open(Log());
        vm.ShowHexCommand.Execute(null);
        await vm.PageForwardCommand.ExecuteAsync(null);

        var firstOffset = vm.HexContent[..8];

        Convert.ToInt64(firstOffset, 16).Should().Be(vm.WindowOffset);
        vm.WindowOffset.Should().Be(512 * 1024, "hex pages by what a hex dump shows, not by the text window");
    }

    [Fact]
    public void Window_CutInsideAMultibyteCharacter_DoesNotEndInAReplacementCharacter()
    {
        // 4 MB of "я" (two bytes each) plus more, so the window edge falls between the two bytes of a letter
        var all = new UTF8Encoding(false).GetBytes(new string('я', Window / 2 + 10));
        var head = all[..(Window - 1)];    // deliberately cut mid-character
        var vm = new FileViewerDialogViewModel("big.txt", head, true, all.Length, null, new BytesSource(all));

        vm.TextContent.Should().NotContain("�");
        vm.TextContent.Should().MatchRegex("^я+$");
    }

    [Fact]
    public async Task Jump_ToTheMiddleOfAMultibyteCharacter_StartsOnACharacterBoundary()
    {
        var all = new UTF8Encoding(false).GetBytes(new string('я', Window));
        var vm = Open(all, "big.txt");

        vm.OffsetInput = (Window + 1).ToString();   // odd offset: inside a two-byte letter
        await vm.GoToOffsetCommand.ExecuteAsync(null);

        vm.TextContent.Should().NotContain("�").And.MatchRegex("^я+$");
    }

    [Fact]
    public async Task ReadInFlight_CanBeCancelled_AndTheOldWindowStays()
    {
        var gate = new TaskCompletionSource<byte[]>();
        var all = Log();
        var source = new Mock<IViewerSource>();
        source.SetupGet(s => s.Length).Returns(all.Length);
        source.Setup(s => s.ReadAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<IProgress<long>?>(), It.IsAny<CancellationToken>()))
            .Returns((long _, int _, IProgress<long>? _, CancellationToken ct) => gate.Task.WaitAsync(ct));
        var vm = new FileViewerDialogViewModel("x.log", all[..Window], true, all.Length, null, source.Object);

        var moving = vm.GoToEndCommand.ExecuteAsync(null);
        vm.IsBusy.Should().BeTrue();
        vm.CancelLoadCommand.Execute(null);
        await moving;

        vm.IsBusy.Should().BeFalse();
        vm.WindowOffset.Should().Be(0);
        vm.TextContent.Should().StartWith("line 0000000");
    }

    [Fact]
    public void Dispose_ReleasesTheSource_SoTheMountLeaseIsLetGo()
    {
        var all = Log();
        var source = new BytesSource(all);
        var vm = new FileViewerDialogViewModel("a.log", all[..Window], true, source.Length, null, source);

        vm.Dispose();

        source.Disposed.Should().BeTrue();
    }

    // ── Encoding choice (TRAP-33, legacy code pages) ──────────────────────────

    [Fact]
    public void DetectWindowEncoding_Utf8WindowCutMidCharacter_IsStillUtf8()
    {
        var bytes = new UTF8Encoding(false).GetBytes(new string('я', 5000));
        var cut = bytes[..^1];   // ends between the two bytes of a letter

        FileViewerDialogViewModel.DetectWindowEncoding(cut).Should().BeOfType<UTF8Encoding>();
    }

    [Fact]
    public void DetectWindowEncoding_Utf8WindowStartingMidCharacter_IsStillUtf8()
    {
        var bytes = new UTF8Encoding(false).GetBytes(string.Concat(Enumerable.Repeat("日本語", 2000)));

        FileViewerDialogViewModel.DetectWindowEncoding(bytes[1..]).Should().BeOfType<UTF8Encoding>();
        FileViewerDialogViewModel.DetectWindowEncoding(bytes[2..]).Should().BeOfType<UTF8Encoding>();
    }

    [Fact]
    public void DetectWindowEncoding_Utf16StartingOnAContinuationLikeByte_StaysUtf16()
    {
        // "Ґ" is U+0490: in UTF-16 LE its first byte, 0x90, looks like a UTF-8 continuation byte.
        var bytes = Encoding.Unicode.GetBytes(string.Concat(Enumerable.Repeat("Ґ abc def ", 500)));

        FileViewerDialogViewModel.DetectWindowEncoding(bytes).Should().Be(Encoding.Unicode);
    }

    [Fact]
    public void LargeUtf8Window_IsJudgedOnABoundedSample()
    {
        var bytes = new UTF8Encoding(false).GetBytes(new string('я', 2_000_000));   // ~4 MB

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var encoding = FileViewerDialogViewModel.DetectWindowEncoding(bytes);
        watch.Stop();

        encoding.Should().BeOfType<UTF8Encoding>();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Windows1251File_IsShownAsRussianInAutoMode()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        const string text = "Это обычный русский текст в старой кодировке, который должен открываться без ручного выбора.";
        var bytes = Encoding.GetEncoding(1251).GetBytes(text);

        var vm = new FileViewerDialogViewModel("readme.txt", bytes, false, bytes.Length);

        vm.TextContent.Should().Be(text);
    }

    // ── Images ────────────────────────────────────────────────────────────────

    [Fact]
    public void ImageBiggerThanTheWindow_IsNotDecoded_AndSaysWhy()
    {
        var head = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        var vm = new FileViewerDialogViewModel("huge.png", head, true, 200L * 1024 * 1024, null, new BytesSource(new byte[8]));

        vm.Mode.Should().NotBe(ViewerMode.Image);
        vm.HasImage.Should().BeFalse();
    }

    [Fact]
    public void InitialRead_IsTheWindow_ExceptForAnImageWhichNeedsTheWholeFile()
    {
        ViewerLauncher.InitialReadBytes("big.log", 5_000_000_000).Should().Be(Window);
        ViewerLauncher.InitialReadBytes("photo.png", 3_000_000).Should().Be(3_000_000);
        ViewerLauncher.InitialReadBytes("photo.png", 500_000_000).Should().Be(Window, "too big to decode: show it as bytes");
        ViewerLauncher.InitialReadBytes("empty.txt", 0).Should().Be(Window);
    }

    // ── Source over a file system ─────────────────────────────────────────────

    private sealed class ForwardOnly(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }

    private static VfsViewerSource FromStream(Func<Stream> open, long length, IDisposable? lease = null)
    {
        var fs = new Mock<IVirtualFileSystem>();
        fs.Setup(f => f.OpenReadAsync(It.IsAny<VfsPath>(), It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult(open()));
        return new VfsViewerSource(fs.Object, new VfsPath("/f"), length, lease);
    }

    [Fact]
    public async Task VfsViewerSource_SeekableStream_ReadsTheRequestedRange()
    {
        var data = Enumerable.Range(0, 1000).Select(i => (byte)(i % 251)).ToArray();
        using var source = FromStream(() => new MemoryStream(data), data.Length);

        var read = await source.ReadAsync(300, 100, null, default);

        read.Should().Equal(data[300..400]);
    }

    [Fact]
    public async Task VfsViewerSource_StreamThatCannotSeek_IsSkippedIntoAndReportsProgress()
    {
        var data = Enumerable.Range(0, 1000).Select(i => (byte)(i % 251)).ToArray();
        using var source = FromStream(() => new ForwardOnly(data), data.Length);
        long last = 0;

        var read = await source.ReadAsync(700, 50, new Progress<long>(v => last = v), default);

        read.Should().Equal(data[700..750]);
        await Task.Delay(100);
        last.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task VfsViewerSource_ReadPastTheEnd_ReturnsWhatIsThere()
    {
        var data = new byte[100];
        using var source = FromStream(() => new MemoryStream(data), data.Length);

        (await source.ReadAsync(90, 50, null, default)).Should().HaveCount(10);
        (await source.ReadAsync(500, 50, null, default)).Should().BeEmpty();
    }

    [Fact]
    public void VfsViewerSource_Dispose_ReleasesTheLease()
    {
        var lease = new Mock<IDisposable>();
        var source = FromStream(() => new MemoryStream(), 0, lease.Object);

        source.Dispose();

        lease.Verify(l => l.Dispose(), Times.Once);
    }

    // ── Find ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("alpha beta alpha", "alpha", 0, true, 0, false)]
    [InlineData("alpha beta alpha", "alpha", 5, true, 11, false)]
    [InlineData("alpha beta alpha", "alpha", 12, true, 0, true)]     // past the last: wraps to the top
    [InlineData("alpha beta alpha", "ALPHA", 1, true, 11, false)]    // case-insensitive
    [InlineData("alpha beta alpha", "alpha", 11, false, 0, false)]   // previous from the second one
    [InlineData("alpha beta alpha", "alpha", 0, false, 11, true)]    // previous from the top: wraps to the end
    [InlineData("alpha beta", "gamma", 0, true, -1, false)]
    public void Find_CyclesForwardAndBackward(string text, string query, int from, bool forward, int expected, bool wrapped)
    {
        TextFinder.Find(text, query, from, forward, out var didWrap).Should().Be(expected);
        didWrap.Should().Be(wrapped);
    }

    [Fact]
    public void Find_EmptyQueryOrText_FindsNothing()
    {
        TextFinder.Find("abc", "", 0, true, out _).Should().Be(-1);
        TextFinder.Find("", "a", 0, true, out _).Should().Be(-1);
    }

    // ── Render limits ─────────────────────────────────────────────────────────

    [Fact]
    public void LinesWithinBudget_ManyShortLines_StopAtTheLineCap()
    {
        var lines = Enumerable.Repeat("x", RenderLimits.MaxLines + 500).ToList();

        RenderLimits.LinesWithinBudget(lines).Should().Be(RenderLimits.MaxLines);
    }

    [Fact]
    public void LinesWithinBudget_ManyLongLines_StopAtTheCharacterBudget()
    {
        var lines = Enumerable.Repeat(new string('x', 5_000), 2_000).ToList();   // each counts as MaxLineChars

        var rendered = RenderLimits.LinesWithinBudget(lines);

        rendered.Should().BeLessThan(lines.Count);
        (rendered * RenderLimits.MaxLineChars).Should().BeLessThanOrEqualTo(RenderLimits.MaxChars + RenderLimits.MaxLineChars);
    }

    [Fact]
    public void LinesWithinBudget_AlwaysRendersAtLeastOneLine()
    {
        RenderLimits.LinesWithinBudget([new string('x', 10_000_000)]).Should().Be(1);
    }

    [Fact]
    public void ClipLine_CutsAMinifiedLine_AndCountsWhatWasLeftOut()
    {
        var clipped = RenderLimits.ClipLine(new string('x', RenderLimits.MaxLineChars + 700), out var hidden);

        clipped.Should().HaveLength(RenderLimits.MaxLineChars);
        hidden.Should().Be(700);
        RenderLimits.ClipLine("short", out var none).Should().Be("short");
        none.Should().Be(0);
    }

    [Fact]
    public void CapMarkdown_HugeSource_IsCutAtALineBoundary()
    {
        var text = string.Concat(Enumerable.Repeat("A paragraph of markdown text that goes on a while.\n\n", 20_000));

        var capped = RenderLimits.CapMarkdown(text, out var wasCapped);

        wasCapped.Should().BeTrue();
        capped.Length.Should().BeLessThanOrEqualTo(RenderLimits.MaxMarkdownChars);
        text.Should().StartWith(capped);
        RenderLimits.CapMarkdown("# small", out var small).Should().Be("# small");
        small.Should().BeFalse();
    }

    // ── Offsets ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("4096", 1_000_000, true, 4096)]
    [InlineData("0x1000", 1_000_000, true, 4096)]
    [InlineData("50%", 1_000, true, 500)]
    [InlineData("1.5MB", 10_000_000, true, 1_572_864)]
    [InlineData("", 100, false, 0)]
    [InlineData("101%", 100, false, 0)]
    [InlineData("-5", 100, false, 0)]
    [InlineData("0xZZ", 100, false, 0)]
    public void OffsetParser_ReadsTheFormsTheFieldPromises(string text, long length, bool ok, long expected)
    {
        OffsetParser.TryParse(text, length, out var offset).Should().Be(ok);
        if (ok)
        {
            offset.Should().Be(expected);
        }
    }
}
