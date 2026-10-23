using TypeLay.Runtime;
using TypeLay.Windows;

namespace TypeLay.Practice;

internal static partial class Program
{
    private const int FinishId = 106;
    private static Control? _finish;
    private static OwnedPracticeCompletion? _completion, _finishRequest;
    private static long _completionSequence;
    private static bool _completionReady, _finishRetry, _sendFinish, _finishVisible;
    private static string? _finishNotice;
    private static readonly List<object> FinishTraces = [];
    private static int _finishTraceCount;
    private static bool _finishCloseObserved;

    // GUI-owned, bounded, content-free observations. The existing outer-loop
    // trace flush owns I/O, never the stock button's window procedure.
    private static void TraceFinish(string stage, string? status = null)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(EditorTracePath) || _finishTraceCount >= 12)
                return;
            int order = ++_finishTraceCount;
            FinishTraces.Add(new
            {
                atMs = WindowsInterruptClock.NowMs,
                operation = "owned-finish-source",
                order,
                stage = order == 12 ? "dropped" : stage,
                sequence = _sequence,
                status = order == 12 ? null : status,
                droppedAtLeast = order == 12 ? 1 : 0
            });
        }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    private static bool CompletionControlVisible => _completionReady && _completion is not null;

    private static void InitializeCompletionControl()
    {
        _finish = Add("Button", "完成练习", 0x00010000 | 0x4000, FinishId);
        RefreshCompletionControl();
    }

    private static void RefreshCompletionControl()
    {
        if (_finish is null)
            return;
        bool visible = CompletionControlVisible;
        bool relayout = visible != _finishVisible;
        _finishVisible = visible;
        SetText(_finish, _finishRequest is not null ? "正在保存…" : _finishRetry ? "重试保存" : "完成练习");
        Native.EnableWindow(_finish.Handle, visible && _finishRequest is null
            && _progressConnected && !_closePending && !_restartPending);
        Native.ShowWindow(_finish.Handle, visible ? 5 : 0);
        if (relayout)
            Layout();
    }

    private static string CompletionProgressNotice(string normal) => _finishNotice ?? normal;

    private static void ResetCompletion()
    {
        _completion = null;
        _finishRequest = null;
        _completionSequence = 0;
        _completionReady = _finishRetry = _sendFinish = false;
        _finishNotice = null;
        RefreshCompletionControl();
    }

    private static void PrepareCompletionRetirement(PracticeMessage message)
    {
        ResetCompletion();
        if (message.Completion is not { } marker)
            return;
        if (!_automatic || _retired || message.Connection != _connection || message.Run != _run
            || message.Sequence != _sequence + 1 || marker.Binding.RunId != _run
            || marker.Binding.ConnectionId != _connection || marker.FinalProgressRevision <= 0)
            throw new InvalidDataException("practice-completion-retirement");
        // The authenticated host pins the last accepted binding before rotating
        // its configuration. This historical fact does not require current focus.
        _completion = marker;
        _completionSequence = message.Sequence;
    }

    private static bool HandleCompletionControl(PracticeMessage message)
    {
        if (message.Kind == "auto-progress" && message.AutomaticProgress is { Completed: true } progress)
        {
            if (!_retired || _completion is not { } marker || _closePending || _restartPending
                || message.Connection != _connection || message.Run != _run || message.Sequence != _sequence
                || _completionSequence != _sequence || !MatchesCompletion(progress, marker))
                throw new InvalidDataException("practice-completion-progress");
            _automaticProgress = progress;
            _completionReady = true;
            RefreshCompletionControl();
            return true;
        }
        if (message.Kind is not ("completion-clear" or "finish-saved" or "finish-retry" or "finish-denied"))
            return false;
        TraceFinish("reply-received", message.Kind);
        if (!_retired || message.Connection != _connection || message.Run != _run || message.Sequence != _sequence
            || _completionSequence != _sequence || message.Completion is null)
            throw new InvalidDataException("practice-completion-order");
        if (message.Kind == "completion-clear")
        {
            if (message.Completion != _completion)
                throw new InvalidDataException("practice-completion-identity");
            var requested = _sendFinish ? null : _finishRequest;
            ResetCompletion();
            _automaticProgress = null;
            // A settings change can cross an already-sent Finish. Keep only
            // enough identity to drain its denial; never retain retry eligibility.
            if (requested is not null)
            {
                _finishRequest = requested;
                _completionSequence = message.Sequence;
            }
            return true;
        }
        if (_finishRequest is null || message.Completion != _finishRequest
            || message.Completion != _completion && message.Kind != "finish-denied")
            throw new InvalidDataException("practice-finish-identity");
        _finishRequest = null;
        if (message.Kind == "finish-saved")
        {
            _finishCloseObserved = true;
            TraceFinish("saved-close");
            ResetCompletion();
            _automaticProgress = null;
            ClosePractice();
        }
        else if (message.Kind == "finish-retry")
        {
            _finishRetry = true;
            _finishNotice = "练习已完成，但未能保存。请重试保存，无需重新练习。";
            RefreshCompletionControl();
        }
        else
        {
            ResetCompletion();
            _automaticProgress = null;
            _finishNotice = "本次完成状态已失效，未保存。请重新开始练习。";
        }
        return true;
    }

    private static bool MatchesCompletion(AutomaticPracticeProgress progress, OwnedPracticeCompletion marker) =>
        progress.Completed && progress.Revision == marker.FinalProgressRevision
        && progress.RunId == marker.Binding.RunId && progress.Target == marker.Binding.Target
        && progress.ConnectionId == marker.Binding.ConnectionId
        && progress.CommandGeneration == marker.Binding.CommandGeneration;

    private static void QueueFinish()
    {
        if (!CompletionControlVisible || !_retired || _finishRequest is not null || !_progressConnected
            || _closePending || _restartPending || _completionSequence != _sequence)
        {
            TraceFinish("queue-rejected");
            return;
        }
        TraceFinish("queue-accepted");
        _finishRequest = _completion;
        _sendFinish = true;
        _finishNotice = null;
        RefreshCompletionControl();
        Native.PostMessageW(_window, ProgressMessage, 0, 0);
    }

    private static void FlushCompletionRequest()
    {
        if (!_sendFinish)
            return;
        _sendFinish = false;
        if (_finishRequest is not { } requested || requested != _completion || !CompletionControlVisible
            || !_retired || !_progressConnected || _closePending || _restartPending
            || _completionSequence != _sequence)
        {
            TraceFinish("flush-rejected");
            return;
        }
        TraceFinish("request-write-start");
        WriteReply(new("finish", _connection, _run, _sequence, Completion: requested));
        TraceFinish("request-write-ended");
    }
}
