using System.Windows.Threading;
using TypeLay.Core;
using TypeLay.Runtime;
using TypeLay.Windows.Contracts;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class BrowserRoutingTests(WpfDesktopFixture desktop)
{
    [Fact]
    public Task MissingBrowserConnectionCannotBorrowTheNativeBrokerForFrozenOperations() => desktop.RunAsync(async () =>
    {
        var broker = new CountingBroker();
        var hook = new KeyboardHook();
        var browser = new BrowserInput(new BrowserServer(Dispatcher.CurrentDispatcher), hook);
        using var router = new WindowsInputRouter(new(broker, 0, monitor: hook), hook, browser);
        router.ConfigureShortcuts(true, "Ctrl+Shift+F9", "Ctrl+Shift+F10", SessionShortcutBindings.Default, true);
        var source = new InputSnapshot(new("windows-browser", "closed-document:1", Guid.NewGuid()), "original",
            new(0, 0), new(0, 0, 100, 20), false, FieldAccess.Supported, ImeState.Clear, 1, 1, 1, true, DirectWriteSupported: true);
        var plan = new WritePlan(Guid.NewGuid(), Guid.NewGuid(), 1, source, null, "result", "result", 6);
        var context = new InputContext(0, 0, 0, source.Target, [], true,
            new(router.CommandGeneration, SessionShortcutBindings.Default));
        Assert.Equal(WriteStatus.RejectedBeforeMutation, (await router.WriteDirectAsync(plan, [])).Status);
        Assert.Equal(WriteStatus.RejectedBeforeMutation, (await router.WriteAsync(plan, context, 100)).Status);
        Assert.False((await router.ReturnToSourceAsync(context)).Returned);
        await router.ClearAsync(context);
        Assert.Equal(0, broker.Calls);
        await router.CaptureAsync(context with
        {
            BoundTarget = null
        });
        Assert.Equal(1, broker.Calls); // Native non-browser discovery still has its original route.
    });
    [Fact]
    public Task AnAuxiliaryFocusReportCannotCreateAnEditorOrSourceBinding() => desktop.RunAsync(() =>
    {
        using var hook = new KeyboardHook();
        using var browser = new BrowserInput(new BrowserServer(Dispatcher.CurrentDispatcher), hook);
        browser.Configure(true, "Ctrl+Shift+F9", "Ctrl+Shift+F10", SessionShortcutBindings.Default, true);
        var target = new TargetId("windows-browser", "absent-document:1", Guid.NewGuid());
        var token = new SessionViewToken(target, hook.CommandGeneration, 1);
        var entry = new SessionGlossaryEntry(Guid.NewGuid(), token, 1, []);
        Assert.False(browser.OpenGlossaryEntry(entry));
        browser.SetGlossaryFocus(new(entry.FormId, token, ImeState.Clear));
        Assert.Null(browser.GlossaryFocus);
        Assert.Equal(FocusMember.Other, browser.GetFocus(target));
        browser.CloseGlossaryEntry(entry.FormId);
        Assert.Equal(FocusMember.Other, browser.GetFocus(target));
    });

    private sealed class CountingBroker : IInputBroker
    {
        public int Calls
        {
            get; private set;
        }
        public bool Available => true;
        public int ProcessId => 0;
        public Task StartAsync() => Task.CompletedTask;
        public Task<BrokerResponse> CallAsync(BrokerRequest request, TimeSpan? timeout = null)
        {
            Calls++;
            return Task.FromResult(new BrokerResponse(BrokerProtocol.Version, request.Id, "silent"));
        }
        public void Dispose()
        {
        }
    }
}
