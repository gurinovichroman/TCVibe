using System.Text.RegularExpressions;

namespace TCV.Network;

/// <summary>
/// An address typed or pasted into the path bar as a connection: <c>sftp://deploy@host:2222/var/log</c>,
/// <c>s3://bucket/prefix</c>, a bucket's or a container's https address. Pure parsing; the application
/// then looks for a saved connection to the same place and borrows its credentials.
/// </summary>
public static partial class ConnectionUrl
{
    /// <summary>
    /// Reads <paramref name="text"/> as a connection address. <paramref name="path"/> is where to open
    /// it, in the connection's own terms (with no fixed bucket an S3 path starts with the bucket).
    /// False for anything else — a disk or UNC path, a plain web page.
    /// </summary>
    public static bool TryParse(string text, out ConnectionInfo info, out string path)
    {
        info = new ConnectionInfo();
        path = "/";
        text = text.Trim().Trim('"');
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.IsUnc || uri.IsFile)
        {
            return false;
        }

        var scheme = uri.Scheme.ToLowerInvariant();
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString).ToList();
        var (user, password) = UserInfo(uri);

        if (scheme is "https" or "http")
        {
            return TryParseHttps(uri, segments, user, password, out info, out path);
        }

        NetworkProtocol? protocol = scheme switch
        {
            "ftp" => NetworkProtocol.Ftp,
            "ftps" => uri.Port == ConnectionInfo.ImplicitFtpsPort ? NetworkProtocol.FtpsImplicit : NetworkProtocol.Ftps,
            "sftp" or "ssh" or "scp" => NetworkProtocol.Sftp,
            "dav" or "webdav" => NetworkProtocol.WebDav,
            "davs" or "webdavs" => NetworkProtocol.WebDavs,
            "s3" => NetworkProtocol.S3,
            "azblob" => NetworkProtocol.AzureBlob,
            "gdrive" => NetworkProtocol.GoogleDrive,
            "dropbox" => NetworkProtocol.Dropbox,
            "onedrive" => NetworkProtocol.OneDrive,
            "smb" => NetworkProtocol.Smb,
            _ => null,
        };
        if (protocol is not { } p)
        {
            return false;
        }

        info.Protocol = p;

        // ConnectionInfo.Port starts at FTP's 21. An address that names no port (s3://, azblob://, a drive)
        // must not hand that on: the editor copies a parsed port into the fields, and S3 would be sent to :21.
        info.Port = info.DefaultPort();
        info.Username = user;
        info.Password = password;
        switch (p)
        {
            case NetworkProtocol.S3:
                // s3://bucket/key: the "host" is the bucket, the endpoint comes from a saved connection.
                info.Bucket = uri.Host;
                info.Name = uri.Host;
                path = Join(segments);
                return uri.Host.Length > 0;
            case NetworkProtocol.AzureBlob:
                info.Username = uri.Host;
                info.Name = uri.Host;
                if (segments.Count > 0)
                {
                    info.Bucket = segments[0];
                    segments.RemoveAt(0);
                }

                path = Join(segments);
                return uri.Host.Length > 0;
            case NetworkProtocol.GoogleDrive or NetworkProtocol.Dropbox or NetworkProtocol.OneDrive:
                // gdrive://me@example.com/docs — the account is an e-mail address, which a URI splits at the
                // "@" into user and host; DescribeAddress writes it that way, so it is put back together here.
                info.Username = user.Length > 0 && uri.Host.Length > 0 ? $"{user}@{uri.Host}" : user;
                path = Join(segments);
                return true;
        }

        if (uri.Host.Length == 0)
        {
            return false;
        }

        info.Host = uri.Host;
        info.Name = uri.Host;
        info.Port = uri.IsDefaultPort || uri.Port <= 0 ? info.DefaultPort() : uri.Port;
        path = Join(segments);
        info.RemotePath = path;
        return true;
    }

    /// <summary>
    /// Reads an http(s) address as the endpoint of an S3-compatible store that has no host pattern of its
    /// own (Contabo, MinIO, Ceph…): the host is the endpoint, the first path segment the bucket. Only for a
    /// place that already knows it is filling an S3 connection (the connection editor): <see cref="TryParse"/>
    /// must keep refusing a plain web page, so the path bar does not take it for a store.
    /// </summary>
    public static bool TryParseS3Endpoint(string text, out ConnectionInfo info, out string path)
    {
        info = new ConnectionInfo();
        path = "/";
        text = text.Trim().Trim('"');
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0)
        {
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.UnescapeDataString).ToList();
        var (user, password) = UserInfo(uri);

        info.Protocol = NetworkProtocol.S3;
        info.Username = user;
        info.Password = password;
        info.Host = uri.Host.ToLowerInvariant();
        info.Name = info.Host;
        info.UseHttp = uri.Scheme == "http";
        info.Port = uri.Port;
        if (segments.Count > 0)
        {
            // The address puts the bucket in the path, so the endpoint is addressed path-style.
            info.Bucket = segments[0];
            info.PathStyle = true;
            segments.RemoveAt(0);
        }

        path = Join(segments);
        return true;
    }

    /// <summary>
    /// An https address of a bucket or a container: <c>https://bucket.s3.eu-west-1.amazonaws.com/key</c>,
    /// <c>https://s3.eu-west-1.amazonaws.com/bucket/key</c>, <c>https://acct.blob.core.windows.net/c/x</c>.
    /// Any other web address is not a connection.
    /// </summary>
    private static bool TryParseHttps(Uri uri, List<string> segments, string user, string? password,
        out ConnectionInfo info, out string path)
    {
        info = new ConnectionInfo { Username = user, Password = password };
        path = "/";
        var host = uri.Host.ToLowerInvariant();

        if (AzureHost().Match(host) is { Success: true } azure)
        {
            info.Protocol = NetworkProtocol.AzureBlob;
            info.Port = info.DefaultPort();
            info.Username = azure.Groups["account"].Value;
            info.Name = info.Username;
            info.Host = host;
            if (segments.Count > 0)
            {
                info.Bucket = segments[0];
                segments.RemoveAt(0);
            }

            path = Join(segments);
            return true;
        }

        if (AwsHost().Match(host) is { Success: true } aws)
        {
            info.Protocol = NetworkProtocol.S3;
            info.Port = info.DefaultPort();
            var region = aws.Groups["region"].Success ? aws.Groups["region"].Value : "us-east-1";
            info.Region = region;
            info.Host = region == "us-east-1" ? "s3.amazonaws.com" : $"s3.{region}.amazonaws.com";
            if (aws.Groups["bucket"].Success)
            {
                info.Bucket = aws.Groups["bucket"].Value;
            }
            else if (segments.Count > 0)
            {
                info.Bucket = segments[0];
                segments.RemoveAt(0);
            }
            else
            {
                return false;
            }

            info.Name = info.Bucket!;
            path = Join(segments);
            return true;
        }

        return false;
    }

    /// <summary>
    /// The saved connection that reaches the same place as <paramref name="parsed"/>, so a typed address
    /// opens with the saved credentials and settings; null when none does. With a match, the path is
    /// rewritten into that connection's terms (a bucket-wide S3 connection wants the bucket in it).
    /// </summary>
    public static ConnectionInfo? FindSaved(IEnumerable<ConnectionInfo> saved, ConnectionInfo parsed, ref string path)
    {
        foreach (var candidate in saved)
        {
            if (!SameFamily(candidate.Protocol, parsed.Protocol))
            {
                continue;
            }

            switch (parsed.Protocol)
            {
                case NetworkProtocol.S3:
                    if (SameText(candidate.Bucket, parsed.Bucket))
                    {
                        return candidate;
                    }

                    // A connection to the whole account reaches the bucket too, if it is the same endpoint.
                    if (string.IsNullOrWhiteSpace(candidate.Bucket)
                        && (string.IsNullOrEmpty(parsed.Host) || SameText(candidate.Host, parsed.Host)))
                    {
                        path = "/" + parsed.Bucket + (path == "/" ? string.Empty : path);
                        return candidate;
                    }

                    continue;
                case NetworkProtocol.AzureBlob:
                    if (!SameText(candidate.Username, parsed.Username))
                    {
                        continue;
                    }

                    if (SameText(candidate.Bucket, parsed.Bucket))
                    {
                        return candidate;
                    }

                    if (string.IsNullOrWhiteSpace(candidate.Bucket) && !string.IsNullOrEmpty(parsed.Bucket))
                    {
                        path = "/" + parsed.Bucket + (path == "/" ? string.Empty : path);
                        return candidate;
                    }

                    continue;
                case NetworkProtocol.GoogleDrive or NetworkProtocol.Dropbox or NetworkProtocol.OneDrive:
                    if (string.IsNullOrEmpty(parsed.Username) || SameText(candidate.Username, parsed.Username))
                    {
                        return candidate;
                    }

                    continue;
            }

            if (SameText(candidate.Host, parsed.Host)
                && (candidate.Port == parsed.Port || parsed.Port <= 0)
                && (string.IsNullOrEmpty(parsed.Username) || SameText(candidate.Username, parsed.Username)))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool SameFamily(NetworkProtocol a, NetworkProtocol b) =>
        a == b || (IsFtps(a) && IsFtps(b));

    private static bool IsFtps(NetworkProtocol p) => p is NetworkProtocol.Ftps or NetworkProtocol.FtpsImplicit;

    private static bool SameText(string? a, string? b) =>
        string.Equals(a ?? string.Empty, b ?? string.Empty, StringComparison.OrdinalIgnoreCase);

    private static string Join(List<string> segments) =>
        segments.Count == 0 ? "/" : "/" + string.Join('/', segments);

    private static (string User, string? Password) UserInfo(Uri uri)
    {
        if (uri.UserInfo.Length == 0)
        {
            return (string.Empty, null);
        }

        var colon = uri.UserInfo.IndexOf(':');
        return colon < 0
            ? (Uri.UnescapeDataString(uri.UserInfo), null)
            : (Uri.UnescapeDataString(uri.UserInfo[..colon]), Uri.UnescapeDataString(uri.UserInfo[(colon + 1)..]));
    }

    [GeneratedRegex(@"^(?<account>[a-z0-9]{3,24})\.blob\.core\.(windows\.net|chinacloudapi\.cn|usgovcloudapi\.net)$")]
    private static partial Regex AzureHost();

    // bucket.s3.region.amazonaws.com, bucket.s3.amazonaws.com, s3.region.amazonaws.com, s3-region.amazonaws.com
    [GeneratedRegex(@"^((?<bucket>[a-z0-9][a-z0-9.\-]{1,61}[a-z0-9])\.)?s3([.\-](?<region>[a-z0-9\-]+))?\.amazonaws\.com$")]
    private static partial Regex AwsHost();
}
