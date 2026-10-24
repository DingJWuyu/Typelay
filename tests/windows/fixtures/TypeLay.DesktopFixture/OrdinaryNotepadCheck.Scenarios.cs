using N = TypeLay.Platform.Windows.NativeMethods;

internal static partial class HyPackageCheck
{
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
