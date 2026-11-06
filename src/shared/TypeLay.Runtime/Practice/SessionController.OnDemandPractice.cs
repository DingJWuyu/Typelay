using TypeLay.Core;

namespace TypeLay.Runtime;

public sealed partial class SessionController
{
    private sealed class OnDemandPracticeCoordinator(SessionController owner)
    {
        private OnDemandPracticeProgress? _practiceWakeCommand;
        private (Guid JobId, OnDemandPracticeProgress Command)? _pendingDirectCommand;
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
            RetireCommands();
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
            RetireCommands();
            PracticeProgress = null;
            PracticeProgressChanged?.Invoke(null);
            return true;
        }
        internal void RecordPreviewCommand(InputNotice notice)
        {
            _practiceWakeCommand = notice.Kind == "wake" ? PracticeCommand(notice, direct: false) : null;
        }

        internal OnDemandPracticeProgress? PreviewCommandFor(TranslationJob job) =>
            job.Kind == TranslationKind.FullOrSelection && _practiceWakeCommand?.Target == job.Basis.Target
                ? _practiceWakeCommand : null;

        internal void ObserveTranslatedPreview(OnDemandPracticeProgress? command, TranslationJob job, bool completed)
        {
            if (completed && command is not null && ReferenceEquals(command, _practiceWakeCommand)
                && job.Kind == TranslationKind.FullOrSelection && command.Target == job.Basis.Target
                && owner._hook.GetFocus(job.Basis.Target) != FocusMember.Other)
                CompletePracticeCommand(command, direct: false);
        }

        internal void RecordDirectCommand(InputNotice notice, TranslationJob job)
        {
            // Retain only provenance. The direct operation remains the sole
            // owner of its source body and detached execution lifetime.
            _pendingDirectCommand = notice.Kind == "direct" && notice.Source?.Target == job.Basis.Target
                && PracticeCommand(notice, direct: true) is { } command ? (job.Id, command) : null;
        }

        internal OnDemandPracticeProgress? TakeDirectCommand(TranslationJob job)
        {
            if (_pendingDirectCommand is not { } pending || pending.JobId != job.Id
                || pending.Command.Target != job.Basis.Target)
                return null;
            _pendingDirectCommand = null;
            return pending.Command;
        }

        internal void ObserveDirectReplacement(OnDemandPracticeProgress? command, WritePlan plan, WriteReceipt receipt)
        {
            if (command?.Target == plan.Expected.Target
                && receipt.Status == WriteStatus.Verified && receipt.Reason == Denial.None
                && receipt.VerifiedSnapshot is { IsUsable: true } after && after.Target == plan.Expected.Target
                && after.Text == plan.ResultText && after.Selection == new TextRange(plan.Caret, plan.Caret))
                CompletePracticeCommand(command, direct: true);
        }

        // A foreground session can retire while its independent direct command
        // is queued or executing. Only that preview loses its lesson provenance.
        internal void RetirePreview() => _practiceWakeCommand = null;

        internal void RetireCommands()
        {
            RetirePreview();
            _pendingDirectCommand = null;
        }

        private OnDemandPracticeProgress? PracticeCommand(InputNotice notice, bool direct)
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
        private void CompletePracticeCommand(OnDemandPracticeProgress? command, bool direct)
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
            {
                RetirePreview();
                _practicePreviewAt = owner._loop.NowMs;
            }
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
