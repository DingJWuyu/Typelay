using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows;
using TypeLay.Windows.Contracts;

namespace TypeLay.App;

internal sealed partial class WindowsInputRouter
{
    private sealed record NativeWakeFlight(NativeWakeCapture Capture, Task<InputCapture> Read);
    private NativeWakeFlight? _nativeWakeFlight;

    private bool TryReadNativeWake(out InputNotice? notice)
    {
        notice = null;
        if (_disposed)
            return false;
        if (_nativeWakeFlight is null && hook.TakeNativeWakeCapture() is { } pending)
        {
            if (wakeExcluded is null || browser.OwnsForeground
                || !hook.NativeWakeCaptureCurrent(pending, WindowsInterruptClock.NowMs))
            {
                notice = WakeRefusal(pending, Denial.StaleObservation);
                hook.FinishNativeWakeCapture(pending);
                return true;
            }
            try
            {
                _nativeWakeFlight = new(pending, native.CaptureWakeAsync(pending, wakeExcluded(),
                    () => !_disposed && !browser.OwnsForeground
                        && hook.NativeWakeCaptureCurrent(pending, WindowsInterruptClock.NowMs)));
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                notice = WakeRefusal(pending, Denial.PlatformFailure);
                hook.FinishNativeWakeCapture(pending);
                return true;
            }
        }
        if (_nativeWakeFlight is not { Read.IsCompleted: true } flight)
            return false;
        _nativeWakeFlight = null;
        var capture = flight.Capture;
        try
        {
            var result = flight.Read.GetAwaiter().GetResult();
            var source = result.Snapshot;
            // Only the notice-delivery seam accepts a controlled time in tests.
            // Request admission, queued-current checks and Broker keep their
            // original real clock and deadline.
            long now = wakeDeliveryNow?.Invoke() ?? WindowsInterruptClock.NowMs;
            // This is the actual notice-delivery boundary, not an async
            // continuation whose queued result can outlive input/configuration.
            if (browser.OwnsForeground || !hook.NativeWakeCaptureCurrent(capture, now)
                || source is null || result.Focus != FocusMember.Source
                || result.UserEpoch != capture.Epochs.User || result.ForegroundEpoch != capture.Epochs.Foreground
                || !WindowsTarget.TryDecode(source.Target, out var target)
                || target.ProcessId != capture.Origin.ProcessId || target.ProcessStartUtcTicks != capture.Origin.ProcessStartUtcTicks
                || target.RootHwnd != capture.Origin.RootHwnd || target.ControlHwnd != capture.Origin.ControlHwnd)
            {
                notice = WakeRefusal(capture, Denial.StaleObservation);
                return true;
            }
            var evidence = new CommandCaptureEvidence(source.Target, capture.Origin.TriggeredAtMs, source.ObservedAtMs);
            Denial denial = evidence.Check(source, now);
            if (denial == Denial.None && (source.Access != FieldAccess.Supported || source.WriteSupported || source.DirectWriteSupported))
                denial = Denial.Unsupported;
            if (denial == Denial.None && source.Ime != ImeState.Clear
                && !(source.Ime == ImeState.Unknown && source.PreviewText is not null))
                denial = Denial.ImeUnclear;
            notice = denial != Denial.None ? WakeRefusal(capture, denial)
                : new("wake", AcceptedAtMs: capture.Origin.TriggeredAtMs,
                    UserEpoch: capture.Epochs.User, ForegroundEpoch: capture.Epochs.Foreground,
                    Source: source, CommandGeneration: capture.Generation, CommandSequence: capture.Sequence,
                    CommandCapture: evidence, Application: result.Application);
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            notice = WakeRefusal(capture, error is OperationCanceledException ? Denial.StaleObservation : Denial.PlatformFailure);
            return true;
        }
        finally { hook.FinishNativeWakeCapture(capture); }
    }
    private static async Task ObserveRetiredWake(Task<InputCapture> read)
    {
        try
        {
            await read.ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }
    private static InputNotice WakeRefusal(NativeWakeCapture capture, Denial reason) =>
        new("wake", AcceptedAtMs: capture.Origin.TriggeredAtMs, CommandGeneration: capture.Generation,
            CommandSequence: capture.Sequence, Rejection: reason);
}
