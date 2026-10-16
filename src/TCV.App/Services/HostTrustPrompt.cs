using TCV.App.Localization;
using TCV.Network;

namespace TCV.App.Services;

/// <summary>
/// Puts a server-identity question to the user: shows what the server claims (fingerprint,
/// key type or certificate subject, and why a certificate failed) and defaults to "No".
/// </summary>
public static class HostTrustPrompt
{
    public static Task<bool> AskAsync(IDialogService dialogs, HostTrustRequest request)
    {
        var ssh = request.Kind == KnownHostsStore.SshKind;
        var title = LocalizationManager.Get(ssh ? "Trust_SshTitle" : "Trust_TlsTitle");
        var message = string.Format(
            LocalizationManager.Get(ssh ? "Trust_SshMessage" : "Trust_TlsMessage"),
            request.Host, request.Algorithm, request.Fingerprint, request.Detail);

        return dialogs.ConfirmAsync(title, message);
    }
}
