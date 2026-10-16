using TCV.Ai.Agents;
using TCV.App.Localization;

namespace TCV.App.Agents;

/// <summary>
/// Human wording for <see cref="ChangeOrigin"/>. The dashboard used to print the enum name, which said
/// «Agent» with the same confidence as «Supervised» although the first is a circumstantial guess and
/// the second is a log line (ADR-0019). The strength of the evidence is part of the label.
/// </summary>
public static class AgentOriginText
{
    public static string Name(ChangeOrigin origin) => LocalizationManager.Get(NameKey(origin));

    /// <summary>What the verdict rests on — the tooltip that keeps the weak levels honest.</summary>
    /// <param name="byDirectory">The log claimed the file through its folder (a batch), not by name —
    /// then the hint must not say «proof».</param>
    public static string Hint(ChangeOrigin origin, bool byDirectory = false) =>
        LocalizationManager.Get(HintKey(origin, byDirectory));

    public static string NameKey(ChangeOrigin origin) => origin switch
    {
        ChangeOrigin.Supervised => "Agent_OriginSupervised",
        ChangeOrigin.Manager => "Agent_OriginManager",
        ChangeOrigin.Agent => "Agent_OriginAgent",
        _ => "Agent_OriginExternal",
    };

    public static string HintKey(ChangeOrigin origin, bool byDirectory = false) => origin switch
    {
        ChangeOrigin.Supervised when byDirectory => "Agent_OriginSupervisedDirHint",
        ChangeOrigin.Manager when byDirectory => "Agent_OriginManagerDirHint",
        ChangeOrigin.Supervised => "Agent_OriginSupervisedHint",
        ChangeOrigin.Manager => "Agent_OriginManagerHint",
        ChangeOrigin.Agent => "Agent_OriginAgentHint",
        _ => "Agent_OriginExternalHint",
    };
}
