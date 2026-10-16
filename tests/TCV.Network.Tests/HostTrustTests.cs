using System.Security.Cryptography;
using FluentAssertions;
using TCV.Network;

namespace TCV.Network.Tests;

public sealed class HostTrustTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "tcv-trust-" + Guid.NewGuid().ToString("N"));
    private readonly byte[] _keyA = [1, 2, 3, 4, 5, 6, 7, 8];
    private readonly byte[] _keyB = [9, 9, 9, 9, 9, 9, 9, 9];

    public HostTrustTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string StorePath => Path.Combine(_dir, "known-hosts.json");

    private (HostTrustPolicy Policy, List<HostTrustRequest> Asked) Policy(bool answer)
    {
        var asked = new List<HostTrustRequest>();
        var policy = new HostTrustPolicy(new KnownHostsStore(StorePath), request =>
        {
            asked.Add(request);
            return Task.FromResult(answer);
        });
        return (policy, asked);
    }

    // ── SSH host keys ────────────────────────────────────────────────────────

    [Fact]
    public void VerifySshHostKey_FirstContact_AsksAndRemembersAnApproval()
    {
        var (policy, asked) = Policy(answer: true);

        var first = policy.VerifySshHostKey("Example.COM", 22, "ssh-ed25519", _keyA);
        var second = policy.VerifySshHostKey("example.com", 22, "ssh-ed25519", _keyA);

        first.Should().BeTrue();
        second.Should().BeTrue();
        asked.Should().ContainSingle("the second connection is recognised, not asked about again");
        asked[0].Host.Should().Be("example.com:22");
    }

    [Fact]
    public void VerifySshHostKey_UserRefuses_IsRejectedAndNothingIsStored()
    {
        var (policy, _) = Policy(answer: false);

        policy.VerifySshHostKey("h", 22, "ssh-rsa", _keyA).Should().BeFalse();

        new KnownHostsStore(StorePath).Hosts.Should().BeEmpty();
    }

    [Fact]
    public void VerifySshHostKey_ChangedKey_ThrowsWithoutAskingAnyone()
    {
        var (policy, asked) = Policy(answer: true);
        policy.VerifySshHostKey("h", 22, "ssh-ed25519", _keyA);
        asked.Clear();

        var act = () => policy.VerifySshHostKey("h", 22, "ssh-ed25519", _keyB);

        act.Should().Throw<HostKeyChangedException>().Which.Message.Should().Contain("CHANGED");
        asked.Should().BeEmpty("a one-click 'trust anyway' would make the check pointless");
    }

    [Fact]
    public void VerifySshHostKey_SameHostOtherPort_IsADifferentServer()
    {
        var (policy, asked) = Policy(answer: true);

        policy.VerifySshHostKey("h", 22, "ssh-ed25519", _keyA);
        policy.VerifySshHostKey("h", 2222, "ssh-ed25519", _keyB);

        asked.Should().HaveCount(2);
    }

    [Fact]
    public void FingerprintOf_IsTheOpenSshSha256Format()
    {
        var expected = "SHA256:" + Convert.ToBase64String(SHA256.HashData(_keyA)).TrimEnd('=');

        HostTrustPolicy.FingerprintOf(_keyA).Should().Be(expected);
        expected.Should().NotEndWith("=");
    }

    // ── TLS certificates ─────────────────────────────────────────────────────

    [Fact]
    public void VerifyCertificate_ChainTheSystemTrusts_IsAcceptedWithoutAsking()
    {
        var (policy, asked) = Policy(answer: false);

        policy.VerifyCertificate("h", 21, "CN=h", SHA256.HashData(_keyA), chainIsValid: true, "").Should().BeTrue();

        asked.Should().BeEmpty();
    }

    [Fact]
    public void VerifyCertificate_SelfSigned_IsPinnedOnApprovalAndAcceptedNextTime()
    {
        var (policy, asked) = Policy(answer: true);
        var hash = SHA256.HashData(_keyA);

        policy.VerifyCertificate("h", 21, "CN=h", hash, false, "RemoteCertificateChainErrors").Should().BeTrue();
        policy.VerifyCertificate("h", 21, "CN=h", hash, false, "RemoteCertificateChainErrors").Should().BeTrue();

        asked.Should().ContainSingle().Which.Detail.Should().Contain("ChainErrors");
    }

    [Fact]
    public void VerifyCertificate_PinnedCertificateReplacedByAnotherUntrustedOne_Throws()
    {
        var (policy, _) = Policy(answer: true);
        policy.VerifyCertificate("h", 21, "CN=h", SHA256.HashData(_keyA), false, "x");

        var act = () => policy.VerifyCertificate("h", 21, "CN=h", SHA256.HashData(_keyB), false, "x");

        act.Should().Throw<HostKeyChangedException>();
    }

    [Fact]
    public void VerifyCertificate_UserRefuses_IsRejected()
    {
        var (policy, _) = Policy(answer: false);

        policy.VerifyCertificate("h", 21, "CN=h", SHA256.HashData(_keyA), false, "x").Should().BeFalse();
    }

    // ── The store ────────────────────────────────────────────────────────────

    [Fact]
    public void Trust_PersistsAcrossInstances()
    {
        new KnownHostsStore(StorePath).Trust(KnownHostsStore.SshKind, "h:22", "ssh-rsa", "SHA256:abc");

        new KnownHostsStore(StorePath).Check(KnownHostsStore.SshKind, "h:22", "SHA256:abc")
            .Should().Be(HostTrustState.Trusted);
    }

    [Fact]
    public void Forget_MakesTheServerUnknownAgain()
    {
        var store = new KnownHostsStore(StorePath);
        store.Trust(KnownHostsStore.SshKind, "h:22", "ssh-rsa", "SHA256:abc");

        store.Forget(KnownHostsStore.SshKind, "h:22");

        store.Check(KnownHostsStore.SshKind, "h:22", "SHA256:abc").Should().Be(HostTrustState.Unknown);
    }

    [Fact]
    public void Load_CorruptFile_IsKeptAsACopyAndReported()
    {
        File.WriteAllText(StorePath, "{ not json");

        var store = new KnownHostsStore(StorePath);

        store.Hosts.Should().BeEmpty();
        store.LoadError.Should().NotBeNullOrEmpty();
        Directory.GetFiles(_dir, "known-hosts.json.corrupt-*").Should().ContainSingle();
    }

    [Fact]
    public void Save_KeepsThePreviousVersionAsBak()
    {
        var store = new KnownHostsStore(StorePath);
        store.Trust(KnownHostsStore.SshKind, "a:22", "x", "SHA256:1");
        store.Trust(KnownHostsStore.SshKind, "b:22", "x", "SHA256:2");

        File.Exists(StorePath + ".bak").Should().BeTrue();
        File.ReadAllText(StorePath + ".bak").Should().Contain("a:22").And.NotContain("b:22");
    }

    [Fact]
    public void NetworkErrors_Find_WalksNestedExceptions()
    {
        var inner = new HostKeyChangedException("h", "a", "b");
        var wrapped = new InvalidOperationException("outer", new AggregateException(new IOException("mid", inner)));

        NetworkErrors.Find<HostKeyChangedException>(wrapped).Should().BeSameAs(inner);
        NetworkErrors.Find<TimeoutException>(wrapped).Should().BeNull();
    }
}
