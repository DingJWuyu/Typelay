using TypeLay.Windows;
using TypeLay.Windows.Contracts;
using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using TypeLay.Core;

namespace TypeLay.Platform.Windows;

// Fixture registrations are developer-test capabilities, NOT a general website/app allowlist.
public sealed record FixtureField(string Name, long Hwnd, FieldAccess Classification, int LifetimeCookie);
public sealed record FixtureDescriptor(int ProcessId, long RootHwnd, long SubmitButtonHwnd,
    Guid Lifetime, FixtureField[] Fields);

/// <summary>
/// Controlled Win32 backend for fixture contracts and keyboard-hook regressions.
/// Only registered native EDIT HWNDs in the supplied fixture process are accessible.
/// This test backend does not implement production UIA discovery or external IME observation.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NativeEditAdapter
{
    private readonly Process _process;
    private readonly FixtureDescriptor _fixture;
    private readonly long _processStart;
    private readonly Dictionary<long, FixtureField> _fields;
    private TargetId? _quarantined;

    public NativeEditAdapter(Process launchedFixture, FixtureDescriptor fixture)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess)
            throw new PlatformNotSupportedException("Fixture backend requires Windows x64.");
        if (launchedFixture.Id != fixture.ProcessId || launchedFixture.HasExited)
            throw new ArgumentException("Fixture registration does not match the launched process.");
        _process = launchedFixture;
        _fixture = fixture;
        _processStart = launchedFixture.StartTime.ToUniversalTime().Ticks;
        _fields = fixture.Fields.ToDictionary(f => f.Hwnd);
    }

    private bool Registered(nint hwnd, out FixtureField? field)
    {
        field = null;
        if (_process.HasExited || _process.StartTime.ToUniversalTime().Ticks != _processStart
            || !_fields.TryGetValue((long)hwnd, out field) || !NativeMethods.IsWindow(hwnd))
            return false;
        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid != _fixture.ProcessId || NativeMethods.GetAncestor(hwnd, 2) != (nint)_fixture.RootHwnd
            || NativeMethods.GetProp(hwnd, NativeMethods.LifetimeProperty) != (nint)field.LifetimeCookie)
            return false;
        var name = new StringBuilder(128);
        NativeMethods.GetClassName(hwnd, name, name.Capacity);
        return name.ToString() == "Edit";
    }

    private static FieldAccess Classify(nint hwnd, FixtureField field)
    {
        int style = NativeMethods.GetStyle(hwnd, -16);
        if ((style & 0x20) != 0)
            return FieldAccess.Password;
        if ((style & 0x800) != 0)
            return FieldAccess.ReadOnly;
        if (!NativeMethods.IsWindowEnabled(hwnd))
            return FieldAccess.Disabled;
        return field.Classification; // Unclassified is NOT assumed safe.
    }

    private static ImeState GetFixtureIme(nint hwnd) => (long)NativeMethods.GetProp(hwnd, NativeMethods.ImeProperty) switch
    {
        1 => ImeState.Clear,
        2 => ImeState.Composing,
        _ => ImeState.Unknown
    };

    public InputSnapshot? Capture()
    {
        try
        {
            var focus = NativeMethods.Focused();
            if (focus.Root != (nint)_fixture.RootHwnd || !Registered(focus.Control, out FixtureField? found)
                || found is null)
                return null;
            FixtureField field = found;
            var target = new WindowsTarget(_fixture.ProcessId, _processStart, _fixture.RootHwnd,
                field.Hwnd, "native-edit-fixture", field.LifetimeCookie.ToString(), _fixture.Lifetime);
            NativeMethods.GetWindowRect(focus.Control, out NativeMethods.Rect rect);
            var bounds = new RectPx(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            FieldAccess access = Classify(focus.Control, field);
            if (!NativeMethods.IsWindowVisible(focus.Control) || NativeMethods.IsIconic(focus.Root))
                access = FieldAccess.Offscreen;
            bool multiline = (NativeMethods.GetStyle(focus.Control, -16) & 4) != 0;
            ImeState ime = GetFixtureIme(focus.Control);
            InputSnapshot Blocked() => new(target, "", null, bounds, multiline, access, ime,
                WindowsInterruptClock.NowMs, 0, 0, WriteSupported: true);
            // Never read a password / PIN / unknown / readonly field, even just for diagnostics.
            if (access != FieldAccess.Supported || ime != ImeState.Clear)
                return Blocked();
            string text = NativeMethods.ReadText(focus.Control, Limits.MaxTextUnits);
            TextRange selection = NativeMethods.ReadSelection(focus.Control);
            string again = NativeMethods.ReadText(focus.Control, Limits.MaxTextUnits);
            if (text != again || selection != NativeMethods.ReadSelection(focus.Control)
                || NativeMethods.Focused() != focus || !Registered(focus.Control, out _)
                || Classify(focus.Control, field) != FieldAccess.Supported
                || GetFixtureIme(focus.Control) != ImeState.Clear)
                return null;
            return new(target, text, selection, bounds, multiline, access, ime,
                WindowsInterruptClock.NowMs, 0, 0, WriteSupported: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        } // Do not serialize native exception messages or user text.
    }

    public WriteReceipt TryWrite(WritePlan plan)
    {
        bool mutationStarted = false;
        try
        {
            if (_quarantined == plan.Expected.Target)
                return new(WriteStatus.RejectedBeforeMutation, Denial.UnknownOutcome);
            InputSnapshot? fresh = Capture();
            if (fresh is null)
                return new(WriteStatus.RejectedBeforeMutation, Denial.TargetChanged);
            Denial reason = WriteSafety.Validate(plan, fresh, WindowsInterruptClock.NowMs);
            if (reason != Denial.None)
                return new(WriteStatus.RejectedBeforeMutation, reason);
            nint hwnd = (nint)fresh.Target.AsWindows().ControlHwnd;
            TextRange range = plan.Scope ?? new TextRange(0, fresh.Text.Length);
            mutationStarted = true; // A timed-out EM_SETSEL may already have changed the selection.
            NativeMethods.Send(hwnd, NativeMethods.EM_SETSEL, (nuint)range.Start, (nint)range.End);
            if (NativeMethods.Focused() != ((nint)fresh.Target.AsWindows().RootHwnd, hwnd)
                || !Registered(hwnd, out FixtureField? field) || field is null
                || Classify(hwnd, field) != FieldAccess.Supported || GetFixtureIme(hwnd) != ImeState.Clear
                || NativeMethods.ReadText(hwnd, Limits.MaxTextUnits) != fresh.Text
                || NativeMethods.ReadSelection(hwnd) != range)
                return Unknown(plan);
            // Directed replacement only. Never Enter, clipboard, Ctrl+A/C/V, or synthetic typing.
            NativeMethods.SendString(hwnd, NativeMethods.EM_REPLACESEL, plan.Replacement, 1);
            InputSnapshot? after = Capture();
            if (after is null || !after.IsUsable || after.Target != fresh.Target || after.Text != plan.ResultText
                || after.Selection != new TextRange(plan.Caret, plan.Caret))
                return Unknown(plan);
            return new(WriteStatus.Verified, Denial.None, after);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return mutationStarted ? Unknown(plan)
                : new(WriteStatus.RejectedBeforeMutation, Denial.PlatformFailure);
        }
    }

    private WriteReceipt Unknown(WritePlan plan)
    {
        _quarantined = plan.Expected.Target;
        return new(WriteStatus.OutcomeUnknown, Denial.UnknownOutcome);
    }
}
