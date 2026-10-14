using TypeLay.Windows.Contracts;
using TypeLay.Windows;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Automation;
using TypeLay.Core;
using TypeLay.Protocol;
using TypeLay.Platform.Windows;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

[CollectionDefinition("Desktop", DisableParallelization = true)]
public sealed class DesktopCollection : ICollectionFixture<WpfDesktopFixture>
{
}

// Uses production Broker IPC and unmodified system Edit controls in a separate
// process. It does not use fixture-adapter IME flags, injected keys or a fake writer.
[Collection("Desktop")]
public sealed class ProductionDirectWriteTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DirectReplacementStaysBoundToTheOriginalFieldAfterFocusChanges(bool multiline)
    {
        string root = Repository();
        using var fixture = Start("dotnet", Path.Combine(root,
            "tests/windows/fixtures/TypeLay.NativeFixture/bin/Release/net10.0/TypeLay.NativeFixture.dll"), "--stock-controls");
        using var broker = Start("dotnet", Path.Combine(root,
            "src/windows/TypeLay.Broker/bin/Release/net10.0-windows/TypeLay.Broker.dll"));
        string token = broker.StartInfo.Environment["TYPELAY_PIPE_TOKEN"]!;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        long id = 0;
        try
        {
            string? line = await fixture.StandardOutput.ReadLineAsync(deadline.Token);
            var host = JsonSerializer.Deserialize<FixtureDescriptor>(line ?? "null")!;
            Assert.NotNull(host);
            nint a = (nint)host.Fields.Single(value => value.Name == (multiline ? "b" : "a")).Hwnd;
            nint b = (nint)host.Fields.Single(value => value.Name == (multiline ? "a" : "b")).Hwnd;
            async Task<BrokerResponse> Call(BrokerRequest request)
            {
                await Wire.WriteAsync(broker.StandardInput.BaseStream, request with
                {
                    Id = ++id,
                    Token = token
                }, deadline.Token);
                return await Wire.ReadAsync<BrokerResponse>(broker.StandardOutput.BaseStream, deadline.Token);
            }
            async Task Focus(nint field)
            {
                Assert.True(N.PostMessage((nint)host.RootHwnd, N.FixtureFocus, (nuint)field, 0));
                var until = Stopwatch.StartNew();
                while (N.Focused() != ((nint)host.RootHwnd, field) && until.Elapsed < TimeSpan.FromSeconds(2))
                    await Task.Delay(10, deadline.Token);
                Assert.Equal(((nint)host.RootHwnd, field), N.Focused());
            }
            async Task<InputSnapshot> Capture(string text, TextRange selection)
            {
                await Focus(a);
                N.SendString(a, N.WM_SETTEXT, text);
                N.Send(a, N.EM_SETSEL, (nuint)selection.Start, (nint)selection.End);
                long sentAt = WindowsInterruptClock.NowMs;
                var reply = await Call(new(BrokerProtocol.Version, 0, "capture", "", CaptureDirect: true));
                long receivedAt = WindowsInterruptClock.NowMs;
                Assert.NotNull(reply.Snapshot);
                Assert.InRange(reply.Snapshot.ObservedAtMs, sentAt, receivedAt);
                Assert.True(reply.Snapshot.DirectWriteSupported,
                    "The production source must advertise a coordinated writer before direct translation is allowed.");
                Assert.Equal(ImeState.Clear, reply.Snapshot.Ime);
                Assert.Equal(multiline, reply.Snapshot.Multiline);
                Assert.Equal(text, reply.Snapshot.Text);
                Assert.Equal(selection, reply.Snapshot.Selection);
                return reply.Snapshot;
            }
            static WritePlan Plan(InputSnapshot source, string translation)
            {
                var result = WriteSafety.Replace(source.Text, source.TranslationScope, translation);
                return new(Guid.NewGuid(), Guid.NewGuid(), 1, source, source.TranslationScope,
                    translation, result.Text, result.Caret);
            }
            async Task<WriteReceipt> Write(WritePlan plan)
            {
                var reply = await Call(new(BrokerProtocol.Version, 0, "direct-write", "", BoundTarget: plan.Expected.Target, Plan: plan));
                Assert.NotNull(reply.Receipt);
                return reply.Receipt;
            }
            await Call(new(BrokerProtocol.Version, 0, "hello", ""));
            N.SendString(b, N.WM_SETTEXT, "B must remain unchanged");
            var source = await Capture("A🙂你好Z", new(3, 5));
            // The system's multiline Edit exposes TextPattern without ValuePattern.
            // Its absence must not be mistaken for readonly during bound writeback.
            if (multiline)
            {
                var element = AutomationElement.FromHandle(a);
                Assert.False(element.TryGetCurrentPattern(ValuePattern.Pattern, out _));
                Assert.True(element.TryGetCurrentPattern(TextPattern.Pattern, out var textPattern));
                Assert.False(Assert.IsType<bool>(((TextPattern)textPattern).DocumentRange.GetAttributeValue(TextPattern.IsReadOnlyAttribute)));
            }
            var plan = Plan(source, "Hello");
            await Focus(b);
            Assert.Equal(WriteStatus.Verified, (await Write(plan)).Status);
            Assert.Equal("A🙂HelloZ", N.ReadText(a, Limits.MaxTextUnits));
            Assert.Equal("B must remain unchanged", N.ReadText(b, Limits.MaxTextUnits));
            Assert.Equal(((nint)host.RootHwnd, b), N.Focused());
            Assert.Equal(0, (long)N.Send((nint)host.RootHwnd, N.FixtureSubmitCount));
            N.Send(a, 0xc7);
            Assert.Equal("A🙂你好Z", N.ReadText(a, Limits.MaxTextUnits));
            Assert.Equal(((nint)host.RootHwnd, b), N.Focused());
            source = await Capture("你好", new(2, 2));
            plan = Plan(source, "Hello");
            await Focus(b);
            N.SendString(a, N.WM_SETTEXT, "new user text");
            Assert.Equal(WriteStatus.RejectedBeforeMutation, (await Write(plan)).Status);
            Assert.Equal("new user text", N.ReadText(a, Limits.MaxTextUnits));
            Assert.Equal(((nint)host.RootHwnd, b), N.Focused());
            source = await Capture("你好", new(2, 2));
            plan = Plan(source, "Hello");
            await Focus(b);
            Assert.Equal(WriteStatus.Verified, (await Write(plan)).Status);
            Assert.Equal("Hello", N.ReadText(a, Limits.MaxTextUnits));
            Assert.Equal(0, (long)N.Send((nint)host.RootHwnd, N.FixtureSubmitCount));
            // Capture permission is not permanent: a later readonly/password
            // change must reject the old plan without touching either input.
            source = await Capture("你好", new(2, 2));
            plan = Plan(source, "Hello");
            await Focus(b);
            N.Send(a, 0x00cf, 1); // EM_SETREADONLY.
            var readonlyReceipt = await Write(plan);
            Assert.Equal(WriteStatus.RejectedBeforeMutation, readonlyReceipt.Status);
            Assert.Equal(Denial.Sensitive, readonlyReceipt.Reason);
            Assert.Equal("你好", N.ReadText(a, Limits.MaxTextUnits));
            Assert.Equal(((nint)host.RootHwnd, b), N.Focused());
            N.Send(a, 0x00cf, 0);
            // Password characters are supported only by the single-line system Edit.
            if (!multiline)
            {
                source = await Capture("你好", new(2, 2));
                plan = Plan(source, "Hello");
                await Focus(b);
                N.Send(a, 0x00cc, '*'); // EM_SETPASSWORDCHAR, synthetic source only.
                Assert.Equal((nuint)'*', N.Send(a, 0x00d2));
                var passwordReceipt = await Write(plan);
                Assert.Equal(WriteStatus.RejectedBeforeMutation, passwordReceipt.Status);
                Assert.Equal(Denial.Sensitive, passwordReceipt.Reason);
                Assert.Equal(((nint)host.RootHwnd, b), N.Focused());
                // Verify rejection while protected, then leave this synthetic
                // password state before inspecting its unchanged source text.
                N.Send(a, 0x00cc, 0);
                Assert.Equal((nuint)0, N.Send(a, 0x00d2));
                Assert.Equal("你好", N.ReadText(a, Limits.MaxTextUnits));
            }
            // Losing the cookie must invalidate old jobs, even at the same HWND.
            source = await Capture("你好", new(2, 2));
            plan = Plan(source, "Hello");
            N.RemoveProp(a, "TypeLay.NativeInput.Lifetime.v1");
            await Call(new(BrokerProtocol.Version, 0, "capture", "", CaptureDirect: true));
            await Focus(b);
            Assert.Equal(WriteStatus.RejectedBeforeMutation, (await Write(plan)).Status);
            Assert.Equal("你好", N.ReadText(a, Limits.MaxTextUnits));
            var renewed = await Capture("你好", new(2, 2));
            Assert.NotEqual(source.Target.Lifetime, renewed.Target.Lifetime);
            plan = Plan(renewed, "Hello");
            await Focus(b);
            var excluded = await Call(new(BrokerProtocol.Version, 0, "direct-write", "", BoundTarget: plan.Expected.Target,
                Plan: plan, Excluded: [fixture.ProcessName.ToLowerInvariant()]));
            Assert.Equal(WriteStatus.RejectedBeforeMutation, excluded.Receipt!.Status);
            Assert.Equal("你好", N.ReadText(a, Limits.MaxTextUnits));
            await Call(new(BrokerProtocol.Version, 0, "direct-clear", ""));
            Assert.Equal(WriteStatus.RejectedBeforeMutation, (await Write(plan)).Status);
            Assert.Equal("你好", N.ReadText(a, Limits.MaxTextUnits));
            Assert.Equal(((nint)host.RootHwnd, b), N.Focused());
            N.Send((nint)host.SubmitButtonHwnd, 0xf5);
            Assert.Equal(1, (long)N.Send((nint)host.RootHwnd, N.FixtureSubmitCount));
            await Call(new(BrokerProtocol.Version, 0, "quit", ""));
            await broker.WaitForExitAsync(deadline.Token);
            Assert.Equal(0, broker.ExitCode);
        }
        catch (N.NativeMessageException ex)
        {
            // Fixed stage metadata only; preserve the original failure and budget.
            output.WriteLine("Native message failed: operation={0}, messageId={1}, win32Error={2}, fixtureExited={3}, brokerExited={4}",
                ex.Operation, ex.MessageId, ex.Win32Error, fixture.HasExited, broker.HasExited);
            throw;
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            foreach (var process in new[] { broker, fixture })
                if (!process.HasExited)
                {
                    process.Kill(true);
                    await process.WaitForExitAsync(cleanup.Token);
                }
        }
    }

    private static Process Start(string name, params string[] arguments)
    {
        var info = new ProcessStartInfo(name)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
            info.ArgumentList.Add(argument);
        info.Environment["TYPELAY_PIPE_TOKEN"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        info.Environment.Remove("TYPELAY_TRACE");
        return Process.Start(info) ?? throw new InvalidOperationException("Test process did not start.");
    }
    private static string Repository()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TypeLay.slnx")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
