using TypeLay.Core;

namespace TypeLay.Runtime;

// Only confirmed lesson facts enter here. This module has no input adapter,
// reader, source observation, focus operation or acceptance permission.
internal sealed class PracticeProgressCoordinator
{
    private long _revision;
    private string? _editSeed;
    public AutomaticPracticeProgress? Automatic
    {
        get; private set;
    }
    public event Action<AutomaticPracticeProgress?>? AutomaticChanged;

    public void BeginAutomatic(Guid runId, Guid generation) => Publish(new(runId, null, null, generation, 0));
    public void ClearAutomatic()
    {
        Automatic = null;
        _editSeed = null;
        AutomaticChanged?.Invoke(null);
    }
    public void SourceBound(OwnedPracticeBinding binding)
    {
        if (Automatic is { } progress && progress.RunId == binding.RunId)
            Publish(progress with
            {
                Target = binding.Target,
                ConnectionId = binding.ConnectionId,
                AwaitingFreshSource = false
            });
    }
    public void SourceAwaited(Guid generation)
    {
        if (Automatic is { } progress)
            Publish(progress with
            {
                CommandGeneration = generation,
                AwaitingFreshSource = true
            });
    }
    public void CommandRotated(Guid generation)
    {
        if (Automatic is { } progress)
            Publish(progress with
            {
                CommandGeneration = generation
            });
    }
    public void PreviewConfirmed()
    {
        if (Automatic is { PreviewCompleted: false } progress)
            Publish(progress with
            {
                PreviewCompleted = true
            });
    }
    public void EditBegan(string draft)
    {
        if (Automatic is not { AcceptanceCompleted: true } progress)
            return;
        _editSeed = draft;
        Publish(progress with
        {
            EditingStarted = true,
            DraftChanged = false,
            ReturnedToSource = false
        });
    }
    public void DraftChanged(string draft)
    {
        if (_editSeed is null || Automatic is not { EditingStarted: true } progress)
            return;
        bool changed = draft != _editSeed && WriteSafety.ValidReplacement(draft);
        if (changed != progress.DraftChanged)
            Publish(progress with
            {
                DraftChanged = changed
            });
    }
    public void ReturnConfirmed(string candidate)
    {
        if (Automatic is { EditingStarted: true, DraftChanged: true, ReturnedToSource: false } progress
            && candidate != _editSeed)
            Publish(progress with
            {
                ReturnedToSource = true
            });
    }
    public AutomaticPracticeProgress? AcceptanceConfirmed(bool edited)
    {
        if (Automatic is not { } progress)
            return null;
        if (!progress.AcceptanceCompleted && progress.PreviewCompleted && !edited)
            Publish(progress with
            {
                AcceptanceCompleted = true
            });
        else if (edited && progress.AcceptanceCompleted && progress.EditingStarted && progress.DraftChanged
            && progress.ReturnedToSource && !progress.EditedAcceptanceCompleted)
            return progress with
            {
                Revision = ++_revision,
                EditedAcceptanceCompleted = true
            };
        return null;
    }
    public void CompletionConfirmed(AutomaticPracticeProgress progress)
    {
        Automatic = progress;
        AutomaticChanged?.Invoke(progress);
    }
    public void ForgetEdit() => _editSeed = null;

    private void Publish(AutomaticPracticeProgress progress)
    {
        Automatic = progress with
        {
            Revision = ++_revision
        };
        AutomaticChanged?.Invoke(Automatic);
    }
}
