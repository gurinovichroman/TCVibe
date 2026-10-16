using TCV.App.Localization;
using TCV.Network;

namespace TCV.App.Services;

/// <summary>
/// The user's-language text for a failure from the network plugin. The plugin cannot reach the
/// application's dictionaries, so its exceptions carry data and the wording is chosen here (TRAP-11).
/// </summary>
public static class NetworkErrorText
{
    /// <summary>What to show for <paramref name="ex"/>; anything unrecognised keeps its own message.</summary>
    public static string Describe(Exception ex)
    {
        // A changed server identity is the one error that must be read in full, never as "handshake failed".
        if (NetworkErrors.Find<HostKeyChangedException>(ex) is { } changed)
        {
            return string.Format(LocalizationManager.Get("Conn_HostKeyChanged"), changed.Host, changed.Expected, changed.Actual);
        }

        if (NetworkErrors.Find<TransferTargetExistsException>(ex) is { } exists)
        {
            return string.Format(
                LocalizationManager.Get(exists.OnServer ? "Transfer_ExistsOnServer" : "Transfer_ExistsLocal"), exists.Path);
        }

        if (NetworkErrors.Find<TransferFailedException>(ex) is { } failed)
        {
            return string.Format(LocalizationManager.Get("Transfer_FailedGeneric"), failed.Path);
        }

        return ex.Message;
    }
}
