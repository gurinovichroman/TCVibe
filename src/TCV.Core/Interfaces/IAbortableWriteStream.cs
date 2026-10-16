namespace TCV.Core.Interfaces;

/// <summary>
/// Implemented by write streams whose disposal <em>commits</em> the data — an HTTP PUT sent when
/// the stream closes, for instance. A caller whose copy failed halfway calls <see cref="Abort"/>
/// before disposing, so the half-written body is discarded instead of being published as the file.
/// </summary>
public interface IAbortableWriteStream
{
    /// <summary>Marks the stream as failed: disposing it afterwards must not publish anything.</summary>
    void Abort();
}
