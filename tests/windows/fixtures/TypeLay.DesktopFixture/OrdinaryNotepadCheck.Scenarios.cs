using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using N = TypeLay.Platform.Windows.NativeMethods;

internal static partial class HyPackageCheck
{
    internal static (AutomationElement? Button, string? AutomationId) FindOrdinaryNotepadDiscardButton(AutomationElement dialog)
    {
        // Only IDNO's numeric form and the classic Notepad provider form used
        // by FlaUI's integration tests. The real Notepad regression must verify
        // the installed provider; there is no caption or OS-version fallback.
        var matches = dialog.FindAll(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new OrCondition(new PropertyCondition(AutomationElement.AutomationIdProperty, "7"),
                new PropertyCondition(AutomationElement.AutomationIdProperty, "CommandButton_7"))));
        Require(matches.Count <= 1, "ordinary-notepad-discard-button-ambiguous");
        if (matches.Count == 0)
            return (null, null);
        var button = matches[0];
        string id = button.Current.AutomationId;
        Require(id is "7" or "CommandButton_7", "ordinary-notepad-discard-button-identity-changed");
        return (button, id); // Only the two fixed IDs can enter diagnostic output.
    }

    internal static nint FindOrdinaryNotepadCloseDialog(nint owner, uint processId)
    {
        if (owner == 0 || processId == 0)
            return 0;
        // GW_ENABLEDPOPUP follows the native owner relation, not the popup's
        // provider-dependent position under the UIA desktop root.
        // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-getwindow
        nint dialog = GetNotepadWindow(owner, 6);
        return OrdinaryNotepadCloseDialogMatches(owner, processId, dialog) ? dialog : 0;
    }

    internal static bool OrdinaryNotepadCloseDialogMatches(nint owner, uint processId, nint dialog)
    {
        if (owner == 0 || processId == 0 || dialog == 0 || dialog == owner
            || N.GetWindowThreadProcessId(owner, out uint ownerPid) == 0 || ownerPid != processId
            || N.GetAncestor(owner, 2) != owner
            || GetNotepadWindow(owner, 6) != dialog || GetNotepadWindow(dialog, 4) != owner
            || N.GetWindowThreadProcessId(dialog, out uint dialogPid) == 0 || dialogPid != processId
            || !N.IsWindowVisible(dialog) || !N.IsWindowEnabled(dialog))
            return false;
        var className = new StringBuilder(16);
        return N.GetClassName(dialog, className, className.Capacity) != 0 && className.ToString() == "#32770";
    }

    [DllImport("user32.dll", EntryPoint = "GetWindow")]
    private static extern nint GetNotepadWindow(nint window, uint command);

    internal static bool OrdinaryTextMatches(Scene scene, nint field, int limit, string expected)
    {
        scene.TextLengthChangeCount ??= 0;
        bool matches = N.TextMatches(field, limit, expected, out var changed);
        if (changed is not null)
        {
            scene.TextLengthChangeCount++;
            scene.RecordTextReadFailure(changed);
        }
        return matches;
    }

    // This unattended check observes the public cue but cannot supply a trusted
    // source key. Only that known limitation or the explicit read-only capability
    // denial may reach the independent tail; neither claims acceptance passed.
    internal static async Task RunOrdinaryNotepadInteractions(Report report,
        Func<Scene, Task> awaitAcceptanceCue, Func<Scene, Task> onDemandCancelAndDirect,
        Func<Scene, Task> ordinaryExit, Action confirmIndependentSource)
    {
        Failure? publicDenialBeforeInput = null;
        try
        {
            await report.Phase("automatic-accept-and-undo", async scene =>
            {
                scene.Stage = "await-public-acceptance-cue";
                try
                {
                    await awaitAcceptanceCue(scene);
                }
                catch (Failure error) when (error.Code == "required-capability-unavailable")
                {
                    publicDenialBeforeInput = error;
                    throw;
                }
                report.OperationToken.ThrowIfCancellationRequested();
            });
            // Phase records the completed cue observation. Acceptance itself was
            // not attempted; do not turn an available cue into a passing action.
            var acceptance = report.Scenes.Single(scene => scene.Name == "automatic-accept-and-undo");
            acceptance.Outcome = "not-run";
            acceptance.Stage = "trusted-source-input-required";
            acceptance.Facts = new
            {
                acceptanceCueObserved = true,
                acceptanceAttempted = false,
                acceptanceVerified = false
            };
        }
        catch (Failure error) when (ReferenceEquals(error, publicDenialBeforeInput))
        {
            // Retain the product's actual public denial, stage and observations.
        }
        var edit = report.Scenes.Single(scene => scene.Name == "edit-return-and-undo");
        edit.Stage = "dependency-unavailable";
        edit.Facts = new
        {
            dependency = "automatic-accept-and-undo",
            reason = publicDenialBeforeInput?.Code ?? "trusted-source-input-required"
        };
        await report.Phase("on-demand-cancel-and-direct", async scene =>
        {
            scene.Stage = "verify-independent-source";
            confirmIndependentSource(); // Read-only: never restore focus or source.
            report.OperationToken.ThrowIfCancellationRequested();
            scene.Stage = "on-demand-cancel-and-direct";
            await onDemandCancelAndDirect(scene);
        });
        // A forced cleanup after any other failure is not ordinary exit.
        await report.Phase("ordinary-exit", ordinaryExit);
    }
}
