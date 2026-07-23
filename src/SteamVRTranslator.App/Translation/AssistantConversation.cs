using System.Text;
using SteamVRTranslator.App.Localization;

namespace SteamVRTranslator.App.Translation;

internal sealed class AssistantConversation
{
    private readonly List<AssistantConversationTurn> _turns = [];

    public AssistantConversation(CapturedFrame capture)
    {
        Capture = capture;
    }

    public CapturedFrame Capture { get; }

    public IReadOnlyList<AssistantConversationTurn> Snapshot() => _turns.ToArray();

    public string Append(string question, string answer)
    {
        _turns.Add(new AssistantConversationTurn(question.Trim(), answer.Trim()));
        return FormatTranscript();
    }

    public AssistantConversationView CreateView(
        string? pendingQuestion = null,
        string? pendingAnswer = null) =>
        new(
            Snapshot(),
            string.IsNullOrWhiteSpace(pendingQuestion) ? null : pendingQuestion.Trim(),
            string.IsNullOrWhiteSpace(pendingAnswer) ? null : pendingAnswer.Trim());

    public string FormatPending(string? question, string status)
    {
        var output = new StringBuilder(FormatTranscript());
        if (output.Length > 0)
        {
            output.AppendLine().AppendLine();
        }
        var number = _turns.Count + 1;
        if (!string.IsNullOrWhiteSpace(question))
        {
            output.Append("## ").Append(AppLocalization.Text("Chat.User")).Append(' ').Append(number).AppendLine()
                .AppendLine()
                .AppendLine(question.Trim())
                .AppendLine()
                .Append("## ").Append(AppLocalization.Text("Chat.Assistant")).Append(' ').Append(number).AppendLine()
                .AppendLine();
        }
        output.Append(status);
        return output.ToString();
    }

    public string FormatTranscript()
    {
        var output = new StringBuilder();
        for (var index = 0; index < _turns.Count; index++)
        {
            if (index > 0)
            {
                output.AppendLine().AppendLine();
            }
            var turn = _turns[index];
            var number = index + 1;
            output.Append("## ").Append(AppLocalization.Text("Chat.User")).Append(' ').Append(number).AppendLine()
                .AppendLine()
                .AppendLine(turn.User)
                .AppendLine()
                .Append("## ").Append(AppLocalization.Text("Chat.Assistant")).Append(' ').Append(number).AppendLine()
                .AppendLine()
                .Append(turn.Assistant);
        }
        return output.ToString();
    }
}

internal sealed record AssistantConversationView(
    IReadOnlyList<AssistantConversationTurn> Turns,
    string? PendingQuestion,
    string? PendingAnswer);
