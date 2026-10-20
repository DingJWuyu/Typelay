using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using TypeLay.Core;
using TypeLay.Platform.Windows;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class NativeFixtureReadinessTests(ITestOutputHelper output)
{
    [Fact]
    public async Task DescriptorWaitsForGuiDispatchBeforeExternalControlInitialization()
    {
        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "TypeLay.slnx")))
            root = Directory.GetParent(root)?.FullName ?? throw new DirectoryNotFoundException("TypeLay.slnx");
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(root,
            "tests/windows/fixtures/TypeLay.NativeFixture/bin/Release/net10.0/TypeLay.NativeFixture.dll"));
        start.ArgumentList.Add("--stock-controls");
        start.Environment["TYPELAY_FIXTURE_HOLD_READY"] = "1";
        using var fixture = Process.Start(start)!;
        using var entered = new EventWaitHandle(false, EventResetMode.ManualReset,
            N.FixtureGuiBarrierName(fixture.Id, "entered"));
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset,
            N.FixtureGuiBarrierName(fixture.Id, "release"));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        bool failed = false;
        try
        {
            Assert.True(await Task.Run(() => entered.WaitOne(TimeSpan.FromSeconds(5)), deadline.Token),
                "The fixture GUI did not enter its queued startup barrier.");
            // Inspect the real pipe without consuming metadata or waiting for more bytes.
            SafeHandle pipe = fixture.StandardOutput.BaseStream switch
            {
                FileStream file => file.SafeFileHandle,
                PipeStream stream => stream.SafePipeHandle,
                _ => throw new InvalidOperationException("Unexpected redirected stdout stream.")
            };
            Assert.True(PeekNamedPipe(pipe, 0, 0, 0, out uint available, 0));
            Assert.Equal(0u, available);
            release.Set();
            string? line = await fixture.StandardOutput.ReadLineAsync(deadline.Token);
            var descriptor = JsonSerializer.Deserialize<FixtureDescriptor>(line ?? "null");
            Assert.NotNull(descriptor);
            Assert.Equal(fixture.Id, descriptor.ProcessId);
            nint field = (nint)descriptor.Fields.Single(value => value.Name == "a").Hwnd;
            const string original = "Ready source 🙂 selection";
            N.SendString(field, N.WM_SETTEXT, original);
            N.Send(field, N.EM_SETSEL, 1, 5);
            Assert.Equal(original, N.ReadText(field, Limits.MaxTextUnits));
            Assert.Equal(new TextRange(1, 5), N.ReadSelection(field));
            Assert.Equal((nuint)1, N.Send((nint)descriptor.RootHwnd, N.FixtureBarrierStatus));
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            release.Set();
            try
            {
                if (!fixture.HasExited)
                    fixture.Kill(entireProcessTree: true);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await fixture.WaitForExitAsync(cleanup.Token);
            }
            catch (Exception error) when (failed)
            {
                output.WriteLine($"Fixture cleanup also failed: {error.GetType().Name}");
            }
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekNamedPipe(SafeHandle pipe, nint buffer, uint bufferSize,
        nint bytesRead, out uint totalBytesAvailable, nint bytesLeftThisMessage);
}
