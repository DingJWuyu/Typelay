using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using TypeLay.Core;
using TypeLay.Platform.Windows;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class NativeSourceSetupTests(WpfDesktopFixture desktop)
{
    [Theory]
    [InlineData("visible")]
    [InlineData("covered")]
    [InlineData("cancelled")]
    [InlineData("wrong-root")]
    public Task ClickUsesTheVisibleOwnedStockEditOrRefusesWithoutClickingAnotherWindow(string scenario)
        => desktop.RunAsync(async () =>
        {
            await using var source = await Source.Start();
            var cover = new Window
            {
                Title = "TypeLay source setup cover", Width = 1600, Height = 1000,
                Left = 0, Top = 0, WindowStartupLocation = WindowStartupLocation.Manual,
                Topmost = true, Content = new Button { Content = "not the source" }
            };
            int coverClicks = 0;
            cover.PreviewMouseLeftButtonDown += (_, _) => coverClicks++;
            Assert.True(GetCursorPos(out var previous));
            try
            {
                if (scenario == "covered")
                {
                    Assert.True(N.GetWindowRect(source.Root, out var bounds));
                    cover.Left = bounds.Left;
                    cover.Top = bounds.Top;
                    cover.Width = Math.Max(200, bounds.Right - bounds.Left);
                    cover.Height = Math.Max(200, bounds.Bottom - bounds.Top);
                    var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    cover.ContentRendered += (_, _) => rendered.TrySetResult();
                    cover.Show();
                    await rendered.Task.WaitAsync(source.Token);
                }
                using var action = CancellationTokenSource.CreateLinkedTokenSource(source.Token);
                if (scenario == "cancelled") action.Cancel();
                nint root = scenario == "wrong-root" ? source.Field : source.Root;
                if (scenario == "cancelled")
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NativeSourceSetup.ClickAsync(root, source.Field, source.Process, action.Token));
                else if (scenario != "visible")
                    await Assert.ThrowsAsync<InvalidOperationException>(() => NativeSourceSetup.ClickAsync(root, source.Field, source.Process, action.Token));
                else
                {
                    await NativeSourceSetup.ClickAsync(root, source.Field, source.Process, action.Token);
                    while (N.Focused() != (source.Root, source.Field))
                        await Task.Delay(10, source.Token);
                }
                Assert.Equal(0, coverClicks);
                Assert.Equal(Source.Text, N.ReadText(source.Field, Limits.MaxTextUnits));
            }
            finally
            {
                cover.Close();
                SetCursorPos(previous.X, previous.Y);
            }
        });

    [Theory]
    [InlineData("responsive")]
    [InlineData("busy")]
    [InlineData("changed-text")]
    [InlineData("wrong-field")]
    [InlineData("cancelled")]
    public Task ResponseGateOnlyWaitsForReadTimeoutAndNeverMutatesTheSelection(string scenario)
        => desktop.RunAsync(async () =>
        {
            await using var source = await Source.Start();
            Assert.True(GetCursorPos(out var previous));
            using var entered = EventWaitHandle.OpenExisting(N.FixtureGuiBarrierName(source.Process.Id, "entered"));
            using var release = EventWaitHandle.OpenExisting(N.FixtureGuiBarrierName(source.Process.Id, "release"));
            try
            {
                await NativeSourceSetup.ClickAsync(source.Root, source.Field, source.Process, source.Token);
                while (N.Focused() != (source.Root, source.Field))
                    await Task.Delay(10, source.Token);
                N.Send(source.Field, N.EM_SETSEL, 1, 5);
                var observation = new NativeSourceSetup.Observation();
                using var action = CancellationTokenSource.CreateLinkedTokenSource(source.Token);
                if (scenario == "cancelled") action.Cancel();
                if (scenario == "busy")
                {
                    Assert.True(N.PostMessage(source.Root, N.FixtureBlockGui, 0, 0));
                    Assert.True(await Task.Run(() => entered.WaitOne(TimeSpan.FromSeconds(2)), source.Token));
                }
                string expected = scenario == "changed-text" ? "different synthetic source" : Source.Text;
                nint field = scenario == "wrong-field" ? source.OtherField : source.Field;
                var waiting = NativeSourceSetup.WaitResponsiveAsync(source.Root, field, source.Process, expected, observation, action.Token);
                if (scenario == "busy")
                {
                    // The first genuine 100ms native read has timed out. Release
                    // the existing bounded fixture barrier, not a timing sleep.
                    Assert.False(waiting.IsCompleted);
                    Assert.True(observation.ReadTimeouts > 0);
                    release.Set();
                }
                if (scenario == "cancelled")
                    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
                else if (scenario is "changed-text" or "wrong-field")
                    await Assert.ThrowsAsync<InvalidOperationException>(() => waiting);
                else
                {
                    await waiting;
                    Assert.NotNull(observation.ReadCompletedAtMs);
                    Assert.True(observation.ReadCompletedAtMs >= observation.ReadStartedAtMs);
                    Assert.True(observation.IdentityMatched && observation.FocusMatched);
                }
                Assert.Equal(Source.Text, N.ReadText(source.Field, Limits.MaxTextUnits));
                Assert.Equal(new TextRange(1, 5), N.ReadSelection(source.Field));
                Assert.False(observation.SelectionCompleted);
                Assert.Null(observation.SelectionStartedAtMs);
            }
            finally
            {
                release.Set();
                SetCursorPos(previous.X, previous.Y);
            }
        });

    private sealed class Source(Process process, FixtureDescriptor descriptor, CancellationTokenSource deadline) : IAsyncDisposable
    {
        internal const string Text = "Synthetic stock source 🙂";
        internal Process Process => process;
        internal CancellationToken Token => deadline.Token;
        internal nint Root => (nint)descriptor.RootHwnd;
        internal nint Field => (nint)descriptor.Fields.Single(item => item.Name == "a").Hwnd;
        internal nint OtherField => (nint)descriptor.Fields.Single(item => item.Name == "b").Hwnd;
        internal static async Task<Source> Start()
        {
            string root = AppContext.BaseDirectory;
            while (!File.Exists(Path.Combine(root, "TypeLay.slnx")))
                root = Directory.GetParent(root)?.FullName ?? throw new DirectoryNotFoundException("TypeLay.slnx");
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(Path.Combine(root, "tests/windows/fixtures/TypeLay.NativeFixture/bin/Release/net10.0/TypeLay.NativeFixture.dll"));
            start.ArgumentList.Add("--stock-controls");
            var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(10));
            var process = Process.Start(start)!;
            try
            {
                var descriptor = JsonSerializer.Deserialize<FixtureDescriptor>(await process.StandardOutput.ReadLineAsync(deadline.Token) ?? "null");
                Assert.NotNull(descriptor);
                Assert.Equal(process.Id, descriptor.ProcessId);
                var source = new Source(process, descriptor, deadline);
                N.SendString(source.Field, N.WM_SETTEXT, Text);
                return source;
            }
            catch
            {
                try { await Stop(process); }
                finally { process.Dispose(); deadline.Dispose(); }
                throw;
            }
        }
        public async ValueTask DisposeAsync()
        {
            try { await Stop(process); }
            finally { process.Dispose(); deadline.Dispose(); }
        }
        private static async Task Stop(Process process)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(cleanup.Token);
        }
    }
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out N.Point point);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetCursorPos(int x, int y);
}
