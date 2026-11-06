using TypeLay.Core;

namespace TypeLay.Runtime;

public sealed partial class SessionController
{
    // A current editor/source interaction, never a model request or an already
    // submitted write. Each implementation owns its own concrete continuation.
    private interface ISessionInteraction
    {
        SessionViewToken? PendingReturn => null;
        InputContext CaptureContext(InputContext context) => context;
        bool BeforeObserve(InputCapture capture, InputContext request, long now) => false;
        bool AfterObserve(InputCapture capture, InputContext request, long now) => false;
        void Retire();
    }

    private ISessionInteraction? _interaction;
    // An already dispatched native return remains unsettled after its UI retires.
    private ISessionInteraction? _returnDispatch;

    private void RetireInteraction()
    {
        var previous = _interaction;
        _interaction = null;
        previous?.Retire();
    }

    private bool InteractionIsCurrent(ISessionInteraction operation) => !_disposed
        && ReferenceEquals(_interaction, operation);
    private sealed class SourceReturnOperation(SessionController owner, OwnedSourceSession.PendingEditorExit? exit) : ISessionInteraction
    {
        private SessionViewToken? Token;
        private bool InFlight;
        public SessionViewToken? PendingReturn => InFlight ? Token : null;
        public async Task<bool> RunAsync()
        {
            if (!owner.InteractionIsCurrent(this) || owner._overlay.EditingComposition || !owner._session.RequestSourceReturn() || owner._session.Current is not { } source)
            {
                if (owner.InteractionIsCurrent(this))
                    owner._interaction = null;
                return false;
            }
            long generation = ++owner._generation;
            var operation = new SessionViewToken(source.Target, owner._hook.CommandGeneration, generation);
            Token = operation;
            InFlight = true;
            owner._returnDispatch = this;
            owner.RevokeInput("return-from-editor");
            owner._permitUntil = 0;
            owner.Render();
            if (owner._disposed || generation != owner._generation || !owner.InteractionIsCurrent(this))
            {
                InFlight = false;
                if (ReferenceEquals(owner._returnDispatch, this))
                    owner._returnDispatch = null;
                return false;
            }
            var request = owner._ownedSource.BeginEditorReturn(operation);
            InputReturn result = new(false);
            try
            {
                result = await owner._broker.ReturnToSourceAsync(owner.Context() with
                {
                    PracticeReturn = request
                });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                result = new(false, ContextLost: true);
            }
            finally
            {
                InFlight = false;
                if (ReferenceEquals(owner._returnDispatch, this))
                    owner._returnDispatch = null;
            }
            if (!owner.InteractionIsCurrent(this) || generation != owner._generation)
                return false;
            if (result.ContextLost)
            {
                owner.StopSession("return-context-lost");
                return false;
            }
            if (result.Returned)
            {
                if (!owner._ownedSource.ConfirmEditorReturn(request, result, exit, generation))
                    return false;
                owner._nextCapture = 0;
                return true;
            }
            owner._interaction = null;
            owner._session.CancelSourceReturn();
            owner._overlay.ResumeEditor();
            owner.Render();
            return false;
        }
        public bool BeforeObserve(InputCapture capture, InputContext request, long now) => InFlight;
        public bool AfterObserve(InputCapture capture, InputContext request, long now)
        {
            if (owner.InteractionIsCurrent(this) && !InFlight)
                owner._interaction = null;
            return false;
        }
        public void Retire()
        {
            // Removing this exact current interaction cancels its continuation.
            // A dispatched return still owns its receipt until RunAsync settles.
        }
    }

    private void ChangeDraft(string text)
    {
        if (_editorRegenerate is { } operation)
        {
            operation.ChangeDraft(text);
            return;
        }
        _session.ChangeDraft(text);
        _ownedSource.ObserveDraftChanged(text);
    }
}
