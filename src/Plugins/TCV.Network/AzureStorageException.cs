using System.IO;
using System.Net;

namespace TCV.Network;

/// <summary>
/// A request Azure Blob Storage refused. An <see cref="IOException"/>, so a file operation offers
/// retry and skip as for any other I/O error; the message says what to check, in words a user acts on.
/// </summary>
public sealed class AzureStorageException : IOException
{
    public AzureStorageException(string message, HttpStatusCode statusCode, string? errorCode)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public HttpStatusCode StatusCode { get; }

    /// <summary>The service's error code (<c>AuthenticationFailed</c>, <c>BlobNotFound</c>…), when it sent one.</summary>
    public string? ErrorCode { get; }

    /// <summary>
    /// The message for a refusal. <paramref name="code"/> comes from <c>x-ms-error-code</c> or the
    /// <c>&lt;Error&gt;</c> body; <paramref name="detail"/> is <c>AuthenticationErrorDetail</c>, which
    /// tells an expired token from a wrong key from a wrong clock. <paramref name="subject"/> names
    /// the file or folder, or is null.
    /// </summary>
    internal static string Describe(
        HttpStatusCode status, string? code, string? serverMessage, string? detail, string? subject, bool usesSasToken)
    {
        var what = string.IsNullOrEmpty(subject) ? "The item" : $"\"{subject}\"";
        var text = code switch
        {
            "AuthenticationFailed" => DescribeAuthentication(detail, usesSasToken),
            "NoAuthenticationInformation" =>
                "This storage is not public: enter the account key or a SAS token in the connection.",
            "KeyBasedAuthenticationNotPermitted" =>
                "The storage account does not accept access keys, nor SAS tokens signed with them (shared key access is turned off).",
            "AuthorizationPermissionMismatch" => usesSasToken
                ? "The SAS token does not allow this. Check its permissions: read, write, delete, list, create as the action needs."
                : "This account is not allowed to do this here.",
            // What Azure answers when the account's network rules keep this computer out — and what
            // Azurite answers for any bad signature, so the key or token is named too.
            "AuthorizationFailure" => usesSasToken
                ? "This request is not authorized. The SAS token may have expired or not cover it, or the storage account's firewall does not let this network in."
                : "This request is not authorized. Check the account key, and that the storage account's firewall lets this network in.",
            "AuthorizationResourceTypeMismatch" =>
                "The SAS token is not valid for this kind of resource. A container SAS cannot list the account's containers: enter the container in the connection.",
            "AuthorizationServiceMismatch" => "The SAS token does not include the Blob service.",
            "AuthorizationSourceIPMismatch" => "The SAS token is not valid from this computer's IP address.",
            "AuthorizationProtocolMismatch" => "The SAS token only allows HTTPS: turn off plain HTTP in the connection.",
            "InsufficientAccountPermissions" or "AccountIsDisabled" => "The storage account is disabled or does not allow this.",
            "ContainerNotFound" => "The container does not exist (check its name in the connection).",
            "BlobNotFound" or "ResourceNotFound" or "PathNotFound" =>
                $"{what} does not exist; it may have been deleted or renamed meanwhile.",
            "ContainerAlreadyExists" or "BlobAlreadyExists" or "PathAlreadyExists" => $"{what} already exists.",
            "ContainerBeingDeleted" =>
                "The container is being deleted. Azure needs a minute before a container of that name can be made again.",
            "InvalidResourceName" or "OutOfRangeInput" =>
                "The name is not valid for Azure. Container names are 3 to 63 lower-case letters, digits and single hyphens; a blob name is at most 1024 characters.",
            "DirectoryIsNotEmpty" => $"{what} is not empty.",
            "LeaseIdMissing" or "LeaseIdMismatchWithBlobOperation" or "LeaseIdMismatchWithContainerOperation"
                or "LeaseAlreadyPresent" or "LeaseIsBreakingAndCannotBeAcquired" =>
                $"{what} is leased (locked) by another client.",
            "BlobImmutableDueToPolicy" or "BlobImmutableDueToLegalHold" or "ContainerImmutable" =>
                $"{what} is protected by an immutability policy or a legal hold.",
            "BlobArchived" => $"{what} is in the Archive tier: rehydrate it to Hot or Cool before reading it.",
            "CannotVerifyCopySource" =>
                "Azure could not read the source of the copy" + (serverMessage is null ? "." : $": {serverMessage}"),
            "ServerBusy" or "InternalError" or "OperationTimedOut" =>
                "Azure Blob Storage is busy or failed for the moment. Try again in a little while.",
            _ => DescribeByStatus(status, what, serverMessage, usesSasToken),
        };

        var tag = code is null ? $"HTTP {(int)status}" : $"{code}, HTTP {(int)status}";
        return $"{text} ({tag})";
    }

    private static string DescribeAuthentication(string? detail, bool usesSasToken)
    {
        if (detail is not null)
        {
            if (detail.Contains("time frame", StringComparison.OrdinalIgnoreCase)
                || detail.Contains("expir", StringComparison.OrdinalIgnoreCase))
            {
                return "The SAS token has expired or is not valid yet: create a new one.";
            }

            if (detail.Contains("date header", StringComparison.OrdinalIgnoreCase))
            {
                return "Azure refused the request's time: set this computer's clock right (it must be within 15 minutes of the real time).";
            }

            if (detail.Contains("signature", StringComparison.OrdinalIgnoreCase))
            {
                return usesSasToken
                    ? "The SAS token's signature does not match: it was cut short or changed. Paste the whole token again."
                    : "The account key does not match the account: copy key1 or key2 again from the storage account's Access keys, and check the account name.";
            }
        }

        return usesSasToken
            ? "Azure did not accept the SAS token. It may have expired or been revoked; this computer's clock must also be right."
            : "Azure did not accept the account key. Check the account name and key, and that this computer's clock is right.";
    }

    private static string DescribeByStatus(HttpStatusCode status, string what, string? serverMessage, bool usesSasToken) =>
        status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => DescribeAuthentication(null, usesSasToken),
            HttpStatusCode.NotFound => $"{what} does not exist.",
            HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed when serverMessage is null =>
                $"{what} was changed by someone else meanwhile.",
            >= HttpStatusCode.InternalServerError when serverMessage is null =>
                "Azure Blob Storage failed for the moment. Try again in a little while.",
            _ => serverMessage is null
                ? "Azure Blob Storage refused the request."
                : $"Azure Blob Storage refused the request: {serverMessage}",
        };
}
