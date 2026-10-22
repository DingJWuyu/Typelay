using System.Diagnostics;
using System.Runtime.InteropServices;
using TypeLay.App;

namespace TypeLay.App.Tests;

public sealed partial class NativeSourceTimeoutProbe
{
    // A separate test-owned hook, never production KeyboardHook. Its stall starts
    // only after the real native transaction reports Claim. It handles no user
    // input and does not expose a production timing/injection seam.
    private sealed class ProbeHook : IDisposable
    {
        private const uint PingMessage = 0x8037;
        private readonly nint _root;
        private readonly nuint _identity;
        private readonly bool _pause, _observerOnly;
        private readonly Thread _thread;
        private readonly Native.HookProc _callback;
        private nint _hook;
        private uint _threadId;
        private StockEditActivation? _activation;
        private bool _claimed;
        private int _handled, _markerCallbacks, _stopping;
        private Exception? _failure;
        private readonly TaskCompletionSource<bool> _ping = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<StockEditActivation.Result> Activated { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> HoldWindowElapsed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<bool> Returned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim Release { get; } = new(false);
        internal double HeldMs
        {
            get; private set;
        }
        internal bool UnhookSucceeded
        {
            get; private set;
        }
        internal int UnhookError
        {
            get; private set;
        }
        internal int MarkerCallbacks => Volatile.Read(ref _markerCallbacks);

        internal ProbeHook(nint root, nuint identity, bool pause, bool observerOnly = false)
        {
            _root = root;
            _identity = identity;
            _pause = pause;
            _observerOnly = observerOnly;
            _callback = Callback;
            _thread = new Thread(Run) { IsBackground = true, Name = "TypeLay test-only LL timeout observer" };
            _thread.Start();
        }

        internal void Arm(StockEditActivation activation) => Volatile.Write(ref _activation, activation);

        private void Run()
        {
            try
            {
                _threadId = Native.GetCurrentThreadId();
                _hook = Native.SetWindowsHookExW(13, _callback, Native.GetModuleHandleW(null), 0);
                if (_hook == 0)
                    throw new InvalidOperationException("Test-owned low-level hook installation failed.");
                Ready.TrySetResult(true);
                int status = 0;
                while (Volatile.Read(ref _stopping) == 0 && (status = Native.GetMessageW(out var message, 0, 0, 0)) > 0)
                {
                    if (message.Message == PingMessage)
                        _ping.TrySetResult(true);
                    else
                    {
                        Native.TranslateMessage(ref message);
                        Native.DispatchMessageW(ref message);
                    }
                }
                if (status < 0)
                    throw new InvalidOperationException("Test-owned hook message pump failed.");
            }
            catch (Exception error)
            {
                _failure = error;
                Ready.TrySetException(error);
                Activated.TrySetException(error);
                HoldWindowElapsed.TrySetException(error);
                Returned.TrySetException(error);
                _ping.TrySetException(error);
            }
            finally
            {
                // Windows can already have removed a timed-out LL hook. A false
                // Unhook result therefore does not itself diagnose removal.
                if (_hook != 0)
                {
                    UnhookSucceeded = UnhookWindowsHookEx(_hook);
                    UnhookError = UnhookSucceeded ? 0 : Marshal.GetLastWin32Error();
                    if (!UnhookSucceeded && UnhookError != 1404) // ERROR_INVALID_HOOK_HANDLE
                        _failure ??= new InvalidOperationException($"Test hook unhook failed: {UnhookError}.");
                }
                GC.KeepAlive(_callback);
            }
        }

        private nint Callback(int code, nint message, nint pointer)
        {
            try
            {
                if (code != 0 || Volatile.Read(ref _stopping) != 0)
                    return Next(code, message, pointer);
                var key = Marshal.PtrToStructure<Native.KeyData>(pointer);
                if (key.Extra != _identity || (key.Flags & 0x10) == 0)
                    return Next(code, message, pointer);
                if (key.Key == 0x86)
                    Interlocked.Increment(ref _markerCallbacks);
                if (_observerOnly || key.Key != 9 || key.Scan != 0x0f)
                    return Next(code, message, pointer);
                if (message == 0x101 && _claimed)
                    return 1;
                if (message != 0x100 || Interlocked.Exchange(ref _handled, 1) != 0 || Native.GetForegroundWindow() != _root)
                    return Next(code, message, pointer);
                var activation = Volatile.Read(ref _activation) ?? throw new InvalidOperationException("Diagnostic key preceded arm.");
                var result = activation.Activate();
                _claimed = result.Claimed;
                Activated.TrySetResult(result);
                if (result.Claimed && _pause)
                {
                    var hold = Stopwatch.StartNew();
                    Release.Wait(TimeSpan.FromMilliseconds(1600));
                    HoldWindowElapsed.TrySetResult(true);
                    // Coordinator finally releases this even when assertions fail.
                    // The fallback is bounded independently of coordinator health.
                    Release.Wait(TimeSpan.FromSeconds(2));
                    HeldMs = hold.Elapsed.TotalMilliseconds;
                }
                else
                    HoldWindowElapsed.TrySetResult(true);
                Returned.TrySetResult(true);
                return _claimed ? 1 : Next(code, message, pointer);
            }
            catch (Exception error)
            {
                _failure = error;
                Activated.TrySetException(error);
                HoldWindowElapsed.TrySetException(error);
                Returned.TrySetException(error);
                return _claimed ? 1 : Next(code, message, pointer);
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UnhookWindowsHookEx(nint hook);

        private nint Next(int code, nint message, nint pointer) => Native.CallNextHookEx(_hook, code, message, pointer);

        internal async Task<bool> Ping(CancellationToken token)
        {
            if (!Native.PostThreadMessageW(_threadId, PingMessage, 0, 0))
                return false;
            return await _ping.Task.WaitAsync(token);
        }

        public void Dispose()
        {
            Volatile.Write(ref _stopping, 1);
            Release.Set();
            if (_threadId != 0)
                Native.PostThreadMessageW(_threadId, 0x12, 0, 0);
            if (!_thread.Join(TimeSpan.FromSeconds(6)))
                throw new TimeoutException("Test-owned hook did not stop; diagnostic cleanup failed.");
            Release.Dispose();
            if (_failure is not null)
                throw new InvalidOperationException("Test-owned hook failed.", _failure);
        }
    }
}
