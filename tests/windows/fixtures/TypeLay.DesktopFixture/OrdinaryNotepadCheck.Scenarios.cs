using System.Runtime.InteropServices;
using System.Text;
using N = TypeLay.Platform.Windows.NativeMethods;

internal static partial class HyPackageCheck
{
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

    // The fixed Notepad tail, not a general continue-on-failure runner. Only the
    // read-only cue wait can establish this denial; input callbacks never can.
    internal static async Task RunOrdinaryNotepadInteractions(Report report,
        Func<Scene, Task> awaitAcceptanceCue, Func<Scene, Task> acceptAndUndo,
        Func<Scene, Task> editReturnAndUndo, Func<Scene, Task> onDemandCancelAndDirect,
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
                await acceptAndUndo(scene);
            });
        }
        catch (Failure error) when (ReferenceEquals(error, publicDenialBeforeInput))
        {
            // Phase has already retained the original failure, stage and facts.
            var edit = report.Scenes.Single(scene => scene.Name == "edit-return-and-undo");
            edit.Stage = "dependency-unavailable";
            edit.Facts = new
            {
                dependency = "automatic-accept-and-undo",
                reason = error.Code
            };
        }
        if (publicDenialBeforeInput is null)
            await report.Phase("edit-return-and-undo", editReturnAndUndo);
        await report.Phase("on-demand-cancel-and-direct", async scene =>
        {
            if (publicDenialBeforeInput is not null)
            {
                scene.Stage = "verify-independent-source";
                confirmIndependentSource(); // Read-only: never restore focus or source.
                report.OperationToken.ThrowIfCancellationRequested();
            }
            scene.Stage = "on-demand-cancel-and-direct";
            await onDemandCancelAndDirect(scene);
        });
        // A forced cleanup after any other failure is not ordinary exit.
        await report.Phase("ordinary-exit", ordinaryExit);
    }
}
