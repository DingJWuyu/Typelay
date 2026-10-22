using TypeLay.Core;
using TypeLay.Windows;

namespace TypeLay.App;

internal sealed record BrowserInputEpochs(long User, long Source, long Foreground, long Window, long Content, Guid Generation);

internal sealed partial class KeyboardHook
{
    private bool _deferCommandRegistration;
    private volatile bool _nativeCommandsEnabled = true;
    private readonly bool[] _commandConsumed = new bool[256];
    private int _commandGroupReleased;
    private long _commandsReleasedAt;
    internal BrowserInputEpochs BrowserEpochs => new(UserEpoch, SourceUserEpoch, ForegroundEpoch, WindowEpoch, ContentEpoch, CommandGeneration);
    internal bool CommandsReleased => Volatile.Read(ref _commandGroupReleased) != 0;
    internal long CommandsReleasedAt => Interlocked.Read(ref _commandsReleasedAt);
    internal long NextCommandSequence() => Interlocked.Increment(ref _commandSequence);
    internal (string Wake, string Direct)? FailedCommandConfiguration => _hotkeys.FailedConfiguration;
    internal void DeferCommandRegistration()
    {
        _deferCommandRegistration = true;
        _nativeCommandsEnabled = false;
        Revoke();
    }
    // These methods run on the same UI thread that owns RegisterHotKey.
    internal bool SuspendCommandRegistration() => _hotkeys.Suspend();
    internal bool RestoreCommandRegistration(bool preserveNativeCommands = false)
    {
        if (!CommandsReleased)
            return false;
        bool restored = _hotkeys.Resume();
        var configuration = Volatile.Read(ref _shortcuts);
        Interlocked.Exchange(ref _shortcuts, configuration with
        {
            Bindings = _hotkeys.Bindings
        });
        // An initial partial registration already serves its successful chord.
        // A retry must not disable it, while a page handoff stays disabled until
        // both reservations have really returned.
        _nativeCommandsEnabled = restored || preserveNativeCommands && _nativeCommandsEnabled;
        if (restored)
            _deferCommandRegistration = false;
        return restored;
    }
    private void ObserveCommandRelease()
    {
        bool held = false;
        // Mouse button VKs are tracked by OnMouse, not this keyboard group.
        for (int key = 8; key < _keys.Length; key++)
            held |= _keys[key];
        if (held)
            Volatile.Write(ref _commandGroupReleased, 0);
        else
        {
            Interlocked.Exchange(ref _commandsReleasedAt, WindowsInterruptClock.NowMs);
            Volatile.Write(ref _commandGroupReleased, 1);
        }
    }
    internal void NotifyBrowserChange(bool content, bool focus, bool source = true)
    {
        Interlocked.Increment(ref _userEpoch);
        if (source)
            Interlocked.Increment(ref _sourceUserEpoch);
        if (content)
            Interlocked.Increment(ref _contentEpoch);
        if (focus)
            Interlocked.Increment(ref _foregroundEpoch);
        RevokeStockCandidate();
        _gate.Revoke();
    }
}
