using System.Net;

namespace TCV.Network.Cloud;

/// <summary>
/// A cloud drive refused a request. The message is ready to show — the provider's own summary plus
/// the HTTP status and the provider's error code — and the code is kept for callers that branch on it.
/// </summary>
public sealed class CloudDriveException(string message, HttpStatusCode statusCode, string? errorCode)
    : IOException(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    /// <summary>The provider's error code (<c>itemNotFound</c>, <c>path/conflict/file</c>, <c>storageQuotaExceeded</c>…), when it gave one.</summary>
    public string? ErrorCode { get; } = errorCode;
}
