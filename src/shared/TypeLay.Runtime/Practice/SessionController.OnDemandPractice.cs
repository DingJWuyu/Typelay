using TypeLay.Core;

namespace TypeLay.Runtime;

public sealed partial class SessionController
{
    private sealed class OnDemandPracticeCoordinator(SessionController owner)
    {
        internal OnDemandPracticeProgress? _practiceWakeCommand, _pendingDirectPractice;
        private Guid _lastRetiredPracticeRun;
        private long _practiceBoundAt, _practicePreviewAt;
        public OnDemandPracticeProgress? PracticeProgress
        {
            get; private set;
        }
        public event Action<OnDemandPracticeProgress?>? PracticeProgressChanged;

        // Hosts call Begin only for an explicit new/restarted owned practice window.
        // Binding must come from that host's native identity verification, before the
        // command is consumed. Neither registration nor progress changes write safety.
        public bool BeginOnDemandPractice(Guid runId)
        {
            if (owner._disposed || runId == Guid.Empty || runId == _lastRetiredPracticeRun
                || !owner._settings.Value.Enabled || owner._settings.Value.Mode != WorkMode.OnDemand)
                return false;
            if (PracticeProgress?.RunId == runId)
                return true;
            if (PracticeProgress is { } previous)
                _lastRetiredPracticeRun = previous.RunId;
            _practiceWakeCommand = null;
            PracticeProgress = new(runId, null, owner._hook.CommandGeneration, 0, owner._commandFeedbackRevision, false, false);
            PracticeProgressChanged?.Invoke(PracticeProgress);
            return true;
        }
        public bool BindOnDemandPractice(Guid runId, TargetId target)
        {
            if (owner._disposed || PracticeProgress is not { } progress || progress.RunId != runId
                || progress.CommandGeneration != owner._hook.CommandGeneration
                || string.IsNullOrWhiteSpace(target.Backend) || string.IsNullOrWhiteSpace(target.Value)
                || target.Lifetime == Guid.Empty)
                return false;
            if (progress.Target is { } existing)
                return existing == target;
            _practiceBoundAt = owner._loop.NowMs;
            PracticeProgress = progress with
            {
                Target = target
            };
            PracticeProgressChanged?.Invoke(PracticeProgress);
            return true;
        }
        public void ObserveOnDemandPreviewReadiness(Guid runId, TargetId? target, Guid commandGeneration, bool ready)
        {
            if (owner._disposed || PracticeProgress is not { } progress || progress.RunId != runId
                || progress.Target != target || progress.CommandGeneration != commandGeneration
                || ready && (commandGeneration != owner._hook.CommandGeneration || target is null)
                || progress.PreviewReady == ready)
                return;
            PracticeProgress = progress with
            {
                PreviewReady = ready
            };
            PracticeProgressChanged?.Invoke(PracticeProgress);
        }
        public void ObserveOnDemandDirectReadiness(Guid runId, TargetId? target, Guid commandGeneration, bool ready)
        {
            if (owner._disposed || PracticeProgress is not { } progress || progress.RunId != runId
                || progress.Target != target || progress.CommandGeneration != commandGeneration
                || ready && (commandGeneration != owner._hook.CommandGeneration || target is null)
                || progress.DirectReady == ready)
                return;
            PracticeProgress = progress with
            {
                DirectReady = ready
            };
            PracticeProgressChanged?.Invoke(PracticeProgress);
        }
        public bool EndOnDemandPractice(Guid runId)
        {
            if (PracticeProgress?.RunId != runId)
                return false;
            _lastRetiredPracticeRun = runId;
            _practiceWakeCommand = null;
            PracticeProgress = null;
            PracticeProgressChanged?.Invoke(null);
            return true;
        }
        internal OnDemandPracticeProgress? PracticeCommand(InputNotice notice, bool direct)
        {
            // AcceptedAtMs is the existing adapter acceptance timestamp, not proof of
            // a physical key-down. Ties are conservative: queued earlier commands
            // cannot become practice evidence just because they were drained later.
            if (PracticeProgress is not { Target: not null } progress || notice.Source?.Target != progress.Target
                || notice.CommandGeneration == Guid.Empty || notice.CommandGeneration != progress.CommandGeneration
                || notice.CommandSequence <= 0
                || notice.AcceptedAtMs <= _practiceBoundAt || notice.AcceptedAtMs > owner._loop.NowMs
                || direct && (!progress.PreviewCompleted || progress.DirectReplacementCompleted
                    || notice.AcceptedAtMs <= _practicePreviewAt))
                return null;
            return progress with
            {
                CommandSequence = notice.CommandSequence,
                Revision = owner._commandFeedbackRevision
            };
        }
        internal void CompletePracticeCommand(OnDemandPracticeProgress? command, bool direct)
        {
            // Begin publishes the current command revision as a watermark. Even a
            // reused host run ID cannot adopt work accepted before that registration.
            if (command is null || PracticeProgress is not { } progress || progress.RunId != command.RunId
                || progress.Target != command.Target || command.CommandGeneration != owner._hook.CommandGeneration
                || command.Revision != owner._commandFeedbackRevision || command.Revision <= progress.Revision
                || direct && (!command.PreviewCompleted || !progress.PreviewCompleted || progress.DirectReplacementCompleted)
                || !direct && progress.PreviewCompleted)
                return;
            if (!direct)
                _practicePreviewAt = owner._loop.NowMs;
            PracticeProgress = command with
            {
                PreviewCompleted = true,
                DirectReplacementCompleted = direct,
                PreviewReady = progress.PreviewReady,
                DirectReady = progress.DirectReady
            };
            PracticeProgressChanged?.Invoke(PracticeProgress);
        }
    }
}
