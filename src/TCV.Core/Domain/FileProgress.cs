namespace TCV.Core.Domain;

public record FileProgress(
    long BytesCopied,
    long TotalBytes,
    string CurrentFile,
    TimeSpan Elapsed)
{
    public double Percentage => TotalBytes > 0 ? (double)BytesCopied / TotalBytes * 100.0 : 0;
}
