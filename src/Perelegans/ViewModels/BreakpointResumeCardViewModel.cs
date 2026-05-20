namespace Perelegans.ViewModels;

public sealed class BreakpointResumeCardViewModel(
    string title,
    string subtitle,
    string clientText,
    string workspaceText,
    string processText,
    string recentChangeText,
    string statusText,
    string nextStepText,
    string capsuleIntroText = "",
    string recoveryPromptText = "",
    IReadOnlyList<string>? evidenceItems = null,
    IReadOnlyList<string>? resumeSteps = null)
{
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public string ClientText { get; } = clientText;
    public string WorkspaceText { get; } = workspaceText;
    public string ProcessText { get; } = processText;
    public string RecentChangeText { get; } = recentChangeText;
    public string StatusText { get; } = statusText;
    public string NextStepText { get; } = nextStepText;
    public string CapsuleIntroText { get; } = capsuleIntroText;
    public string RecoveryPromptText { get; } = recoveryPromptText;
    public IReadOnlyList<string> EvidenceItems { get; } = evidenceItems ?? [];
    public IReadOnlyList<string> ResumeSteps { get; } = resumeSteps ?? [];
    public bool HasEvidenceItems => EvidenceItems.Count > 0;
    public bool HasResumeSteps => ResumeSteps.Count > 0;
    public bool HasCapsuleIntro => !string.IsNullOrWhiteSpace(CapsuleIntroText);
    public bool HasRecoveryPrompt => !string.IsNullOrWhiteSpace(RecoveryPromptText);
}
