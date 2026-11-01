using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace TypeLay.App;

internal sealed partial class OnboardingWindow
{
    private static readonly CoerceValueCallback? BaseShowActivation = ShowActivatedProperty.GetMetadata(typeof(Window)).CoerceValueCallback;
    private object? _nativeShowRequest;
    private HwndSource? _showSource;

    static OnboardingWindow() => ShowActivatedProperty.OverrideMetadata(typeof(OnboardingWindow),
        new FrameworkPropertyMetadata(true, null, CoerceShowActivation));

    private bool RetiredHiddenShow => !_closed && _nativeShowRequest is { } request
        && !ReferenceEquals(_showRequest, request) && !IsVisible && Visibility != Visibility.Visible;

    private static object CoerceShowActivation(DependencyObject owner, object value)
    {
        // Metadata coercion is replaced, not chained, by WPF. Keep Window's
        // validation and the caller's base value, including bindings/styles.
        value = BaseShowActivation?.Invoke(owner, value) ?? value;
        return ((OnboardingWindow)owner).RetiredHiddenShow ? false : value;
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs args)
    {
        // ClearValue can restore the default without running metadata coercion.
        // Reapply only this retired Show's temporary constraint, not a local value.
        if (args.Property == ShowActivatedProperty && (bool)args.NewValue && RetiredHiddenShow)
            CoerceValue(ShowActivatedProperty);
        base.OnPropertyChanged(args);
    }

    protected override void OnSourceInitialized(EventArgs args)
    {
        if (!_closed)
        {
            _showSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _showSource?.AddHook(CancelRetiredNativeShow);
        }
        base.OnSourceInitialized(args);
    }

    private void RetireShowSource()
    {
        var source = _showSource;
        _showSource = null;
        source?.RemoveHook(CancelRetiredNativeShow);
    }

    private void ShowOpening(object request)
    {
        object? previous = _nativeShowRequest;
        _nativeShowRequest = request;
        bool failed = false;
        try
        {
            CoerceValue(ShowActivatedProperty);
            if (!_closed && !IsVisible && ReferenceEquals(_showRequest, request))
                Show();
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            _nativeShowRequest = previous;
            try
            {
                CoerceValue(ShowActivatedProperty);
            }
            // Do not replace the original public callback exception with a
            // secondary policy observer failure. Fatal allocation still escapes.
            catch (Exception error) when (failed && error is not OutOfMemoryException) { }
        }
    }

    private nint CancelRetiredNativeShow(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int WindowPositionChanging = 0x0046;
        const uint ShowWindow = 0x0040;
        if (message != WindowPositionChanging || lParam == 0 || !RetiredHiddenShow || _showSource?.Handle != hwnd)
            return 0;
        var position = Marshal.PtrToStructure<ShowWindowPosition>(lParam);
        if (position.Handle != hwnd || (position.Flags & ShowWindow) == 0)
            return 0;
        // Cancel before native visibility commits. NOACTIVATE cannot be changed
        // in this message; the retired-only coercion already chose SW_SHOWNA.
        position.Flags &= ~ShowWindow;
        Marshal.StructureToPtr(position, lParam, false);
        // WPF set its internal visible flag after the callback hid the opening.
        // Reset it while the rejected Show is still on the stack, so its style
        // flush cannot revive the HWND and a later inherited Show still works.
        // Hide may reenter; do not touch any opening after it returns.
        Hide();
        return 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ShowWindowPosition
    {
        public nint Handle, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }
}
