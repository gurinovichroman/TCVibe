using System.Collections.ObjectModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TCV.Ai.Agents;
using TCV.App.Localization;

namespace TCV.App.ViewModels.Dialogs;

/// <summary>
/// The approval prompt an agent's write has to pass (6.13b).
/// <para>
/// It shows the full target list rather than a count, and «Refuse» is the default button. Both are
/// deliberate: this dialog appears while the user is doing something else, and a prompt that is easy
/// to dismiss with Enter while summarising itself as «delete 14 items» is how supervised writing turns
/// back into unsupervised writing (INV-11).
/// </para>
/// </summary>
public partial class AgentApprovalDialogViewModel : ObservableObject, IDisposable
{
    private readonly DateTime _deadline;
    private readonly DispatcherTimer? _timer;

    /// <param name="proposal">What the agent asked for.</param>
    /// <param name="timeout">How long until the gate counts the prompt as refused; null shows no countdown.</param>
    /// <param name="toRecycleBin">For a delete: whether it goes to the recycle bin (null when not a delete).</param>
    public AgentApprovalDialogViewModel(
        AgentWriteProposal proposal, TimeSpan? timeout = null, bool? toRecycleBin = null)
    {
        Proposal = proposal;

        WindowTitle = LocalizationManager.Get("AgentApprove_Title");

        Headline = string.Format(
            LocalizationManager.Get("AgentApprove_Headline"), proposal.Agent, ActionText(proposal));

        foreach (var source in proposal.Sources) Targets.Add(source);

        if (proposal.Kind == AgentWriteKind.Delete && toRecycleBin is { } recycle)
        {
            Consequence = LocalizationManager.Get(
                recycle ? "AgentApprove_DeleteRecycle" : "AgentApprove_DeletePermanent");
            ConsequenceIsSevere = !recycle;
        }

        if (timeout is { } wait && wait > TimeSpan.Zero)
        {
            _deadline = DateTime.UtcNow + wait;
            UpdateCountdown();

            // A countdown needs a dispatcher; a view model built in a unit test has none and simply
            // shows the initial text.
            if (System.Windows.Application.Current is not null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
                _timer.Tick += (_, _) => UpdateCountdown();
                _timer.Start();
            }
        }
    }

    public AgentWriteProposal Proposal { get; }

    public string WindowTitle { get; }

    /// <summary>One line naming who asks and what they want, in words rather than an enum.</summary>
    public string Headline { get; }

    /// <summary>Every path the operation touches — the part a count would hide.</summary>
    public ObservableCollection<string> Targets { get; } = new();

    public string Destination => Proposal.Destination;

    public bool HasDestination => Proposal.Destination.Length > 0;

    /// <summary>Drives the warning styling: a delete or a move can lose work, a copy cannot.</summary>
    public bool IsDestructive => Proposal.IsDestructive;

    /// <summary>What a delete will do to the files: recycle bin or gone for good. Empty otherwise.</summary>
    public string Consequence { get; } = string.Empty;

    public bool HasConsequence => Consequence.Length > 0;

    /// <summary>True for a permanent delete, which is styled as a warning.</summary>
    public bool ConsequenceIsSevere { get; }

    /// <summary>The reassurance that makes approving reasonable — this stays undoable.</summary>
    public string Footnote => LocalizationManager.Get("AgentApprove_Footnote");

    /// <summary>«Refused automatically in 4:32» — the prompt does not wait forever, and says so.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCountdown))]
    private string _countdownText = string.Empty;

    public bool HasCountdown => CountdownText.Length > 0;

    public bool Approved { get; private set; }

    /// <summary>
    /// True once the gate gave up waiting. Read by the dialog service before the window is shown, so a
    /// prompt withdrawn in the moment between building this model and opening the window never appears.
    /// </summary>
    public bool Withdrawn { get; private set; }

    public bool DialogResult { get; private set; }
    public event EventHandler? CloseRequested;

    /// <summary>Guards against a second decision: the window is closed by the first one.</summary>
    private bool _decided;

    [RelayCommand]
    private void Approve()
    {
        if (_decided) return;

        Approved = true;
        Close(true);
    }

    [RelayCommand]
    private void Refuse()
    {
        if (_decided) return;

        Approved = false;
        Close(false);
    }

    /// <summary>
    /// Takes the prompt down because the gate's bounded wait expired (<c>ApprovalTimeoutSeconds</c>).
    /// It has already answered the agent with a refusal by then, so leaving the modal window up would
    /// block the whole application and go on offering an «Allow» that performs no write. Called on the
    /// UI thread.
    /// </summary>
    public void Withdraw()
    {
        if (_decided) return;

        Withdrawn = true;
        Approved = false;
        Close(false);
    }

    /// <summary>Stops the countdown timer when the window goes away by any route (e.g. the title-bar X).</summary>
    public void Dispose() => _timer?.Stop();

    private void UpdateCountdown()
    {
        var left = _deadline - DateTime.UtcNow;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;

        CountdownText = string.Format(
            LocalizationManager.Get("AgentApprove_Countdown"),
            $"{(int)left.TotalMinutes}:{left.Seconds:00}");
    }

    private void Close(bool result)
    {
        if (_decided) return;
        _decided = true;
        _timer?.Stop();

        DialogResult = result;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    private static string ActionText(AgentWriteProposal proposal) => proposal.Kind switch
    {
        AgentWriteKind.Copy => LocalizationManager.Get("AgentApprove_ActionCopy"),
        AgentWriteKind.Move => LocalizationManager.Get("AgentApprove_ActionMove"),
        AgentWriteKind.Delete => LocalizationManager.Get("AgentApprove_ActionDelete"),
        AgentWriteKind.CreateDirectory => LocalizationManager.Get("AgentApprove_ActionCreateDir"),
        _ => proposal.Kind.ToString().ToLowerInvariant(),
    };
}
