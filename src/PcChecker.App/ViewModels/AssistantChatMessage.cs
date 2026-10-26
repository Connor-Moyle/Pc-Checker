using CommunityToolkit.Mvvm.ComponentModel;
using PcChecker.Core.Assistant;

namespace PcChecker.App.ViewModels;

/// <summary>One bubble in the Ask Claude chat. Assistant text is tidied for display once streaming finishes.</summary>
public sealed partial class AssistantChatMessage : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    [NotifyPropertyChangedFor(nameof(HasText))]
    private string _text;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayText))]
    [NotifyPropertyChangedFor(nameof(ShowTypingIndicator))]
    private bool _isStreaming;

    [ObservableProperty]
    private bool _isError;

    public AssistantChatMessage(string role, string text)
    {
        Role = role == AssistantMessage.AssistantRole ? AssistantMessage.AssistantRole : AssistantMessage.UserRole;
        _text = text;
        Timestamp = DateTimeOffset.Now;
    }

    public string Role { get; }

    public DateTimeOffset Timestamp { get; }

    public bool IsUser => Role == AssistantMessage.UserRole;

    public bool IsAssistant => !IsUser;

    public string SenderLabel => IsUser ? "You" : "Claude";

    public string TimeText => Timestamp.ToLocalTime().ToString("HH:mm");

    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    /// <summary>Spinner shown while the first tokens are still on their way.</summary>
    public bool ShowTypingIndicator => IsStreaming && !HasText;

    /// <summary>User text verbatim; assistant text with Markdown markers removed once the answer is complete.</summary>
    public string DisplayText => IsUser || IsStreaming ? Text : AssistantTextFormatter.ToPlainText(Text);

    public AssistantMessage ToMessage() => new(Role, Text);

    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(ShowTypingIndicator));
}
