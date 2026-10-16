using System.IO;
using System.Net;

namespace TCV.Network;

/// <summary>
/// A request the S3 server refused. The message is written for the user — what went wrong and what
/// to check — because the application shows it as it is. An <see cref="IOException"/>, so a file
/// operation offers to retry or skip the item instead of failing as a whole.
/// </summary>
public sealed class S3Exception : IOException
{
    public S3Exception(string message, HttpStatusCode statusCode, string? errorCode)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public HttpStatusCode StatusCode { get; }

    /// <summary>The S3 error code (<c>AccessDenied</c>, <c>NoSuchBucket</c>…), or null when the answer had no body.</summary>
    public string? ErrorCode { get; }

    /// <summary>
    /// The message for a failed request. A HEAD answer has no body, so its status alone has to tell
    /// the story; anything else carries an error code, and the common codes get advice.
    /// </summary>
    internal static string Describe(HttpStatusCode status, S3Error? error, string? bucket, string? key, bool anonymous)
    {
        var code = error?.Code;
        var message = error?.Message;
        var item = !string.IsNullOrEmpty(key) ? $"\"{key}\"" : bucket is not null ? $"The bucket \"{bucket}\"" : "The item";
        var bucketName = bucket ?? "?";
        switch (code)
        {
            case "AccessDenied" or "AllAccessDisabled":
                return anonymous
                    ? $"Access denied ({code}): the connection has no access key, and the bucket is not public."
                    : $"Access denied ({code}): check the access key and its permissions.";
            case "InvalidAccessKeyId":
                return "The access key is not known to this server (InvalidAccessKeyId): check the access key ID and the endpoint.";
            case "SignatureDoesNotMatch":
                return "The request signature does not match (SignatureDoesNotMatch): check the secret key, " +
                       "the region and the addressing style (path-style).";
            case "RequestTimeTooSkewed":
                return "The computer's clock is too far from the server's (RequestTimeTooSkewed): correct the system time.";
            case "ExpiredToken" or "TokenRefreshRequired":
                return $"The session token has expired ({code}): get new credentials.";
            case "InvalidToken":
                return "The session token is not valid (InvalidToken): check the credentials.";
            case "NoSuchBucket":
                return $"The bucket \"{bucketName}\" does not exist (NoSuchBucket): check its name and the endpoint.";
            case "NoSuchKey":
                return $"{item} does not exist (NoSuchKey).";
            case "PermanentRedirect" or "AuthorizationHeaderMalformed" or "IllegalLocationConstraintException":
                return error?.Region is { Length: > 0 } region
                    ? $"The bucket \"{bucketName}\" is in region {region} ({code}): set this region in the connection."
                    : $"The bucket \"{bucketName}\" is in another region ({code}): check the region and the endpoint.";
            case "SlowDown" or "ServiceUnavailable":
                return $"The server is overloaded ({code}): try again in a moment.";
            case "NotImplemented":
                return "This S3 server does not support the request (NotImplemented).";
            case "EntityTooLarge":
                return $"{item} is larger than the server accepts (EntityTooLarge).";
            case "InvalidBucketName":
                return $"\"{bucketName}\" is not a valid bucket name (InvalidBucketName).";
            case "QuotaExceeded" or "InsufficientStorage" or "XMinioStorageFull":
                return $"The storage is full or over its quota ({code}).";
            case not null when !string.IsNullOrWhiteSpace(message):
                return $"{message.TrimEnd('.')} ({code}).";
            case not null:
                return $"The S3 server refused the request ({code}, {(int)status}).";
        }

        return status switch
        {
            HttpStatusCode.Forbidden => anonymous
                ? "Access denied (403): the connection has no access key, and the bucket is not public."
                : "Access denied (403): check the access key, the secret key and the permissions.",
            HttpStatusCode.NotFound => !string.IsNullOrEmpty(key)
                ? $"{item} does not exist (404)."
                : $"The bucket \"{bucketName}\" does not exist (404): check its name and the endpoint.",
            HttpStatusCode.MovedPermanently =>
                "The bucket is in another region (301): check the region and the endpoint.",
            HttpStatusCode.BadRequest =>
                "The server rejected the request (400): check the region and the addressing style (path-style).",
            HttpStatusCode.PreconditionFailed => $"{item} was changed on the server meanwhile (412).",
            >= HttpStatusCode.InternalServerError =>
                $"The S3 server failed ({(int)status} {status}): try again later.",
            _ => $"The S3 server answered {(int)status} {status}.",
        };
    }
}
