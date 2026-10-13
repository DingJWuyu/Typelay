using TypeLay.Windows;
using TypeLay.Windows.Contracts;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using TypeLay.Core;

namespace TypeLay.Broker;

// Owns bounded native bindings, never source text. The actual compare/replacement
// is performed by the stock Edit's GUI thread, not by this cross-process caller.
internal sealed class NativeEditTransactions : IDisposable
{
    public const string Provider = "win32-edit-transaction-v1";
    private const int HeaderSize = 88, BufferBytes = (Limits.MaxTextUnits + 1) * 2, MappingSize = 49248;
    private readonly Dictionary<long, Binding> _bindings = new();
    private readonly Dictionary<uint, nint> _hooks = new();
    private nint _library, _procedure;
    private uint _message;
    private sealed record Binding(WindowsTarget Original, WindowsTarget Target, ulong Cookie, uint Thread, long TouchedAt, bool Initialized = false);
    private sealed record Result(uint Status, Denial Reason, string Text, TextRange Selection, bool Multiline, ImeState Ime);

    public InputSnapshot? Capture(WindowsTarget identity, RectPx bounds, long inputEpoch, long focusEpoch)
    {
        Prune();
        var binding = Bind(identity);
        if (binding is null)
            return null;
        // Only the first capture may install a cookie. Subsequent observations
        // cannot resurrect an old identity on a newly created/reused HWND.
        _bindings[identity.ControlHwnd] = binding with
        {
            Initialized = true
        };
        var result = Invoke(binding, binding.Initialized ? 4u : 1u);
        if (result.Reason == Denial.TargetChanged)
        {
            Release(binding);
            return null;
        }
        if (result.Status != 1 || result.Ime != ImeState.Clear || !result.Selection.IsValid(result.Text))
            return null;
        return new(binding.Target, result.Text, result.Selection, bounds, result.Multiline,
            FieldAccess.Supported, result.Ime, WindowsInterruptClock.NowMs, inputEpoch, focusEpoch,
            WriteSupported: true, DirectWriteSupported: true);
    }

    public WriteReceipt Write(WritePlan plan)
    {
        if (!_bindings.TryGetValue(plan.Expected.Target.AsWindows().ControlHwnd, out var binding)
            || binding.Target != plan.Expected.Target)
            return new(WriteStatus.RejectedBeforeMutation, Denial.TargetChanged);
        using var process = Process.GetProcessById(binding.Target.ProcessId);
        if (process.StartTime.ToUniversalTime().Ticks != binding.Target.ProcessStartUtcTicks)
            return new(WriteStatus.RejectedBeforeMutation, Denial.TargetChanged);
        var result = Invoke(binding, 2, plan);
        if (result.Status == 2)
            return new(WriteStatus.RejectedBeforeMutation, result.Reason);
        if (result.Status != 1 || result.Text != plan.ResultText || result.Selection != new TextRange(plan.Caret, plan.Caret))
            return new(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome);
        return new(WriteStatus.Verified, Denial.None, plan.Expected with
        {
            Text = result.Text,
            Selection = result.Selection,
            ObservedAtMs = WindowsInterruptClock.NowMs,
            Ime = result.Ime
        });
    }

    private Binding? Bind(WindowsTarget identity)
    {
        if (!Environment.Is64BitProcess || identity.ControlHwnd == 0)
            return null;
        if (_bindings.TryGetValue(identity.ControlHwnd, out var existing))
        {
            if (existing.Original with
            {
                Lifetime = identity.Lifetime
            } == identity)
            {
                existing = existing with
                {
                    TouchedAt = WindowsInterruptClock.NowMs
                };
                _bindings[identity.ControlHwnd] = existing;
                return existing;
            }
            Release(existing);
        }
        if (_bindings.Count >= 32)
            return null;
        var name = new StringBuilder(32);
        if (GetClassNameW((nint)identity.ControlHwnd, name, name.Capacity) != 4 || name.ToString() != "Edit")
            return null;
        using var process = Process.GetProcessById(identity.ProcessId);
        if (!IsWow64Process2(process.Handle, out ushort machine, out ushort nativeMachine)
            || nativeMachine != 0x8664 || machine != 0)
            return null;
        uint thread = GetWindowThreadProcessId((nint)identity.ControlHwnd, out uint pid);
        if (thread == 0 || pid != identity.ProcessId)
            return null;
        if (_library == 0)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "TypeLay.NativeInput.dll");
            if (!File.Exists(path))
                return null;
            _library = NativeLibrary.Load(path);
            _procedure = NativeLibrary.GetExport(_library, "TypeLayCallWndProc");
            _message = RegisterWindowMessageW("TypeLay.NativeInput.Transaction.v1");
        }
        if (_message == 0)
            return null;
        if (!_hooks.ContainsKey(thread))
        {
            nint hook = SetWindowsHookExW(4, _procedure, _library, thread);
            if (hook == 0)
                return null;
            _hooks.Add(thread, hook);
        }
        ulong cookie = unchecked((ulong)(long)GetPropW((nint)identity.ControlHwnd, "TypeLay.NativeInput.Lifetime.v1"));
        if (cookie == 0)
            cookie = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) | 1;
        // Every new binding gets a distinct identity, including after broker
        // restart, expiry or an interrupted initial capture.
        var target = identity with
        {
            Provider = Provider,
            Lifetime = Guid.NewGuid()
        };
        var binding = new Binding(identity, target, cookie, thread, WindowsInterruptClock.NowMs);
        _bindings.Add(identity.ControlHwnd, binding);
        return binding;
    }

    private Result Invoke(Binding binding, uint operation, WritePlan? plan = null)
    {
        byte[] nonce = RandomNumberGenerator.GetBytes(16);
        ulong first = BitConverter.ToUInt64(nonce, 0), second = BitConverter.ToUInt64(nonce, 8);
        string name = $"Local\\TypeLay.Input.{first:x16}{second:x16}";
        using var mapping = MemoryMappedFile.CreateNew(name, MappingSize, MemoryMappedFileAccess.ReadWrite);
        using var view = mapping.CreateViewAccessor(0, MappingSize, MemoryMappedFileAccess.ReadWrite);
        view.Write(0, 1u);
        view.Write(12, operation);
        view.Write(16, (uint)binding.Target.ProcessId);
        view.Write(20, binding.Thread);
        view.Write(24, binding.Target.ControlHwnd);
        view.Write(32, binding.Target.RootHwnd);
        view.Write(40, binding.Cookie);
        // Private native-handler budget: both ends explicitly use GetTickCount64.
        // This field never carries an App/Broker authorization timestamp.
        view.Write(48, GetTickCount64() + 500UL);
        if (plan is not null)
        {
            view.Write(56, plan.Expected.Text.Length);
            view.Write(60, plan.Scope?.Start ?? 0);
            view.Write(64, plan.Scope?.End ?? plan.Expected.Text.Length);
            view.Write(68, plan.Replacement.Length);
            WriteText(view, HeaderSize, plan.Expected.Text);
            WriteText(view, HeaderSize + BufferBytes, plan.Replacement);
        }
        // A timeout after dispatch is an unknown write outcome, never a safe retry.
        nint sent = SendMessageTimeoutW((nint)binding.Target.ControlHwnd, _message, (nuint)first,
            unchecked((nint)second), 2 | 0x20, 700, out _);
        if (sent == 0)
            return new(0, Denial.PlatformFailure, "", new(0, 0), false, ImeState.Unknown);
        uint status = view.ReadUInt32(4);
        Denial reason = (Denial)view.ReadUInt32(8);
        int length = view.ReadInt32(56);
        string text = "";
        if (status == 1 && operation != 3 && length is >= 0 and <= Limits.MaxTextUnits)
        {
            byte[] bytes = new byte[length * 2];
            view.ReadArray(HeaderSize + 2 * BufferBytes, bytes, 0, bytes.Length);
            text = Encoding.Unicode.GetString(bytes);
            CryptographicOperations.ZeroMemory(bytes);
        }
        var result = new Result(status, Enum.IsDefined(reason) ? reason : Denial.PlatformFailure, text,
            new(view.ReadInt32(72), view.ReadInt32(76)), view.ReadInt32(80) != 0, (ImeState)view.ReadInt32(84));
        // Only clear after a completed synchronous exchange. On timeout the native
        // callback may still own a view; its deadline and single-use status apply.
        byte[] zero = new byte[MappingSize];
        view.WriteArray(0, zero, 0, zero.Length);
        return result;
    }

    private static void WriteText(MemoryMappedViewAccessor view, long offset, string text)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(text);
        view.WriteArray(offset, bytes, 0, bytes.Length);
        CryptographicOperations.ZeroMemory(bytes);
    }
    private void Prune()
    {
        long before = WindowsInterruptClock.NowMs - 240_000;
        foreach (var binding in _bindings.Values.Where(value => value.TouchedAt < before).ToArray())
            Release(binding);
    }
    private void Release(Binding binding)
    {
        try
        {
            Invoke(binding, 3);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        _bindings.Remove(binding.Target.ControlHwnd);
        if (!_bindings.Values.Any(value => value.Thread == binding.Thread) && _hooks.Remove(binding.Thread, out nint hook))
            UnhookWindowsHookEx(hook);
    }
    public void Clear()
    {
        foreach (var binding in _bindings.Values.ToArray())
            Release(binding);
    }
    public void Dispose()
    {
        Clear();
        if (_library != 0)
            NativeLibrary.Free(_library);
        _library = 0;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint GetPropW(nint hwnd, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint hwnd, StringBuilder name, int count);
    [DllImport("kernel32.dll")] private static extern bool IsWow64Process2(nint process, out ushort processMachine, out ushort nativeMachine);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessageW(string name);
    [DllImport("user32.dll")] private static extern nint SetWindowsHookExW(int type, nint callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")]
    private static extern nint SendMessageTimeoutW(nint hwnd, uint message, nuint wp, nint lp,
        uint flags, uint timeout, out nuint result);
    [DllImport("kernel32.dll")] private static extern ulong GetTickCount64();
}
