using FluentAssertions;
using TCV.App.ViewModels.Dialogs;

namespace TCV.App.Tests;

/// <summary>What the plugin manager copies into plugins/ along with a plugin it loaded.</summary>
public sealed class PluginCompanionFilesTests
{
    [Fact]
    public void CompanionFileNames_NoManifest_OnlyTheDll()
    {
        PluginManagerDialogViewModel.CompanionFileNames("My.Plugin.dll", null)
            .Should().Equal("My.Plugin.dll");
    }

    [Fact]
    public void CompanionFileNames_Manifest_AddsItAndItsRuntimeAssembliesOnly()
    {
        const string deps = """
            {
              "targets": {
                ".NETCoreApp,Version=v10.0": {
                  "My.Plugin/1.0.0": { "runtime": { "My.Plugin.dll": {} } },
                  "Newtonsoft.Json/13.0.3": { "runtime": { "lib/net6.0/Newtonsoft.Json.dll": { "assemblyVersion": "13.0.0.0" } } },
                  "Analyzer/1.0.0": { "compile": { "lib/Analyzer.dll": {} } }
                }
              }
            }
            """;

        PluginManagerDialogViewModel.CompanionFileNames("My.Plugin.dll", deps)
            .Should().BeEquivalentTo(["My.Plugin.dll", "My.Plugin.deps.json", "Newtonsoft.Json.dll"]);
    }

    [Fact]
    public void CompanionFileNames_BrokenManifest_KeepsTheDllAndTheManifest()
    {
        PluginManagerDialogViewModel.CompanionFileNames("P.dll", "{ not json")
            .Should().Equal("P.dll", "P.deps.json");
    }
}
