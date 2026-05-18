namespace Perelegans.ViewModels;

public sealed class BreakpointResumeCardViewModel(
    string title,
    string subtitle,
    string clientText,
    string workspaceText,
    string processText,
    string recentChangeText,
    string statusText,
    string nextStepText)
{
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public string ClientText { get; } = clientText;
    public string WorkspaceText { get; } = workspaceText;
    public string ProcessText { get; } = processText;
    public string RecentChangeText { get; } = recentChangeText;
    public string StatusText { get; } = statusText;
    public string NextStepText { get; } = nextStepText;
}
