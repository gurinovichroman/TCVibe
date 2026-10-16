using System.IO;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.Settings;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Tests;

/// <summary>
/// The model timeout on the agent tab (6.55) — until then it could only be changed by hand in
/// settings.json, which is where the «did not answer in time» message sent the user.
/// </summary>
public class SettingsDialogLlmTimeoutTests
{
    private static void WithDialog(AppSettings settings, Action<SettingsDialogViewModel> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "tcv-set-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var serializer = new SettingsSerializer(
                NullLogger<SettingsSerializer>.Instance, Path.Combine(dir, "settings.json"));
            body(new SettingsDialogViewModel(settings, serializer));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Load_StoredTimeout_IsShown()
    {
        var settings = new AppSettings();
        settings.Agent.LlmTimeoutSeconds = 180;

        WithDialog(settings, vm => vm.LlmTimeoutSeconds.Should().Be(180));
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(5000, 600)]
    public void Load_HandEditedOutOfRange_ShowsWhatTheClientWaits(int stored, int shown)
    {
        var settings = new AppSettings();
        settings.Agent.LlmTimeoutSeconds = stored;

        WithDialog(settings, vm => vm.LlmTimeoutSeconds.Should().Be(shown));
    }

    [Fact]
    public void Save_ValidTimeout_IsWrittenToTheSettings()
    {
        var settings = new AppSettings();
        var closed = false;

        WithDialog(settings, vm =>
        {
            vm.CloseRequested += (_, _) => closed = true;
            vm.LlmTimeoutSeconds = 300;
            vm.SaveCommand.Execute(null);
        });

        settings.Agent.LlmTimeoutSeconds.Should().Be(300);
        closed.Should().BeTrue();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(601)]
    public void Save_OutOfRangeTimeout_StaysOpenWithAnError_AndKeepsTheStoredValue(int typed)
    {
        var settings = new AppSettings();
        settings.Agent.LlmTimeoutSeconds = 60;
        var closed = false;

        WithDialog(settings, vm =>
        {
            vm.CloseRequested += (_, _) => closed = true;
            vm.LlmTimeoutSeconds = typed;
            vm.SaveCommand.Execute(null);

            vm.SaveError.Should().NotBeEmpty("a silently changed number would leave the model giving up early");
        });

        closed.Should().BeFalse();
        settings.Agent.LlmTimeoutSeconds.Should().Be(60);
    }
}
