using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using SteamVRTranslator.App.Configuration;

namespace SteamVRTranslator.App.Subtitles;

public sealed class SubtitleHistoryViewModel
{
    private SubtitleConfiguration _configuration;
    private readonly SpeakerColorRegistry _speakerColors = new();

    public SubtitleHistoryViewModel(SubtitleConfiguration configuration)
    {
        _configuration = configuration;
    }

    public ObservableCollection<SubtitleHistoryEntry> Entries { get; } = [];

    public void ApplyConfiguration(SubtitleConfiguration configuration) =>
        _configuration = configuration;

    public SubtitleHistoryEntry Add(
        int speaker,
        TimeSpan start,
        TimeSpan end,
        string sourceText)
    {
        var entry = new SubtitleHistoryEntry(
            Guid.NewGuid(),
            speaker,
            _configuration.UseSpeakerColors
                ? _speakerColors.GetBrush(speaker)
                : SpeakerColorRegistry.NeutralBrush,
            start,
            end,
            sourceText,
            string.Empty,
            _configuration.TranslateText);
        Entries.Add(entry);
        Trim();
        return entry;
    }

    public void Clear() => Entries.Clear();

    private void Trim()
    {
        while (Entries.Count > _configuration.MaximumHistoryEntries)
        {
            Entries.RemoveAt(0);
        }

        var characters = Entries.Sum(entry => entry.SourceText.Length + entry.TranslatedText.Length);
        while (Entries.Count > 1 && characters > _configuration.MaximumHistoryCharacters)
        {
            var first = Entries[0];
            characters -= first.SourceText.Length + first.TranslatedText.Length;
            Entries.RemoveAt(0);
        }
    }
}

public sealed class SubtitleHistoryEntry : INotifyPropertyChanged
{
    private string _translatedText;
    private bool _isTranslating;
    private string? _error;

    public SubtitleHistoryEntry(
        Guid id,
        int speaker,
        Brush speakerBrush,
        TimeSpan start,
        TimeSpan end,
        string sourceText,
        string translatedText,
        bool isTranslating)
    {
        Id = id;
        Speaker = speaker;
        SpeakerBrush = speakerBrush;
        Start = start;
        End = end;
        SourceText = sourceText;
        _translatedText = translatedText;
        _isTranslating = isTranslating;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Guid Id { get; }

    public int Speaker { get; }

    public int DisplaySpeaker => Speaker + 1;

    public Brush SpeakerBrush { get; }

    public TimeSpan Start { get; }

    public TimeSpan End { get; }

    public string TimeRange => $"{Start:mm\\:ss} - {End:mm\\:ss}";

    public string SourceText { get; }

    public string TranslatedText
    {
        get => _translatedText;
        set => SetField(ref _translatedText, value);
    }

    public bool IsTranslating
    {
        get => _isTranslating;
        set => SetField(ref _isTranslating, value);
    }

    public string? Error
    {
        get => _error;
        set => SetField(ref _error, value);
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class SpeakerColorRegistry
{
    public static Brush NeutralBrush { get; } = CreateFrozenBrush("#6C756F");

    private static readonly string[] Palette =
    [
        "#2D8CFF",
        "#E15C64",
        "#1C9C75",
        "#A46BCE",
        "#D07A21",
        "#168DA3",
        "#B3538A",
        "#687A2B"
    ];
    private readonly Dictionary<int, Brush> _brushes = [];

    public Brush GetBrush(int speaker)
    {
        if (_brushes.TryGetValue(speaker, out var brush))
        {
            return brush;
        }

        var created = CreateFrozenBrush(Palette[Math.Abs(speaker) % Palette.Length]);
        _brushes[speaker] = created;
        return created;
    }

    private static Brush CreateFrozenBrush(string value)
    {
        var color = (Color)ColorConverter.ConvertFromString(value);
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
