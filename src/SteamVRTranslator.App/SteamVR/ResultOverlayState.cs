namespace SteamVRTranslator.App.SteamVR;

internal enum ResultStatus
{
    Streaming,
    Completed,
    Error
}

internal sealed record ResultOverlaySnapshot(
    long RequestId,
    string Text,
    ResultStatus Status,
    double ScrollOffset,
    bool IsVisible,
    DateTimeOffset CreatedAt);

internal sealed class ResultOverlayState
{
    private ResultOverlaySnapshot? _result;
    private long _nextRequestId;

    public bool HasResult => _result is not null;

    public bool IsVisible => _result?.IsVisible == true;

    public long Begin(string placeholder)
    {
        var requestId = ++_nextRequestId;
        _result = new ResultOverlaySnapshot(
            requestId,
            placeholder,
            ResultStatus.Streaming,
            0,
            true,
            DateTimeOffset.Now);
        return requestId;
    }

    public bool Update(long requestId, string text)
    {
        if (_result is not { } result ||
            result.RequestId != requestId ||
            result.Status != ResultStatus.Streaming ||
            string.IsNullOrEmpty(text))
        {
            return false;
        }

        _result = result with { Text = text };
        return true;
    }

    public bool Complete(long requestId, string text, bool isError)
    {
        if (_result is not { } result || result.RequestId != requestId)
        {
            return false;
        }

        _result = result with
        {
            Text = string.IsNullOrWhiteSpace(text) ? result.Text : text,
            Status = isError ? ResultStatus.Error : ResultStatus.Completed
        };
        return true;
    }

    public bool ToggleVisibility()
    {
        if (_result is not { } result)
        {
            return false;
        }

        _result = result with { IsVisible = !result.IsVisible };
        return true;
    }

    public bool Scroll(double delta, double maximumOffset)
    {
        if (_result is not { IsVisible: true } result)
        {
            return false;
        }

        var maximum = Math.Max(0, maximumOffset);
        var next = Math.Clamp(result.ScrollOffset + delta, 0, maximum);
        if (Math.Abs(next - result.ScrollOffset) < 0.1)
        {
            return false;
        }

        _result = result with { ScrollOffset = next };
        return true;
    }

    public void ClampScroll(double maximumOffset)
    {
        if (_result is not { } result)
        {
            return;
        }

        var clamped = Math.Clamp(result.ScrollOffset, 0, Math.Max(0, maximumOffset));
        if (Math.Abs(clamped - result.ScrollOffset) >= 0.1)
        {
            _result = result with { ScrollOffset = clamped };
        }
    }

    public ResultOverlaySnapshot? Snapshot() => _result;
}

internal readonly record struct ResultProgressUpdate(
    long OverlayId,
    long RequestId,
    long OperationId,
    int Sequence,
    string Text,
    DateTimeOffset QueuedAt);
