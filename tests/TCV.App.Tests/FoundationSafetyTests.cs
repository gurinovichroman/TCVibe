using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows.Input;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using TCV.App.Services;
using TCV.App.Settings;

namespace TCV.App.Tests;

public sealed class SettingsSerializerSafetyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tcv-settings-" + Guid.NewGuid().ToString("N")[..8]);
    private string SettingsFile => Path.Combine(_dir, "settings.json");

    public SettingsSerializerSafetyTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private SettingsSerializer Create(bool readOnly = false) =>
        new(NullLogger<SettingsSerializer>.Instance, SettingsFile) { ReadOnly = readOnly };

    [Fact]
    public void Load_NoFile_ReturnsDefaultsWithoutWarning()
    {
        var serializer = Create();

        serializer.Load().Theme.ThemeName.Should().Be("Dark");
        serializer.LoadWarning.Should().BeNull();
    }

    [Fact]
    public void Load_PartialOldFile_KeepsWhatItHasAndDefaultsTheRest()
    {
        File.WriteAllText(SettingsFile, "{\"Theme\":{\"ThemeName\":\"Light\"}}");

        var settings = Create().Load();

        settings.Theme.ThemeName.Should().Be("Light");
        settings.Operations.ConfirmDelete.Should().BeTrue();
    }

    [Fact]
    public void Load_NullSections_AreReplacedByDefaults()
    {
        File.WriteAllText(SettingsFile, "{\"LeftPanel\":null,\"Gadgets\":null,\"ExternalTools\":{\"Editors\":null}}");

        var settings = Create().Load();

        settings.LeftPanel.Should().NotBeNull();
        settings.Gadgets.Should().NotBeNull();
        settings.ExternalTools.Editors.Should().NotBeNull();
    }

    [Fact]
    public void Load_CorruptFile_ReturnsDefaultsWarnsAndKeepsACopy()
    {
        File.WriteAllText(SettingsFile, "{ this is not json");
        var serializer = Create();

        var settings = serializer.Load();

        settings.Should().NotBeNull();
        serializer.LoadWarning.Should().NotBeNullOrEmpty();
        Directory.GetFiles(_dir, "settings.json.corrupt-*").Should().HaveCount(1);
    }

    [Fact]
    public void Save_SecondSave_KeepsThePreviousVersionAsBackup()
    {
        var serializer = Create();
        var settings = new AppSettings();
        settings.Theme.ThemeName = "Dark";
        serializer.Save(settings).Should().BeTrue();
        settings.Theme.ThemeName = "Light";
        serializer.Save(settings).Should().BeTrue();

        File.ReadAllText(SettingsFile + ".bak").Should().Contain("Dark");
        File.ReadAllText(SettingsFile).Should().Contain("Light");
        File.Exists(SettingsFile + ".tmp").Should().BeFalse();
    }

    [Fact]
    public void Save_ReadOnlyGuest_WritesNothing()
    {
        var serializer = Create(readOnly: true);

        serializer.Save(new AppSettings()).Should().BeFalse();

        File.Exists(SettingsFile).Should().BeFalse();
    }

    [Fact]
    public void Save_LockedFileAtLoad_RefusesToOverwriteIt()
    {
        File.WriteAllText(SettingsFile, "{}");
        var serializer = Create();
        using (new FileStream(SettingsFile, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            serializer.Load();
        }

        serializer.LoadWarning.Should().NotBeNull();
        serializer.Save(new AppSettings()).Should().BeFalse();
        File.ReadAllText(SettingsFile).Should().Be("{}");
    }

    [Fact]
    public void Save_LlmApiKey_IsNotWrittenInTheClear()
    {
        var serializer = Create();
        var settings = new AppSettings();
        settings.Agent.LlmApiKey = "sk-very-secret-value";

        serializer.Save(settings);

        var json = File.ReadAllText(SettingsFile);
        json.Should().NotContain("sk-very-secret-value");
        json.Should().Contain("dpapi:");
        Create().Load().Agent.LlmApiKey.Should().Be("sk-very-secret-value");
    }

    [Fact]
    public void Load_LegacyPlaintextApiKey_StillLoads()
    {
        File.WriteAllText(SettingsFile, "{\"Agent\":{\"LlmApiKey\":\"sk-old-plain\"}}");

        Create().Load().Agent.LlmApiKey.Should().Be("sk-old-plain");
    }

    [Fact]
    public void Save_AfterLoadingALegacyPlaintextApiKey_WritesItProtected()
    {
        // Only the settings dialog used to encrypt, so an ordinary save kept the old key in the clear.
        File.WriteAllText(SettingsFile, "{\"Agent\":{\"LlmApiKey\":\"sk-old-plain\"}}");
        var settings = Create().Load();

        Create().Save(settings);

        File.ReadAllText(SettingsFile).Should().NotContain("sk-old-plain").And.Contain("dpapi:");
        Create().Load().Agent.LlmApiKey.Should().Be("sk-old-plain");
    }
}

public class SecretProtectorTests
{
    [Fact]
    public void Protect_RoundTrips()
    {
        var stored = SecretProtector.Protect("hunter2");

        stored.Should().NotContain("hunter2");
        SecretProtector.Unprotect(stored).Should().Be("hunter2");
    }

    [Fact]
    public void Protect_Empty_StaysEmpty()
    {
        SecretProtector.Protect("").Should().BeEmpty();
        SecretProtector.Unprotect(null).Should().BeEmpty();
    }

    [Fact]
    public void Unprotect_GarbageWithPrefix_IsEmptyAndFlaggedUnreadable()
    {
        SecretProtector.Unprotect("dpapi:AAAA").Should().BeEmpty();
        SecretProtector.IsUnreadable("dpapi:AAAA").Should().BeTrue();
        SecretProtector.IsUnreadable("plain").Should().BeFalse();
    }
}

public class GestureTextTests
{
    [Fact]
    public void Format_ModifiersAndKey_ReadsLikeTheKeyboard()
    {
        GestureText.Format(new KeyStroke(Key.C, ModifierKeys.Control | ModifierKeys.Shift)).Should().Be("Ctrl+Shift+C");
    }

    [Theory]
    [InlineData(Key.Return, "Enter")]
    [InlineData(Key.Delete, "Del")]
    [InlineData(Key.Prior, "PgUp")]
    [InlineData(Key.OemQuestion, "/")]
    [InlineData(Key.Left, "←")]
    [InlineData(Key.Down, "↓")]
    public void Format_WpfKeyNames_AreTranslated(Key key, string expected)
    {
        GestureText.Format(new KeyStroke(key, ModifierKeys.None)).Should().Be(expected);
    }

    [Fact]
    public void Format_NullBinding_IsEmpty()
    {
        GestureText.Format((HotkeyBinding?)null).Should().BeEmpty();
    }
}

public class ThemeManagerTests
{
    [Fact]
    public void Apply_UnknownTheme_FallsBackToDark()
    {
        var manager = new ThemeManager(new AppSettings());

        manager.Apply("Nonexistent");

        manager.CurrentTheme.Should().Be("Dark");
    }

    [Fact]
    public void ResolveThemeName_UnknownSavedName_IsDark()
    {
        var settings = new AppSettings();
        settings.Theme.UseSystemTheme = false;
        settings.Theme.ThemeName = "Solarized";

        new ThemeManager(settings).ResolveThemeName().Should().Be("Dark");
    }

    [Fact]
    public void ResolveThemeName_SavedLight_IsLight()
    {
        var settings = new AppSettings();
        settings.Theme.UseSystemTheme = false;
        settings.Theme.ThemeName = "Light";

        new ThemeManager(settings).ResolveThemeName().Should().Be("Light");
    }
}

[Collection("TemporaryFiles")]
public sealed class TemporaryFilesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "tcv-tmp-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _previous = TemporaryFiles.Root;

    public TemporaryFilesTests() => TemporaryFiles.Root = _root;

    public void Dispose()
    {
        TemporaryFiles.Root = _previous;
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void PathFor_SameNameTwice_GivesDifferentFolders()
    {
        var a = TemporaryFiles.PathFor("readme.txt");
        var b = TemporaryFiles.PathFor("readme.txt");

        a.Should().NotBe(b);
        Path.GetFileName(a).Should().Be("readme.txt");
        Directory.Exists(Path.GetDirectoryName(a)).Should().BeTrue();
    }

    [Fact]
    public void PathFor_InvalidCharacters_AreReplaced()
    {
        var path = TemporaryFiles.PathFor("a<b>:c.txt");

        Path.GetFileName(path).Should().NotContainAny("<", ">", ":");
    }

    [Fact]
    public void DeleteCreatedThisRun_RemovesTheFolders()
    {
        var path = TemporaryFiles.PathFor("x.bin");
        File.WriteAllText(path, "data");

        TemporaryFiles.DeleteCreatedThisRun();

        Directory.Exists(Path.GetDirectoryName(path)).Should().BeFalse();
    }

    [Fact]
    public void Holds_ACopyMadeHere_IsTrue()
    {
        // An archive opened from inside another one, or from a server, is such a copy: panel:// must
        // not check it against the approved directories as if it were the user's file (ADR-0023).
        TemporaryFiles.Holds(TemporaryFiles.PathFor("inner.zip")).Should().BeTrue();
    }

    [Fact]
    public void Holds_AFileOutsideTheRoot_IsFalse()
    {
        TemporaryFiles.Holds(Path.Combine(Path.GetTempPath(), "dist.zip")).Should().BeFalse();
        TemporaryFiles.Holds(_root + "-sibling\\dist.zip").Should().BeFalse();   // a prefix is not a parent
    }

    [Fact]
    public void SweepOlderThan_RemovesOldFoldersAndKeepsFreshOnes()
    {
        var old = Path.Combine(_root, "old");
        var fresh = Path.Combine(_root, "fresh");
        Directory.CreateDirectory(old);
        Directory.CreateDirectory(fresh);
        Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-3));

        TemporaryFiles.SweepOlderThan(TimeSpan.FromDays(1)).Should().Be(1);

        Directory.Exists(old).Should().BeFalse();
        Directory.Exists(fresh).Should().BeTrue();
    }
}

public class UpdateServiceTests
{
    private sealed class Stub(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond());
    }

    private static UpdateService Service(Func<HttpResponseMessage> respond) =>
        new(NullLogger<UpdateService>.Instance, "owner/repo", new Stub(respond));

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task CheckAsync_ServerError_IsFailedNotUpToDate()
    {
        using var service = Service(() => new HttpResponseMessage(HttpStatusCode.Forbidden));

        var result = await service.CheckAsync();

        result.Outcome.Should().Be(UpdateCheckOutcome.Failed);
        result.Error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task CheckAsync_NotFound_MeansNoPublishedRelease_NotABrokenConnection()
    {
        using var service = Service(() => new HttpResponseMessage(HttpStatusCode.NotFound));

        (await service.CheckAsync()).Outcome.Should().Be(UpdateCheckOutcome.NoReleases);
    }

    [Fact]
    public async Task CheckAsync_GarbageBody_IsFailed()
    {
        using var service = Service(() => Json("not json"));

        (await service.CheckAsync()).Outcome.Should().Be(UpdateCheckOutcome.Failed);
    }

    [Fact]
    public async Task CheckAsync_OlderTag_IsUpToDate()
    {
        using var service = Service(() => Json("{\"tag_name\":\"v0.0.1\"}"));

        (await service.CheckAsync()).Outcome.Should().Be(UpdateCheckOutcome.UpToDate);
    }

    [Fact]
    public async Task CheckAsync_NewerPreReleaseTag_IsAvailableWithPortableAsset()
    {
        var body = JsonSerializer.Serialize(new
        {
            tag_name = "v99.0.0-beta.1",
            body = "notes",
            assets = new[] { new { name = "TCV-portable.zip", browser_download_url = "https://example.com/tcv.zip" } },
        });
        using var service = Service(() => Json(body));

        var result = await service.CheckAsync();

        result.Outcome.Should().Be(UpdateCheckOutcome.Available);
        result.Update!.LatestVersion.Should().Be(new Version(99, 0, 0));
        result.Update.DownloadUrl.Should().Be("https://example.com/tcv.zip");
    }

    [Theory]
    [InlineData("v1.2.3", "1.2.3")]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("V1.2.3+build5", "1.2.3")]
    public void TryParseTag_CommonForms_Parse(string tag, string expected)
    {
        UpdateService.TryParseTag(tag, out var version).Should().BeTrue();
        version.Should().Be(Version.Parse(expected));
    }

    [Fact]
    public void TryParseTag_Nonsense_Fails()
    {
        UpdateService.TryParseTag("nightly", out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("https://github.com/x/y/releases", true)]
    [InlineData("http://example.com", true)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("ms-msdt:/id", false)]
    [InlineData("C:\\Windows\\notepad.exe", false)]
    [InlineData(null, false)]
    public void IsSafeDownloadUrl_OnlyHttpAndHttps(string? url, bool expected)
    {
        UpdateService.IsSafeDownloadUrl(url).Should().Be(expected);
    }

    [Theory]
    [InlineData("gurinovichroman/TCVibe", true)]
    [InlineData("owner/repo.name", true)]
    [InlineData(@"D:\feeds\tcv", false)]
    [InlineData("https://example.com/tcv", false)]
    [InlineData("owner/repo/extra", false)]
    [InlineData("repo", false)]
    public void IsGitHubRepository_OwnerSlashName_AnythingElseIsAFeed(string repository, bool expected)
    {
        UpdateService.IsGitHubRepository(repository).Should().Be(expected);
    }

    [Fact]
    public void CanInstall_OutsideThePortableLayout_IsFalse()
    {
        using var service = Service(() => new HttpResponseMessage(HttpStatusCode.NotFound));

        service.CanInstall.Should().BeFalse("a dev build or an old ZIP offers the download page");
    }

    [Fact]
    public async Task CheckAsync_FeedOutsideThePortableLayout_IsFailedWithoutGoingOnline()
    {
        var asked = false;
        using var service = new UpdateService(NullLogger<UpdateService>.Instance, @"D:\feeds\tcv",
            new Stub(() => { asked = true; return new HttpResponseMessage(HttpStatusCode.OK); }));

        var result = await service.CheckAsync();

        result.Outcome.Should().Be(UpdateCheckOutcome.Failed);
        asked.Should().BeFalse();
    }

    [Fact]
    public async Task CheckAsync_VelopackPortableAssetName_IsFoundRegardlessOfCase()
    {
        var body = JsonSerializer.Serialize(new
        {
            tag_name = "v99.0.0",
            assets = new[] { new { name = "TCVibe-win-x64-Portable.zip", browser_download_url = "https://example.com/p.zip" } },
        });
        using var service = Service(() => Json(body));

        (await service.CheckAsync()).Update!.DownloadUrl.Should().Be("https://example.com/p.zip");
    }

    [Fact]
    public async Task CheckAsync_PortableZipPerArchitecture_OffersTheOneThisCopyRunsAs()
    {
        var own = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        var other = own.Contains("x86", StringComparison.OrdinalIgnoreCase) ? "win-x64" : "win-x86";
        var body = JsonSerializer.Serialize(new
        {
            tag_name = "v99.0.0",
            assets = new[]
            {
                new { name = $"TCVibe-{other}-Portable.zip", browser_download_url = "https://example.com/other.zip" },
                new { name = $"TCVibe-{own}-Portable.zip", browser_download_url = "https://example.com/own.zip" },
            },
        });
        using var service = Service(() => Json(body));

        (await service.CheckAsync()).Update!.DownloadUrl.Should().Be("https://example.com/own.zip");
    }
}
