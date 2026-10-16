using FluentAssertions;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Tests;

public class InputDialogViewModelTests
{
    [Fact]
    public void Confirm_WithABlankValue_IsUnavailable_WhenAnAnswerIsRequired()
    {
        var vm = new InputDialogViewModel("t", "p", initial: "");

        vm.ConfirmCommand.CanExecute(null).Should().BeFalse();

        vm.Value = "   ";
        vm.ConfirmCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void Confirm_BecomesAvailable_AsSoonAsThereIsText()
    {
        var vm = new InputDialogViewModel("t", "p");
        var changed = 0;
        vm.ConfirmCommand.CanExecuteChanged += (_, _) => changed++;

        vm.Value = "name";

        vm.ConfirmCommand.CanExecute(null).Should().BeTrue();
        changed.Should().BeGreaterThan(0, "the OK button has to notice the text");
    }

    [Fact]
    public void Confirm_WithAllowEmpty_AcceptsABlankValue_SoAPromptCanClearAField()
    {
        // "Empty to clear" prompts (workspace icon, colour, group) depend on this.
        var vm = new InputDialogViewModel("t", "p", initial: "#FF0000", allowEmpty: true);
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        vm.Value = "";
        vm.ConfirmCommand.CanExecute(null).Should().BeTrue();
        vm.ConfirmCommand.Execute(null);

        closed.Should().BeTrue();
        vm.DialogResult.Should().BeTrue();
        vm.Value.Should().BeEmpty();
    }

    [Fact]
    public void Cancel_ClosesWithoutAResult()
    {
        var vm = new InputDialogViewModel("t", "p", initial: "x");
        var closed = false;
        vm.CloseRequested += (_, _) => closed = true;

        vm.CancelCommand.Execute(null);

        closed.Should().BeTrue();
        vm.DialogResult.Should().BeFalse();
    }

    [Fact]
    public void InitialValue_IsShown()
    {
        new InputDialogViewModel("t", "p", initial: "seed").Value.Should().Be("seed");
    }
}
