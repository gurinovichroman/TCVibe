using System.IO;
using FluentAssertions;
using TCV.App.Services;
using TCV.Network;

namespace TCV.App.Tests;

/// <summary>Network failures reach the user in the UI language, whatever the library wrapped them in (TRAP-11).</summary>
public class NetworkErrorTextTests
{
    [Fact]
    public void Describe_HostKeyChangedInsideALibraryException_UsesTheDictionaryText()
    {
        var changed = new HostKeyChangedException("example.org:22", "SHA256:old", "SHA256:new");
        var wrapped = new AggregateException(new InvalidOperationException("handshake failed", changed));

        // Outside the app the dictionary is not loaded and Get returns "[key]".
        NetworkErrorText.Describe(wrapped).Should().Be("[Conn_HostKeyChanged]");
    }

    [Fact]
    public void Describe_UploadOntoAnExistingFile_NamesTheServerCase()
    {
        NetworkErrorText.Describe(new TransferTargetExistsException("/pub/a.txt", onServer: true))
            .Should().Be("[Transfer_ExistsOnServer]");
        NetworkErrorText.Describe(new TransferTargetExistsException(@"C:\a.txt", onServer: false))
            .Should().Be("[Transfer_ExistsLocal]");
    }

    [Fact]
    public void Describe_AnythingElse_KeepsItsOwnMessage()
    {
        NetworkErrorText.Describe(new IOException("disk full")).Should().Be("disk full");
    }
}
