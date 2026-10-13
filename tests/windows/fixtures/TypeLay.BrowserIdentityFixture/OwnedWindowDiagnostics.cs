using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace TypeLay.BrowserIdentityFixture;

// One bounded observation late in the existing browser stage, never a readiness gate.
internal static class OwnedWindowDiagnostics
{
    internal sealed class Result
    {
        public string State { get; set; } = "not-started";
        public bool WindowLimitReached
        {
            get; set;
        }
        public int EnumerationError
        {
            get; set;
        }
        public List<Window> Windows { get; } = [];
    }

    internal sealed class Window
    {
        public int Index
        {
            get; set;
        }
        public uint ProcessId
        {
            get; set;
        }
        public bool Visible
        {
            get; set;
        }
        public bool Enabled
        {
            get; set;
        }
        public bool OwnerOwned
        {
            get; set;
        }
        public bool? OwnerEnabled
        {
            get; set;
        }
        // An enabled owned popup above a disabled owner is evidence, not proof of modality.
        public bool ModalCandidate
        {
            get; set;
        }
        public string Response { get; set; } = "not-probed";
        public int ResponseError
        {
            get; set;
        }
        public string Screenshot { get; set; } = "not-attempted";
        public string? ScreenshotFile
        {
            get; set;
        }
        public string? ScreenshotError
        {
            get; set;
        }
        public int? ScreenshotCleanupError
        {
            get; set;
        }
    }

    private sealed record HeldWindow(nint Handle, uint ThreadId, uint ProcessId, SafeProcessHandle Process, bool VisibleAtEnumeration) : IDisposable
    {
        public void Dispose() => Process.Dispose();
    }

    // Closing this short-query gate is a cleanup barrier, unlike checking a token.
    // PrintWindow itself must never hold it because that API can block indefinitely.
    private sealed class QueryGate(long stageEnds) : IDisposable
    {
        private readonly object _sync = new();
        private bool _closed;
        internal T Run<T>(Func<T> query, CancellationToken token)
        {
            lock (_sync)
            {
                if (_closed)
                    throw new OperationCanceledException();
                CheckDeadline(token, stageEnds);
                return query();
            }
        }
        public void Dispose()
        {
            lock (_sync)
                _closed = true;
        }
    }

    internal static async Task<Result> ObserveAsync(OwnedProcess browser, string output, long stageEnds, CancellationToken stage)
    {
        var result = new Result();
        var held = new List<HeldWindow>();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(31), stage);
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(stage);
            budget.CancelAfter(TimeSpan.FromSeconds(3));
            CancellationToken token = budget.Token;
            CheckDeadline(token, stageEnds);
            using var job = new SafeFileHandle(browser.DuplicateJobHandle(), ownsHandle: true);
            result.State = "observing";
            Marshal.SetLastPInvokeError(0);
            bool enumerated = EnumWindows((window, _) =>
            {
                if (token.IsCancellationRequested || Stopwatch.GetTimestamp() >= stageEnds)
                    return false;
                HeldWindow? candidate = TryHold(window, job, stageEnds, token);
                if (candidate is not null)
                {
                    if (held.Count < 2)
                        held.Add(candidate);
                    else
                    {
                        int hidden = held.FindIndex(value => !value.VisibleAtEnumeration);
                        if (candidate.VisibleAtEnumeration && hidden >= 0)
                        {
                            held[hidden].Dispose();
                            held[hidden] = candidate;
                        }
                        else
                            candidate.Dispose();
                    }
                }
                if (held.Count < 2 || held.Any(value => !value.VisibleAtEnumeration))
                    return true;
                result.WindowLimitReached = true;
                return false;
            }, 0);
            if (!enumerated && !result.WindowLimitReached && !token.IsCancellationRequested && Stopwatch.GetTimestamp() < stageEnds)
            {
                result.EnumerationError = Marshal.GetLastWin32Error();
                result.State = "enumeration-failed";
                return result;
            }
            CheckDeadline(token, stageEnds);
            int captures = 0;
            foreach (HeldWindow window in held)
            {
                CheckDeadline(token, stageEnds);
                if (!Matches(window.Handle, window.ThreadId, window.ProcessId, window.Process, job, stageEnds, token))
                    continue;
                var observation = new Window
                {
                    Index = result.Windows.Count,
                    ProcessId = window.ProcessId,
                    Visible = IsWindowVisible(window.Handle),
                    Enabled = IsWindowEnabled(window.Handle)
                };
                result.Windows.Add(observation);
                nint ownerHandle = GetWindow(window.Handle, 4); // GW_OWNER, no title or UI text.
                using HeldWindow? owner = TryHold(ownerHandle, job, stageEnds, token);
                if (owner is not null && Matches(owner.Handle, owner.ThreadId, owner.ProcessId, owner.Process, job, stageEnds, token))
                {
                    observation.OwnerOwned = true;
                    observation.OwnerEnabled = IsWindowEnabled(owner.Handle);
                    observation.ModalCandidate = observation.Visible && observation.Enabled && !observation.OwnerEnabled.Value;
                }
                CheckDeadline(token, stageEnds);
                if (!Matches(window.Handle, window.ThreadId, window.ProcessId, window.Process, job, stageEnds, token))
                {
                    observation.Response = "identity-changed";
                    continue;
                }
                uint responseBudget = (uint)Math.Clamp((stageEnds - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency, 0, 200);
                if (responseBudget == 0)
                    throw new OperationCanceledException();
                Marshal.SetLastPInvokeError(0);
                nint responded = SendMessageTimeoutW(window.Handle, 0, 0, 0, 0x23, responseBudget, out _);
                observation.ResponseError = Marshal.GetLastWin32Error();
                CheckDeadline(token, stageEnds);
                observation.Response = Matches(window.Handle, window.ThreadId, window.ProcessId, window.Process, job, stageEnds, token)
                    ? responded != 0 ? "responded" : "unavailable-or-timeout"
                    : "identity-changed";
                if (!observation.Visible || captures == 2 || observation.Response == "identity-changed")
                    continue;
                captures++;
                using var captureBudget = CancellationTokenSource.CreateLinkedTokenSource(token);
                using var queryGate = new QueryGate(stageEnds);
                captureBudget.CancelAfter(TimeSpan.FromSeconds(1));
                CancellationToken captureToken = captureBudget.Token;
                // The worker owns duplicated handles. Parent cleanup cannot turn job=0 into
                // an any-job query or invalidate its exact process identity while it returns.
                var workerProcess = new SafeProcessHandle(Duplicate(window.Process.DangerousGetHandle()), true);
                SafeFileHandle workerJob;
                try
                {
                    workerJob = new SafeFileHandle(Duplicate(job.DangerousGetHandle()), true);
                }
                catch { workerProcess.Dispose(); throw; }
                observation.Screenshot = "capturing";
                Task<byte[]?> capture = Task.Run(() =>
                {
                    using (workerProcess)
                    using (workerJob)
                        return Capture(window, workerProcess, workerJob, queryGate, stageEnds, captureToken);
                });
                byte[]? bytes;
                try
                {
                    bytes = await capture.WaitAsync(captureBudget.Token);
                }
                catch (OperationCanceledException)
                {
                    observation.Screenshot = token.IsCancellationRequested ? "cancelled" : "timeout";
                    // Observe exceptions from late native work, but never publish its bytes.
                    _ = capture.ContinueWith(task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                    CheckDeadline(token, stageEnds);
                    continue;
                }
                if (bytes is null)
                {
                    observation.Screenshot = "unavailable";
                    continue;
                }
                CheckDeadline(token, stageEnds);
                if (!Matches(window.Handle, window.ThreadId, window.ProcessId, window.Process, job, stageEnds, token))
                {
                    observation.Screenshot = "identity-changed";
                    continue;
                }
                string name = Path.GetFileNameWithoutExtension(output) + ".owned-window-" + observation.Index + ".bmp";
                string path = Path.Combine(Path.GetDirectoryName(output)!, name);
                CheckDeadline(token, stageEnds);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                CheckDeadline(token, stageEnds);
                bool created = false;
                observation.Screenshot = "writing";
                try
                {
                    await using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                    {
                        created = true;
                        observation.ScreenshotFile = name;
                        CheckDeadline(token, stageEnds);
                        await stream.WriteAsync(bytes, token);
                        CheckDeadline(token, stageEnds);
                        await stream.FlushAsync(token);
                    }
                    CheckDeadline(token, stageEnds);
                    observation.Screenshot = "saved";
                    observation.ScreenshotFile = name;
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    observation.Screenshot = error is OperationCanceledException ? "cancelled" : "write-failed";
                    observation.ScreenshotError = error.GetType().Name + ":" + error.HResult.ToString("X8");
                    if (created)
                    {
                        try
                        {
                            File.Delete(path); // Only this observer's newly created partial sidecar.
                            observation.ScreenshotFile = null;
                        }
                        catch (Exception cleanupError) when (cleanupError is not OutOfMemoryException)
                        {
                            observation.Screenshot = "partial-cleanup-failed";
                            observation.ScreenshotCleanupError = cleanupError.HResult;
                        }
                    }
                    throw;
                }
            }
            result.State = "completed";
        }
        catch (OperationCanceledException) { result.State = "cancelled-or-budget-expired"; }
        catch (Exception error) when (error is not OutOfMemoryException) { result.State = "error:" + error.GetType().Name; }
        finally { foreach (HeldWindow window in held) window.Dispose(); }
        return result;
    }

    private static HeldWindow? TryHold(nint window, SafeFileHandle job, long stageEnds, CancellationToken token)
    {
        if (window == 0 || token.IsCancellationRequested || Stopwatch.GetTimestamp() >= stageEnds)
            return null;
        uint thread = Windows.GetWindowThreadProcessId(window, out uint pid);
        if (thread == 0 || pid == 0)
            return null;
        SafeProcessHandle? process = OpenProcess(0x00101000, false, pid); // SYNCHRONIZE | QUERY_LIMITED_INFORMATION
        try
        {
            if (process.IsInvalid || !Windows.IsProcessInJob(process.DangerousGetHandle(), job.DangerousGetHandle(), out bool owned) || !owned ||
                WaitForSingleObject(process, 0) != 258 || token.IsCancellationRequested || Stopwatch.GetTimestamp() >= stageEnds)
                return null;
            if (!Windows.IsWindow(window) || Windows.GetWindowThreadProcessId(window, out uint livePid) != thread || livePid != pid)
                return null;
            var held = new HeldWindow(window, thread, pid, process, IsWindowVisible(window));
            process = null;
            return held;
        }
        finally { process?.Dispose(); }
    }

    private static void CheckDeadline(CancellationToken token, long stageEnds)
    {
        token.ThrowIfCancellationRequested();
        if (Stopwatch.GetTimestamp() >= stageEnds)
            throw new OperationCanceledException();
    }

    private static bool Matches(nint window, uint thread, uint pid, SafeProcessHandle process, SafeFileHandle job, long stageEnds, CancellationToken token)
    {
        CheckDeadline(token, stageEnds);
        return !job.IsInvalid && !job.IsClosed && WaitForSingleObject(process, 0) == 258 &&
            Windows.IsProcessInJob(process.DangerousGetHandle(), job.DangerousGetHandle(), out bool owned) && owned &&
            Windows.IsWindow(window) && Windows.GetWindowThreadProcessId(window, out uint currentPid) == thread && currentPid == pid;
    }

    private static byte[]? Capture(HeldWindow window, SafeProcessHandle process, SafeFileHandle job, QueryGate queries, long stageEnds, CancellationToken token)
    {
        nint dc = 0, bitmap = 0, previous = 0;
        CheckDeadline(token, stageEnds);
        nint previousDpi = SetThreadDpiAwarenessContext(-4);
        try
        {
            if (previousDpi == 0)
                return null;
            Rect? bounds = queries.Run(() =>
                Matches(window.Handle, window.ThreadId, window.ProcessId, process, job, stageEnds, token) && GetWindowRect(window.Handle, out Rect value)
                    ? (Rect?)value : null, token);
            if (bounds is not Rect rect)
                return null;
            int width = rect.Right - rect.Left, height = rect.Bottom - rect.Top;
            if (width <= 0 || height <= 0 || width > 4096 || height > 4096 || (long)width * height > 4_000_000)
                return null;
            var info = new BitmapInfo { Size = 40, Width = width, Height = -height, Planes = 1, Bits = 32 };
            dc = CreateCompatibleDC(0); // Memory DC only. Never copies desktop pixels.
            bitmap = CreateDIBSection(0, ref info, 0, out nint pixels, 0, 0);
            if (dc == 0 || bitmap == 0 || pixels == 0)
                return null;
            previous = SelectObject(dc, bitmap);
            if (previous == 0 || previous == -1 || !queries.Run(() => Matches(window.Handle, window.ThreadId, window.ProcessId, process, job, stageEnds, token), token))
                return null;
            CheckDeadline(token, stageEnds);
            bool printed = PrintWindow(window.Handle, dc, 2);
            // Cancellation is checked before any post-capture HWND access. A late task
            // only releases its private GDI/duplicated handles; it never writes a file.
            CheckDeadline(token, stageEnds);
            if (!printed || !queries.Run(() => Matches(window.Handle, window.ThreadId, window.ProcessId, process, job, stageEnds, token), token))
                return null;
            byte[] bytes = new byte[54 + checked(width * height * 4)];
            bytes[0] = (byte)'B';
            bytes[1] = (byte)'M';
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), 54);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), width);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), -height);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26), 1);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(28), 32);
            Marshal.Copy(pixels, bytes, 54, bytes.Length - 54);
            CheckDeadline(token, stageEnds);
            return bytes;
        }
        finally
        {
            if (previous != 0 && previous != -1)
                SelectObject(dc, previous);
            if (bitmap != 0)
                DeleteObject(bitmap);
            if (dc != 0)
                DeleteDC(dc);
            if (previousDpi != 0)
                SetThreadDpiAwarenessContext(previousDpi);
        }
    }

    private static nint Duplicate(nint handle)
    {
        if (!DuplicateHandle(-1, handle, -1, out nint copy, 0, false, 2))
            throw new Win32Exception();
        return copy;
    }

    private delegate bool EnumCallback(nint window, nint parameter);
    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size;
        public int Width, Height;
        public ushort Planes, Bits;
        public uint Compression, ImageSize;
        public int XResolution, YResolution;
        public uint UsedColors, ImportantColors;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(EnumCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint window, uint command);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SendMessageTimeoutW(nint window, uint message, nuint wparam, nint lparam, uint flags, uint timeout, out nuint result);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Rect rect);
    [DllImport("user32.dll")] private static extern bool PrintWindow(nint window, nint dc, uint flags);
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateDIBSection(nint dc, ref BitmapInfo info, uint usage, out nint bits, nint section, uint offset);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DuplicateHandle(nint sourceProcess, nint source, nint targetProcess, out nint target, uint access, bool inherit, uint options);
}
