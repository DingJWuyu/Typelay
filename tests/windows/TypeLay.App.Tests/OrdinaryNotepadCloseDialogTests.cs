using System.Windows.Automation;
using N = TypeLay.Platform.Windows.NativeMethods;
using Xunit;

namespace TypeLay.App.Tests;

// Real HWND ownership/class/visibility, not a model of the UIA desktop tree.
// The shipped Notepad scene still proves its actual prompt and normal exit.
[Collection("Desktop")]
public sealed class OrdinaryNotepadCloseDialogTests(WpfDesktopFixture desktop)
{
    [Theory]
    [InlineData("no")]
    [InlineData("missing")]
    [InlineData("wrong-id")]
    [InlineData("ambiguous")]
    public Task DiscardSelectionRequiresExactlyOneNoButtonAmongRealNativeControls(string scenario) => desktop.RunAsync(async () =>
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(5));
        var ct = limit.Token;
        nint owner = 0, dialog = 0, expected = 0;
        try
        {
            owner = N.CreateWindow(0, "STATIC", "Synthetic source", 0x90000000, 0, 0, 240, 200, 0, 0, N.GetModuleHandle(null), 0);
            Assert.NotEqual(nint.Zero, owner);
            dialog = N.CreateWindow(0, "#32770", "Synthetic close prompt", 0x90000000, 0, 0, 200, 180,
                owner, 0, N.GetModuleHandle(null), 0);
            Assert.NotEqual(nint.Zero, dialog);
            int[] ids = scenario switch
            {
                "no" => [6, 7, 2],
                "ambiguous" => [6, 7, 7, 2],
                "wrong-id" => [6, 17, 2],
                _ => [6, 2]
            };
            for (int index = 0; index < ids.Length; index++)
            {
                nint button = N.CreateWindow(0, "BUTTON", "Synthetic choice", 0x50000000, 8, 8 + index * 32, 140, 28,
                    dialog, ids[index], N.GetModuleHandle(null), 0);
                Assert.NotEqual(nint.Zero, button);
                if (ids[index] == 7)
                    expected = button;
            }
            await Task.Run(() =>
            {
                var root = AutomationElement.FromHandle(dialog);
                if (scenario == "ambiguous")
                {
                    var error = Assert.Throws<HyPackageCheck.Failure>(() => HyPackageCheck.FindOrdinaryNotepadDiscardButton(root));
                    Assert.Equal("ordinary-notepad-discard-button-ambiguous", error.Code);
                    return;
                }
                var selected = HyPackageCheck.FindOrdinaryNotepadDiscardButton(root);
                if (scenario == "no")
                {
                    Assert.NotNull(selected.Button);
                    Assert.Equal(expected, (nint)selected.Button.Current.NativeWindowHandle);
                    Assert.Equal("7", selected.AutomationId);
                }
                else
                {
                    Assert.Null(selected.Button);
                    Assert.Null(selected.AutomationId);
                }
            }, ct).WaitAsync(ct);
        }
        finally
        {
            if (dialog != 0)
                N.DestroyWindow(dialog);
            if (owner != 0)
                N.DestroyWindow(owner);
        }
    });

    [Theory]
    [InlineData("owned")]
    [InlineData("other-owner")]
    [InlineData("wrong-process")]
    [InlineData("hidden")]
    [InlineData("disabled")]
    [InlineData("not-dialog")]
    public Task OnlyTheVisibleEnabledDialogOfTheExactSourceCanBeLocated(string scenario) => desktop.RunAsync(async () =>
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        limit.CancelAfter(TimeSpan.FromSeconds(5));
        var ct = limit.Token;
        ct.ThrowIfCancellationRequested();
        nint owner = 0, otherOwner = 0, dialog = 0;
        try
        {
            owner = N.CreateWindow(0, "STATIC", "Synthetic source", 0x90000000, 0, 0, 200, 100, 0, 0, N.GetModuleHandle(null), 0);
            otherOwner = N.CreateWindow(0, "STATIC", "Other source", 0x90000000, 220, 0, 200, 100, 0, 0, N.GetModuleHandle(null), 0);
            Assert.NotEqual(nint.Zero, owner);
            Assert.NotEqual(nint.Zero, otherOwner);
            uint processId = (uint)Environment.ProcessId;
            Assert.Equal(nint.Zero, HyPackageCheck.FindOrdinaryNotepadCloseDialog(owner, processId));
            uint style = 0x80000000;
            if (scenario != "hidden")
                style |= 0x10000000;
            if (scenario == "disabled")
                style |= 0x08000000;
            dialog = N.CreateWindow(0, scenario == "not-dialog" ? "STATIC" : "#32770", "Synthetic close prompt", style,
                0, 0, 160, 80, scenario == "other-owner" ? otherOwner : owner, 0, N.GetModuleHandle(null), 0);
            Assert.NotEqual(nint.Zero, dialog);
            uint expectedProcessId = scenario == "wrong-process" ? processId + 1 : processId;
            nint expected = scenario == "owned" ? dialog : 0;
            Assert.Equal(expected, HyPackageCheck.FindOrdinaryNotepadCloseDialog(owner, expectedProcessId));
            Assert.Equal(scenario == "owned", HyPackageCheck.OrdinaryNotepadCloseDialogMatches(owner, expectedProcessId, dialog));
            if (scenario == "owned")
            {
                var identity = await Task.Run(() =>
                {
                    var current = AutomationElement.FromHandle(expected).Current;
                    return (Window: current.NativeWindowHandle, Process: current.ProcessId);
                }, ct).WaitAsync(ct);
                Assert.Equal(expected, (nint)identity.Window);
                Assert.Equal(Environment.ProcessId, identity.Process);
                // A retained HWND must not authorize a later choice after its
                // native ownership changes, even within the same process.
                Assert.Equal(owner, N.SetWindowLongPtr(dialog, -8, otherOwner)); // GWLP_HWNDPARENT
                Assert.False(HyPackageCheck.OrdinaryNotepadCloseDialogMatches(owner, processId, dialog));
                Assert.Equal(nint.Zero, HyPackageCheck.FindOrdinaryNotepadCloseDialog(owner, processId));
                Assert.Equal(dialog, HyPackageCheck.FindOrdinaryNotepadCloseDialog(otherOwner, processId));
            }
            Assert.True(N.DestroyWindow(dialog));
            dialog = 0;
            Assert.Equal(nint.Zero, HyPackageCheck.FindOrdinaryNotepadCloseDialog(owner, processId));
        }
        finally
        {
            if (dialog != 0)
                N.DestroyWindow(dialog);
            if (otherOwner != 0)
                N.DestroyWindow(otherOwner);
            if (owner != 0)
                N.DestroyWindow(owner);
        }
    });
}
