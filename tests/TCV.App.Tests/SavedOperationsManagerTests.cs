using System.IO;
using System.Text.RegularExpressions;
using FluentAssertions;
using TCV.App.Operations;
using TCV.App.Services;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Tests;

/// <summary>
/// The saved-operations manager (6.50): adding, editing, renaming and deleting through the dialog,
/// written entry by entry and only on OK; and the list text that used to show the raw enum name.
/// </summary>
public class SavedOperationsManagerTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "tcv-savedops-mgr-" + Guid.NewGuid().ToString("N"));

    public SavedOperationsManagerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string FilePath => Path.Combine(_dir, "saved-operations.json");

    private SavedOperationStore Store() => new(FilePath);

    private SavedOperationStore StoreWith(params SavedOperation[] operations)
    {
        var store = Store();
        foreach (var operation in operations)
        {
            store.Save(operation);
        }

        return store;
    }

    private static SavedOperation Op(string name, SavedOperationKind kind = SavedOperationKind.Copy,
        string mask = "*.log", string target = @"D:\archive") =>
        new() { Name = name, Kind = kind, SourceMask = mask, Target = target, Confirm = true };

    private static SavedOperationItemViewModel Row(SavedOperationsDialogViewModel vm, string name) =>
        vm.Items.Single(i => i.Operation.Name == name);

    // ── The list text ─────────────────────────────────────────────────────────

    private static string Text(string key) => "<" + key + ">";

    [Fact]
    public void Describe_CopyOrMove_ShowsTheLocalisedKindTheMaskAndTheTarget()
    {
        SavedOperationText.Describe(Op("a", SavedOperationKind.Move), Text)
            .Should().Be(@"<SavedOp_Kind_Move> *.log → D:\archive");
    }

    [Fact]
    public void Describe_EmptyMaskAndTarget_SayTheSelectionAndTheOtherPanel()
    {
        SavedOperationText.Describe(Op("a", mask: "", target: " "), Text)
            .Should().Be("<SavedOp_Kind_Copy> <SavedOp_Selection> → <SavedOp_OtherPanel>");
    }

    [Fact]
    public void Describe_DeleteHasNoTarget_AndANewFolderTakesNoFiles()
    {
        SavedOperationText.Describe(Op("a", SavedOperationKind.Delete), Text)
            .Should().Be("<SavedOp_Kind_Delete> *.log");
        SavedOperationText.Describe(Op("a", SavedOperationKind.CreateDirectory, target: "%P\\out"), Text)
            .Should().Be(@"<SavedOp_Kind_CreateDirectory> → %P\out");
    }

    [Fact]
    public void KindLabels_ExistForEveryKind_InBothDictionaries()
    {
        // The label is looked up by the enum member's name (TRAP-11): a new kind without a key
        // would show "[SavedOp_Kind_…]" in the picker and the manager. (Any language, not just two.)
        foreach (var language in TCV.App.Localization.LocalizationManager.SupportedLanguages)
        {
            var keys = DictionaryKeys(language);
            Enum.GetValues<SavedOperationKind>()
                .Select(SavedOperationText.KindKey)
                .Where(k => !keys.Contains(k))
                .Should().BeEmpty($"Strings.{language}.xaml needs a label for every saved-operation kind");
        }
    }

    private static HashSet<string> DictionaryKeys(string language)
    {
        var file = Path.Combine(SourceTree.AppDirectory, "Localization", $"Strings.{language}.xaml");
        return Regex.Matches(File.ReadAllText(file), @"<sys:String x:Key=""([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .ToHashSet();
    }

    // ── Model helpers ─────────────────────────────────────────────────────────

    [Fact]
    public void Clone_IsDetached()
    {
        var original = Op("a");
        var copy = original.Clone();

        copy.Target = "elsewhere";

        original.Target.Should().Be(@"D:\archive");
    }

    [Fact]
    public void DefinesSameAs_IgnoresLastUse_ButNotACaseOnlyRename()
    {
        var original = Op("Build");
        var used = original.Clone();
        used.LastUsedUtc = DateTime.UtcNow;
        var renamed = original.Clone();
        renamed.Name = "build";

        original.DefinesSameAs(used).Should().BeTrue();
        original.DefinesSameAs(renamed).Should().BeFalse();
    }

    // ── The manager ───────────────────────────────────────────────────────────

    [Fact]
    public void Open_ListsTheStoredOperations_AndSelectsTheFirst()
    {
        var vm = new SavedOperationsDialogViewModel(StoreWith(Op("a"), Op("b")));

        vm.Items.Select(i => i.Operation.Name).Should().BeEquivalentTo(["a", "b"]);
        vm.SelectedItem.Should().NotBeNull();
        vm.EditName.Should().Be(vm.SelectedItem!.Operation.Name);
    }

    [Fact]
    public void Edit_ThenCancel_LeavesTheFileAlone()
    {
        var store = StoreWith(Op("a"));
        var vm = new SavedOperationsDialogViewModel(store);
        bool? closed = null;
        vm.CloseRequested += (_, saved) => closed = saved;

        vm.EditTarget = @"E:\other";
        vm.CancelCommand.Execute(null);

        closed.Should().BeFalse();
        store.LoadAll().Single().Target.Should().Be(@"D:\archive");
    }

    [Fact]
    public void Edit_ThenOk_WritesTheChange_AndKeepsWhenItWasLastUsed()
    {
        var used = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        var stored = Op("a");
        stored.LastUsedUtc = used;
        var store = StoreWith(stored);
        var vm = new SavedOperationsDialogViewModel(store);
        bool? closed = null;
        vm.CloseRequested += (_, saved) => closed = saved;

        vm.EditKind = SavedOperationKind.Move;
        vm.EditSourceMask = " *.tmp ";
        vm.SaveCommand.Execute(null);

        closed.Should().BeTrue();
        vm.Saved.Should().BeTrue();
        var written = store.LoadAll().Single();
        written.Kind.Should().Be(SavedOperationKind.Move);
        written.SourceMask.Should().Be("*.tmp");
        written.LastUsedUtc.Should().Be(used);
    }

    [Fact]
    public void Remove_ThenOk_DeletesItFromTheStore()
    {
        var store = StoreWith(Op("a"), Op("b"));
        var vm = new SavedOperationsDialogViewModel(store);

        vm.SelectedItem = Row(vm, "a");
        vm.RemoveCommand.Execute(null);
        vm.SaveCommand.Execute(null);

        vm.ErrorText.Should().BeEmpty();
        store.LoadAll().Select(o => o.Name).Should().Equal("b");
    }

    [Fact]
    public void Remove_SelectsTheNeighbour_AndIsDisabledWithNothingLeft()
    {
        var vm = new SavedOperationsDialogViewModel(StoreWith(Op("a")));

        vm.RemoveCommand.Execute(null);

        vm.SelectedItem.Should().BeNull();
        vm.RemoveCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void Rename_ThenOk_ReplacesTheOldName()
    {
        var store = StoreWith(Op("old"));
        var vm = new SavedOperationsDialogViewModel(store);

        vm.EditName = "new";
        vm.SaveCommand.Execute(null);

        store.LoadAll().Select(o => o.Name).Should().Equal("new");
    }

    [Fact]
    public void SwappingTwoNames_KeepsBothOperations()
    {
        var store = StoreWith(Op("a", target: "A"), Op("b", target: "B"));
        var vm = new SavedOperationsDialogViewModel(store);

        vm.SelectedItem = Row(vm, "a");
        vm.EditName = "tmp";
        vm.SelectedItem = Row(vm, "b");
        vm.EditName = "a";
        vm.SelectedItem = Row(vm, "tmp");
        vm.EditName = "b";
        vm.SaveCommand.Execute(null);

        var written = store.LoadAll().ToDictionary(o => o.Name, o => o.Target);
        written.Should().BeEquivalentTo(new Dictionary<string, string> { ["a"] = "B", ["b"] = "A" });
    }

    [Fact]
    public void Ok_KeepsAnOperationAnotherWindowSavedMeanwhile()
    {
        // Entry-by-entry writes, not a rewrite of the list read when the dialog opened.
        var store = StoreWith(Op("a"));
        var vm = new SavedOperationsDialogViewModel(store);
        Store().Save(Op("from the log"));

        vm.EditTarget = @"E:\x";
        vm.SaveCommand.Execute(null);

        store.LoadAll().Select(o => o.Name).Should().BeEquivalentTo(["a", "from the log"]);
    }

    [Fact]
    public void Ok_DeletingWhatAnotherWindowAlreadyRemoved_IsNotAnError()
    {
        var store = StoreWith(Op("a"), Op("b"));
        var vm = new SavedOperationsDialogViewModel(store);
        Store().Delete("a");

        vm.SelectedItem = Row(vm, "a");
        vm.RemoveCommand.Execute(null);
        vm.SaveCommand.Execute(null);

        vm.ErrorText.Should().BeEmpty();
        vm.Saved.Should().BeTrue();
        store.LoadAll().Select(o => o.Name).Should().Equal("b");
    }

    [Fact]
    public void Add_TwiceGivesDistinctNames_AndOkWritesBoth()
    {
        var store = Store();
        var vm = new SavedOperationsDialogViewModel(store);

        vm.AddCommand.Execute(null);
        vm.AddCommand.Execute(null);

        vm.Items.Select(i => i.Operation.Name).Should().OnlyHaveUniqueItems();
        vm.HasError.Should().BeFalse();
        vm.SaveCommand.Execute(null);
        store.LoadAll().Should().HaveCount(2);
    }

    [Fact]
    public void Add_StartsAsACopyToTheOtherPanel()
    {
        var vm = new SavedOperationsDialogViewModel(Store());

        vm.AddCommand.Execute(null);

        vm.EditKind.Should().Be(SavedOperationKind.Copy);
        vm.EditTarget.Should().Be("%O");
        vm.EditSourceMask.Should().BeEmpty();
    }

    [Fact]
    public void Draft_IsSelectedButNotWrittenUntilOk()
    {
        var store = Store();
        var draft = new SavedOperation { SourceMask = "*.cs", Target = "%O" };

        var vm = new SavedOperationsDialogViewModel(store, draft);

        vm.SelectedItem!.Original.Should().BeNull();
        vm.EditSourceMask.Should().Be("*.cs");
        vm.EditName.Should().NotBeNullOrWhiteSpace();
        store.LoadAll().Should().BeEmpty();

        vm.EditName = "Logs";
        vm.SaveCommand.Execute(null);
        store.LoadAll().Single().Name.Should().Be("Logs");
    }

    [Fact]
    public void Draft_NamedLikeAStoredOne_GetsAFreeName()
    {
        var vm = new SavedOperationsDialogViewModel(StoreWith(Op("Logs")), new SavedOperation { Name = "logs" });

        vm.HasError.Should().BeFalse();
        vm.Items.Select(i => i.Operation.Name.ToLowerInvariant()).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Ok_IsBlocked_ByAnEmptyName()
    {
        var store = StoreWith(Op("a"));
        var vm = new SavedOperationsDialogViewModel(store);
        bool? closed = null;
        vm.CloseRequested += (_, saved) => closed = saved;

        vm.EditName = "  ";
        vm.SaveCommand.Execute(null);

        vm.HasError.Should().BeTrue();
        closed.Should().BeNull();
        store.LoadAll().Single().Name.Should().Be("a");
    }

    [Fact]
    public void Ok_IsBlocked_ByTwoOperationsWithTheSameName_CaseInsensitively()
    {
        var store = StoreWith(Op("a"), Op("b"));
        var vm = new SavedOperationsDialogViewModel(store);

        vm.SelectedItem = Row(vm, "b");
        vm.EditName = "A";
        vm.SaveCommand.Execute(null);

        vm.HasError.Should().BeTrue();
        store.LoadAll().Select(o => o.Name).Should().BeEquivalentTo(["a", "b"]);
    }

    [Fact]
    public void Ok_IsBlocked_ByANewFolderWithoutAName()
    {
        // An empty target means the other panel's folder, which always exists: the operation
        // would do nothing.
        var vm = new SavedOperationsDialogViewModel(StoreWith(Op("a")));

        vm.EditKind = SavedOperationKind.CreateDirectory;
        vm.EditTarget = "";

        vm.HasError.Should().BeTrue();
    }

    [Theory]
    [InlineData(SavedOperationKind.Move)]
    [InlineData(SavedOperationKind.Delete)]
    public void DestructiveKind_IsAlwaysConfirmed(SavedOperationKind kind)
    {
        var store = StoreWith(new SavedOperation { Name = "a", Kind = SavedOperationKind.Copy, Target = "%O", Confirm = false });
        var vm = new SavedOperationsDialogViewModel(store);

        vm.EditKind = kind;

        vm.EditConfirm.Should().BeTrue();
        vm.ConfirmIsOptional.Should().BeFalse();
        vm.SaveCommand.Execute(null);
        store.LoadAll().Single().Confirm.Should().BeTrue();
    }

    [Theory]
    [InlineData(SavedOperationKind.Copy, true, true, true)]
    [InlineData(SavedOperationKind.Move, true, true, true)]
    [InlineData(SavedOperationKind.Delete, true, false, false)]
    [InlineData(SavedOperationKind.CreateDirectory, false, true, false)]
    public void Form_EnablesOnlyTheFieldsTheKindUses(
        SavedOperationKind kind, bool mask, bool target, bool createTarget)
    {
        var vm = new SavedOperationsDialogViewModel(StoreWith(Op("a")));

        vm.EditKind = kind;

        vm.UsesMask.Should().Be(mask);
        vm.UsesTarget.Should().Be(target);
        vm.UsesCreateTarget.Should().Be(createTarget);
    }

    [Fact]
    public void SwitchingTheSelection_KeepsWhatWasTyped()
    {
        var vm = new SavedOperationsDialogViewModel(StoreWith(Op("a"), Op("b")));

        vm.SelectedItem = Row(vm, "a");
        vm.EditTarget = "typed";
        vm.SelectedItem = Row(vm, "b");
        vm.SelectedItem = Row(vm, "a");

        vm.EditTarget.Should().Be("typed");
    }

    [Fact]
    public void Untouched_Operations_AreNotRewritten()
    {
        var store = StoreWith(Op("a"));
        var before = File.GetLastWriteTimeUtc(FilePath);
        var vm = new SavedOperationsDialogViewModel(store);
        File.SetLastWriteTimeUtc(FilePath, before.AddMinutes(-5));
        var stamped = File.GetLastWriteTimeUtc(FilePath);

        vm.SaveCommand.Execute(null);

        vm.Saved.Should().BeTrue();
        File.GetLastWriteTimeUtc(FilePath).Should().Be(stamped);
    }
}
