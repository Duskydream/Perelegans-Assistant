using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace Perelegans.ViewModels;

public partial class ConversationMessage : ObservableObject
{
    private static readonly TimeSpan TypingDelay = TimeSpan.FromMilliseconds(14);

    private ConversationMessage(string text, bool isUser)
    {
        _text = isUser ? text : string.Empty;
        IsUser = isUser;
        Timestamp = DateTime.Now;
        Alignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left;

        if (!isUser)
        {
            _ = StreamTextAsync(text);
        }
    }

    [ObservableProperty]
    private string _text;

    public bool IsUser { get; }
    public DateTime Timestamp { get; }
    public HorizontalAlignment Alignment { get; }

    public static ConversationMessage User(string text) => new(text, true);
    public static ConversationMessage Assistant(string text) => new(text, false);

    private async Task StreamTextAsync(string text)
    {
        foreach (var character in text)
        {
            Text += character;
            await Task.Delay(TypingDelay);
        }
    }
}
