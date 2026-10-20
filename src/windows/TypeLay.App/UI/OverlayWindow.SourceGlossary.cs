using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows.Contracts;

namespace TypeLay.App;

internal sealed partial class OverlayWindow
{
    private InputSnapshot? _sourceGlossarySnapshot;
    private WindowsInputService? _sourceGlossaryInput;
    private SettingsStore? _sourceGlossarySettings;
    private NativeSourceGlossaryTransfer? _sourceGlossaryTransfer;
    private SessionViewToken? _sourceGlossaryOpeningToken;
    private SessionGlossaryEntry? _sourceGlossaryOpeningEntry;

    internal void ConfigureSourceGlossary(WindowsInputService input, SettingsStore settings)
    {
        _sourceGlossaryInput = input;
        _sourceGlossarySettings = settings;
    }
    public bool SupportsSourceGlossaryEntry(SessionViewToken token) => _ordinaryInput is not null
        && _sourceGlossaryInput is not null && _sourceGlossarySettings is not null
        && _ownedEditorHost?.IsOwnedEditorTarget(token.Target) != true
        && NativeSourceGlossaryTransfer.Supports(_sourceGlossarySnapshot, token);

    public bool IsGlossarySourceWindowCurrent(SessionGlossaryEntry entry, long original, long current)
    {
        if (!entry.SourceOrigin)
            return original == current;
        if (_sourceGlossaryTransfer is { } transfer)
            return ReferenceEquals(_glossary?.Entry, entry) && transfer.IsCurrent(entry, original, current);
        // BeginGlossaryEntry renders synchronously before returning its entry.
        // Only that exact opening call may use the original, unchanged epoch.
        if (_sourceGlossaryOpeningToken != entry.Token || original != current
            || _ordinaryInput?.WindowEpoch != current || !SupportsSourceGlossaryEntry(entry.Token)
            || !KeyboardHook.ExactNativeFieldFocused(entry.Token.Target.AsWindows()))
            return false;
        _sourceGlossaryOpeningEntry ??= entry;
        return ReferenceEquals(_sourceGlossaryOpeningEntry, entry);
    }
    public async Task<bool> ReturnGlossaryToSourceAsync(SessionGlossaryEntry entry)
    {
        if (!ReferenceEquals(_glossary?.Entry, entry) || !entry.SourceOrigin
            || _sourceGlossaryTransfer is not { } transfer || _ordinaryInput is not { } monitor
            || _sourceGlossaryInput is not { } input || _sourceGlossarySettings is not { } settings
            || _viewContext is not { } view || view.Token != entry.Token
            || GlossaryEntryFocus is not { Ime: ImeState.Clear } focus || focus.FormId != entry.FormId
            || settings.Revision != entry.SettingsRevision || settings.HasChanges(settings.Value)
            || !monitor.HasSessionConfiguration(new(entry.Token.CommandGeneration, view.Bindings))
            || !transfer.ArmReturn(entry))
            return false;
        var context = new InputContext(monitor.UserEpoch, monitor.ForegroundEpoch, monitor.SourceUserEpoch,
            entry.Token.Target, settings.Value.Excluded, true, new(entry.Token.CommandGeneration, view.Bindings));
        var result = await input.ReturnGlossaryToSourceAsync(context, transfer.ReturnAuthorization!, transfer.ReturnExpiresAtMs);
        return ReferenceEquals(_glossary?.Entry, entry) && _viewContext?.Token == entry.Token
            && settings.Revision == entry.SettingsRevision && !settings.HasChanges(settings.Value)
            && result.Returned && await transfer.ConfirmReturnedAsync();
    }
    private async Task CancelGlossaryAsync(InlineGlossaryForm form)
    {
        if (!ReferenceEquals(_glossary, form))
            return;
        if (!form.Entry.SourceOrigin)
        {
            CloseGlossary(form, restoreEditor: true);
            return;
        }
        try
        {
            await ReturnGlossaryToSourceAsync(form.Entry);
        }
        finally { CloseGlossary(form, restoreEditor: false); }
    }
}
