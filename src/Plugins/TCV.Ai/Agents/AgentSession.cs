namespace TCV.Ai.Agents;

/// <summary>
/// One recorded run of a coding agent: which tool, in which checkout, over which stretch of time.
/// <para>
/// This exists because git does not record who wrote a change and our own
/// <c>OperationLog</c> only sees writes that went through the manager. An agent editing files with
/// its own tools is invisible to both. A session is the cheapest honest evidence we can keep: we
/// know we started <c>claude</c> in <c>D:\repo</c> at 14:02, so a file under that root whose
/// mtime falls after 14:02 was plausibly written by it (see <see cref="ChangeAttribution"/> for
/// exactly how strong that claim is allowed to be).
/// </para>
/// </summary>
/// <param name="Id">Stable id, so a session survives a restart and can be referenced from a log.</param>
/// <param name="Tool">Display name of the agent CLI ("Claude Code").</param>
/// <param name="Root">Absolute directory the agent was started in; changes outside it are not its work.</param>
/// <param name="StartedUtc">When the process was launched.</param>
/// <param name="EndedUtc">When the user marked it finished; null while it is considered live.</param>
/// <param name="Task">What it was asked to do, when the launcher knows (6.13a).</param>
public sealed record AgentSession(
    string Id,
    string Tool,
    string Root,
    DateTime StartedUtc,
    DateTime? EndedUtc,
    string Task)
{
    /// <summary>True while the session has no end and can still claim fresh changes.</summary>
    public bool IsOpen => EndedUtc is null;

    /// <summary>Label shown in the provenance column — the tool, plus the task when there is one.</summary>
    public string Label => Task.Length > 0 ? $"{Tool}: {Task}" : Tool;

    /// <summary>
    /// True when <paramref name="whenUtc"/> falls inside this session. An open session is treated as
    /// running up to <paramref name="nowUtc"/> rather than forever, so a session the user forgot to
    /// close cannot silently claim next week's edits.
    /// </summary>
    public bool Covers(DateTime whenUtc, DateTime nowUtc) =>
        whenUtc >= StartedUtc && whenUtc <= (EndedUtc ?? nowUtc);
}
