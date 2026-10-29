using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using TypeLay.Core;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

public sealed partial class NativeSourceActivationTests
{
    [Theory]
    [InlineData("exposed")]
    [InlineData("covered")]
    [InlineData("cancelled")]
    [InlineData("exception")]
    public Task CallerIsolationEndsBeforeSourcePreparationAndIsRemovedOnEveryExit(string scenario)
        => ui.RunAsync(async () =>
        {
            await using var source = await ProductionForegroundWriteTests.Desktop.Start();
            string original = N.ReadText(source.A, Limits.MaxTextUnits);
            var originalSelection = N.ReadSelection(source.A);
            nint originalReceipt = N.GetProp(source.Root, N.FixtureClickReceipt);
            var cover = new Window
            {
                Title = "TypeLay caller isolation cover",
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = SystemParameters.VirtualScreenLeft,
                Top = SystemParameters.VirtualScreenTop,
                Width = SystemParameters.VirtualScreenWidth,
                Height = SystemParameters.VirtualScreenHeight,
                ShowActivated = false,
                Topmost = true,
                Content = new TextBlock { Text = "Owned test cover" }
            };
            var coverRendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cover.ContentRendered += (_, _) => coverRendered.TrySetResult();
            int coverClicks = 0;
            cover.PreviewMouseLeftButtonDown += (_, _) => coverClicks++;
            Window? caller = null;
            nint callerRoot = 0;
            bool? topmostAtClosing = null, callerAliveAtClosing = null;
            int? nativeTopmostAtClosing = null;
            bool reachedSourcePreparation = false;
            using var action = CancellationTokenSource.CreateLinkedTokenSource(source.Token);
            try
            {
                cover.Show();
                await coverRendered.Task.WaitAsync(source.Token);
                nint coverRoot = new WindowInteropHelper(cover).Handle;
                Assert.NotEqual(nint.Zero, coverRoot);
                Assert.Equal(0x8, N.GetStyle(coverRoot, -20) & 0x8);
                Assert.True(N.GetWindowRect(source.A, out var fieldBounds));
                var fieldPoint = new N.Point
                {
                    X = fieldBounds.Left + (fieldBounds.Right - fieldBounds.Left) / 2,
                    Y = fieldBounds.Top + (fieldBounds.Bottom - fieldBounds.Top) / 2
                };
                Assert.Equal(coverRoot, WindowFromPoint(fieldPoint));
                Task preparing = WithNavigationCaller(source, source.A, PrepareSource, action.Token, window =>
                {
                    caller = window;
                    window.ContentRendered += (_, _) =>
                    {
                        callerRoot = new WindowInteropHelper(window).Handle;
                        if (scenario == "cancelled")
                            action.Cancel();
                    };
                    window.Closing += (_, _) =>
                    {
                        callerAliveAtClosing = N.IsWindow(callerRoot);
                        topmostAtClosing = window.Topmost;
                        nativeTopmostAtClosing = N.GetStyle(callerRoot, -20) & 0x8;
                    };
                });
                if (scenario == "cancelled")
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparing);
                else if (scenario == "exception")
                {
                    var error = await Assert.ThrowsAsync<InvalidOperationException>(() => preparing);
                    Assert.Equal("caller-scope-regression", error.Message);
                }
                else if (scenario == "covered")
                {
                    var error = await Assert.ThrowsAsync<NativeSourceSetup.Failure>(() => preparing);
                    Assert.Equal("source-click-obscured", error.Code);
                }
                else
                {
                    await preparing;
                    Assert.Equal((source.Root, source.A), N.Focused());
                    Assert.Equal("Prepared after caller isolation", N.ReadText(source.A, Limits.MaxTextUnits));
                    Assert.Equal(new TextRange(1, 4), N.ReadSelection(source.A));
                    Assert.NotEqual(originalReceipt, N.GetProp(source.Root, N.FixtureClickReceipt));
                }
                Assert.Equal(scenario != "cancelled", reachedSourcePreparation);
                Assert.NotNull(caller);
                Assert.NotEqual(nint.Zero, callerRoot);
                Assert.True(callerAliveAtClosing);
                Assert.False(topmostAtClosing);
                Assert.Equal(0, nativeTopmostAtClosing);
                Assert.False(caller.Topmost);
                Assert.False(caller.IsVisible);
                Assert.False(N.IsWindow(callerRoot));
                Assert.Equal(0, coverClicks);
                if (scenario != "exposed")
                {
                    Assert.Equal(original, N.ReadText(source.A, Limits.MaxTextUnits));
                    Assert.Equal(originalSelection, N.ReadSelection(source.A));
                    Assert.Equal(originalReceipt, N.GetProp(source.Root, N.FixtureClickReceipt));
                }
                source.AssertZeroSubmits();

                async Task PrepareSource()
                {
                    reachedSourcePreparation = true;
                    Assert.NotNull(caller);
                    Assert.False(caller.Topmost);
                    Assert.Equal(0, N.GetStyle(callerRoot, -20) & 0x8);
                    Assert.Equal(callerRoot, N.GetForegroundWindow());
                    if (scenario == "exception")
                        throw new InvalidOperationException("caller-scope-regression");
                    if (scenario == "covered")
                    {
                        // The temporary caller band cannot authorize a source
                        // click through the retained, still-topmost owned cover.
                        Assert.Equal(coverRoot, WindowFromPoint(fieldPoint));
                        await source.Prepare(source.A, "must not be initialized", new(1, 4));
                        return;
                    }
                    cover.Topmost = false;
                    Assert.Equal(0, N.GetStyle(coverRoot, -20) & 0x8);
                    await source.NavigateAndPrepare(source.A, "Prepared after caller isolation", new(1, 4));
                }
            }
            finally
            {
                try
                {
                    cover.Topmost = false;
                }
                finally { cover.Close(); }
            }
        });
}
