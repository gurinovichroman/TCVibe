namespace TCV.Network;

/// <summary>
/// An S3-compatible provider as the connection editor offers it: how its endpoint host is formed and
/// what its region, addressing and port are by default. Picking one fills the connection's fields;
/// the connection itself stores only the fields (and the preset's <see cref="Id"/> in
/// <see cref="ConnectionInfo.Provider"/>, to show the same choice again).
/// </summary>
/// <param name="HostTemplate">The endpoint host; <c>{region}</c> and <c>{account}</c> are filled by <see cref="BuildHost"/>.</param>
/// <param name="DefaultRegion">The signing region the provider expects when the user names none.</param>
/// <param name="Hint">One line on where the keys and the region are found; null for none.</param>
public sealed record S3Preset(
    string Id, string Name, string HostTemplate, string DefaultRegion,
    bool PathStyle, bool UseHttp, int Port, string? Hint)
{
    /// <summary>The host needs the account id (Cloudflare R2).</summary>
    public bool NeedsAccount => HostTemplate.Contains("{account}", StringComparison.Ordinal);

    /// <summary>The region is part of the host, so changing it changes the endpoint.</summary>
    public bool RegionInHost => HostTemplate.Contains("{region}", StringComparison.Ordinal);

    /// <summary>The endpoint host for <paramref name="region"/> (the default when empty) and <paramref name="account"/>.</summary>
    public string BuildHost(string region, string? account) =>
        HostTemplate
            .Replace("{region}", string.IsNullOrWhiteSpace(region) ? DefaultRegion : region.Trim(), StringComparison.Ordinal)
            .Replace("{account}", account?.Trim() ?? string.Empty, StringComparison.Ordinal);
}

/// <summary>The providers the connection editor offers for an S3 connection, the generic one last.</summary>
/// <remarks>
/// Endpoints as the providers document them (checked October 2026). Where a provider accepts any
/// signing region (R2, Google Cloud Storage), the one its documentation uses is the default.
/// </remarks>
public static class S3Presets
{
    /// <summary>Any S3-compatible endpoint: the user types the host.</summary>
    public const string OtherId = "other";

    public static IReadOnlyList<S3Preset> All { get; } =
    [
        new("aws", "Amazon S3", "s3.{region}.amazonaws.com", "us-east-1", false, false, 443,
            "Region of the bucket, e.g. eu-central-1. A wrong one is corrected on the first request."),
        new("r2", "Cloudflare R2", "{account}.r2.cloudflarestorage.com", "auto", false, false, 443,
            "Account ID is on the R2 overview page; use the access key and secret of an R2 API token."),
        new("b2", "Backblaze B2", "s3.{region}.backblazeb2.com", "us-west-004", false, false, 443,
            "Region is in the bucket's endpoint (s3.us-west-004.backblazeb2.com); use an application key."),
        new("wasabi", "Wasabi", "s3.{region}.wasabisys.com", "us-east-1", false, false, 443,
            "Region of the bucket, e.g. eu-central-1."),
        new("do", "DigitalOcean Spaces", "{region}.digitaloceanspaces.com", "nyc3", false, false, 443,
            "Region is the datacenter: nyc3, ams3, fra1, sgp1…; use a Spaces access key."),
        new("hetzner", "Hetzner Object Storage", "{region}.your-objectstorage.com", "fsn1", false, false, 443,
            "Location: fsn1, nbg1 or hel1."),
        new("yandex", "Yandex Object Storage", "storage.yandexcloud.net", "ru-central1", false, false, 443,
            "Use a static access key of a service account."),
        new("gcs", "Google Cloud Storage (HMAC)", "storage.googleapis.com", "auto", false, false, 443,
            "Create HMAC keys under Cloud Storage → Settings → Interoperability."),
        new("minio", "MinIO (local)", "localhost", "us-east-1", true, true, 9000,
            "A local MinIO over http on port 9000."),
        new(OtherId, "Other S3-compatible", string.Empty, "us-east-1", false, false, 443,
            "Type the endpoint host; turn on path-style addressing if bucket names do not resolve."),
    ];

    /// <summary>The preset with <paramref name="id"/>, or null for an unknown or empty one.</summary>
    public static S3Preset? Find(string? id) =>
        string.IsNullOrEmpty(id) ? null : All.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
}
