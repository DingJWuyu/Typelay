namespace TypeLay.App;

internal sealed partial class KeyboardHook
{
    private readonly EditorForegroundDelivery? _editorForegroundDelivery;

    // Opt-in setup observation only, never current focus or input authority.
    // Completion identifies one original exact-editor foreground event whose
    // callback stack has finished. Later events still revoke every normal owner.
    internal Task<long>? EditorForegroundDelivered => _editorForegroundDelivery?.Completion;

    private sealed class EditorForegroundDelivery(nint editor)
    {
        private readonly TaskCompletionSource<long> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _depth;
        private long _sequence;
        private bool _matches, _failed;
        public Task<long> Completion => _completion.Task;

        // Only the registering hook thread mutates these fields. WinEvent can
        // reenter it: an inner matching event cannot publish while an earlier
        // outer callback still has original invalidation work to perform.
        public void Enter(uint ev, nint window)
        {
            _depth++;
            if (ev == 3)
            {
                _sequence++;
                _matches = window == editor;
            }
        }
        public void Leave(bool handled)
        {
            _failed |= !handled;
            if (--_depth == 0 && !_failed && _matches && _sequence > 0)
                _completion.TrySetResult(_sequence);
        }
    }
}
