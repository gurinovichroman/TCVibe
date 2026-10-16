using TCV.Core.Domain;

namespace TCV.Network;

/// <summary>
/// A store that can sign a link anyone can open to read one file for a while, without credentials of
/// their own: S3 (a presigned URL) and Azure Blob (a service SAS). Nothing is sent to make one.
/// </summary>
public interface IReadLinkSource
{
    /// <summary>False when the connection has nothing to sign with: S3 without keys, Azure with a SAS token.</summary>
    bool CanCreateReadLink { get; }

    /// <summary>
    /// The link to the file at <paramref name="path"/>, readable for <paramref name="lifetime"/> (seven days
    /// at most). Put it on the clipboard as <see cref="Uri.AbsoluteUri"/>: <see cref="Uri.ToString"/>
    /// un-escapes the signature.
    /// </summary>
    Uri CreateReadLink(VfsPath path, TimeSpan lifetime);
}
