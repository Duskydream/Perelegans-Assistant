using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace Perelegans.ViewModels;

public partial class ConversationMessage : ObservableObject
{
    private static readonly TimeSpan TypingDelay = TimeSpan.FromMilliseconds(14);
    private readonly CancellationTokenSource _streamingCancellation = new();

    private ConversationMessage(
        string text,
        bool isUser,
        UsageStatsSnapshot? usageStats = null,
        BreakpointResumeCardViewModel? breakpointCard = null,
        DailyReviewCardViewModel? dailyReviewCard = null)
    {
        _text = isUser ? text : string.Empty;
        IsUser = isUser;
        UsageStats = usageStats;
        BreakpointCard = breakpointCard;
        DailyReviewCard = dailyReviewCard;
        Timestamp = DateTime.Now;
        Alignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;

        if (!isUser)
        {
            _ = StreamTextAsync(text);
        }
    }

    [ObservableProperty]
    private string _text;

    [ObservableProperty]
    private bool _isStreaming;

    public bool IsUser { get; }
    public bool IsAssistant => !IsUser;
    public bool HasUsageStats => UsageStats?.HasSlices == true;
    public bool HasBreakpointCard => BreakpointCard != null;
    public bool HasDailyReviewCard => DailyReviewCard != null;
    public UsageStatsSnapshot? UsageStats { get; }
    public BreakpointResumeCardViewModel? BreakpointCard { get; }
    public DailyReviewCardViewModel? DailyReviewCard { get; }
    public DateTime Timestamp { get; }
    public HorizontalAlignment Alignment { get; }

    public static ConversationMessage User(string text) => new(text, true);
    public static ConversationMessage Assistant(string text) => new(text, false);
    public static ConversationMessage AssistantWithUsageStats(string text, UsageStatsSnapshot usageStats) => new(text, false, usageStats);
    public static ConversationMessage AssistantWithBreakpointCard(string text, BreakpointResumeCardViewModel breakpointCard) => new(text, false, breakpointCard: breakpointCard);
    public static ConversationMessage AssistantWithDailyReviewCard(string text, DailyReviewCardViewModel dailyReviewCard, UsageStatsSnapshot? usageStats = null) =>
        new(text, false, usageStats, dailyReviewCard: dailyReviewCard);

    public void StopStreaming()
    {
        if (!IsStreaming)
        {
            return;
        }

        _streamingCancellation.Cancel();
        IsStreaming = false;
    }

    private async Task StreamTextAsync(string text)
    {
        IsStreaming = true;
        try
        {
            foreach (var character in text)
            {
                _streamingCancellation.Token.ThrowIfCancellationRequested();
                Text += character;
                await Task.Delay(TypingDelay, _streamingCancellation.Token);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsStreaming = false;
        }
    }
}
