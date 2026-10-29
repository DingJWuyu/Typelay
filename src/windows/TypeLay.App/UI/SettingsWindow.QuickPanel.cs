using TypeLay.Runtime;

namespace TypeLay.App;

internal sealed record QuickPanelPreparationState(object Identity, long SettingsRevision, string? Generation, bool InventoryKnown,
    TranslationPreparationPhase Phase, TranslationPreparationView View, bool Ready);

internal sealed partial class SettingsWindow
{
    private object _quickPanelPreparationIdentity = new();
    private SetupProgress? _quickPanelProgress;
    private string? _quickPanelProgressGeneration;
    internal event Action? QuickPanelPreparationChanged;
    internal event Action? QuickPanelProgressChanged;

    private TranslationPreparationPhase QuickPanelPreparationPhase => _startingModel ? TranslationPreparationPhase.StartingModel
        : _install is { IsCancellationRequested: true } ? TranslationPreparationPhase.Pausing
        : _install is not null ? TranslationPreparationPhase.Preparing
        : _setupPaused ? TranslationPreparationPhase.Paused
        : _setupFailure is not null ? TranslationPreparationPhase.Failed
        : TranslationPreparationPhase.Idle;

    internal QuickPanelPreparationState ReadQuickPanelPreparation()
    {
        var inventory = _installations.ReadInstallationState();
        var view = PreparationView();
        if (!IsCurrentQuickPanelRequest(inventory.Generation))
            view = view with
            {
                StatusText = "安装记录已变化，请刷新后重新确认。",
                Guidance = "安装记录已变化，请刷新后重新确认。",
                PrimaryAction = view.PrimaryAction with
                {
                    Enabled = false
                }
            };
        return new(_quickPanelPreparationIdentity, _settings.Revision, inventory.Generation, inventory.InventoryKnown,
            QuickPanelPreparationPhase, view, _model.Ready);
    }

    private bool IsCurrentQuickPanelRequest(string? generation) => _packageRequest is { } request
        ? request.ExpectedCurrent == generation : _preparationRequest is null || _quickPanelProgressGeneration == generation;

    private bool IsCurrentQuickPanelPreparation(QuickPanelPreparationState state)
    {
        var inventory = _installations.ReadInstallationState();
        return !_emergencyShutdown && _closeDialog is null && ReferenceEquals(state.Identity, _quickPanelPreparationIdentity)
            && state.SettingsRevision == _settings.Revision && state.Generation == inventory.Generation
            && state.InventoryKnown == inventory.InventoryKnown && state.Phase == QuickPanelPreparationPhase
            && IsCurrentQuickPanelRequest(inventory.Generation);
    }

    internal SetupProgress? ReadQuickPanelProgress(QuickPanelPreparationState state) => IsCurrentQuickPanelPreparation(state)
        && state.InventoryKnown && state.Generation == _quickPanelProgressGeneration ? _quickPanelProgress : null;

    internal void RunQuickPanelPreparation(QuickPanelPreparationState state)
    {
        // A tray action is an explicit preparation intent, never practice or an
        // inferred update. Revalidate again after the existing install consent.
        if (!IsCurrentQuickPanelPreparation(state) || state.View.PrimaryAction.Id == TranslationPreparationAction.NextStep)
            return;
        RunPreparationAction(state.View.PrimaryAction, () => IsCurrentQuickPanelPreparation(state));
    }
}
