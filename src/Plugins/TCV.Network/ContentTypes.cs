using System.IO;

namespace TCV.Network;

/// <summary>
/// The media type a stored object is labelled with, read from its name: what a browser opening a
/// shared link needs to show the file rather than download it. One table for every store that keeps a
/// type of its own (S3, Azure Blob); the cloud drives work it out themselves.
/// </summary>
internal static class ContentTypes
{
    /// <summary>What anything the table does not know is stored as.</summary>
    public const string Fallback = "application/octet-stream";

    public static string ForName(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html",
        ".css" => "text/css",
        ".js" or ".mjs" => "text/javascript",
        ".json" => "application/json",
        ".txt" or ".log" or ".md" or ".csv" => "text/plain",
        ".xml" => "application/xml",
        ".svg" => "image/svg+xml",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".ico" => "image/x-icon",
        ".pdf" => "application/pdf",
        ".wasm" => "application/wasm",
        ".mp4" => "video/mp4",
        ".zip" => "application/zip",
        _ => Fallback,
    };
}
