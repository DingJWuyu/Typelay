using System.Text.Json;
using TypeLay.Core;
using TypeLay.Runtime;
using Xunit;

namespace TypeLay.App.Tests;

public sealed class BrowserContractTests
{
    private const string Extension = "abcdefghijklmnopabcdefghijklmnop";
    private const string Document = "10000000-0000-0000-0000-000000000001";
    private static BrowserRoute Route => new(Extension, "chrome-document-1", Document, 1, 2, 0, "https:");
    private static JsonElement Authority(long input = 1, long focus = 2, long content = 3,
        string metadata = "approved", string token = "10000000-0000-0000-0000-000000000003", long observed = 100) =>
        JsonSerializer.SerializeToElement(new
        {
            target = Document + ":1",
            metadataKey = metadata,
            contentRevision = content,
            inputRevision = input,
            focusRevision = focus,
            token,
            observedAtMs = observed
        });
    [Fact]
    public void BrowserTraceNoiseCannotConsumeReservedCommandAndReceiptBudget()
    {
        var budget = new BrowserTraceBudget();
        for (int index = 0; index < 224; index++)
            Assert.True(budget.TryTake("browser-observation"));
        Assert.False(budget.GeneralAvailable);
        Assert.True(budget.CommandsAvailable);
        Assert.False(budget.TryTake("browser-owner"));
        Assert.False(budget.TryTake("browser-direct-unrecognized"));
        string[] commands = ["browser-command-approved", "browser-command-path", "browser-direct-Verified",
            "browser-direct-RejectedBeforeMutation", "browser-direct-OutcomeUnknown",
            "browser-accept-Verified", "browser-accept-RejectedBeforeMutation", "browser-accept-OutcomeUnknown"];
        for (int index = 0; index < 32; index++)
            Assert.True(budget.TryTake(commands[index % commands.Length]));
        Assert.False(budget.CommandsAvailable);
        foreach (string operation in commands)
            Assert.False(budget.TryTake(operation));
        Assert.False(budget.TryTake("browser-observation"));
    }
    [Fact]
    public void RouteRequiresAuthenticatedExtensionTopDocumentAndNormalWebScheme()
    {
        string origin = "chrome-extension://" + Extension + "/";
        Assert.True(Route.Valid(origin));
        Assert.True(Route.Contains(Document + ":1"));
        Assert.False(Route.Contains("another-document:1"));
        Assert.False((Route with
        {
            DocumentId = ""
        }).Valid(origin));
        Assert.False((Route with
        {
            DocumentToken = "invalid"
        }).Valid(origin));
        Assert.False((Route with
        {
            FrameId = 1
        }).Valid(origin));
        Assert.False((Route with
        {
            Scheme = "chrome:"
        }).Valid(origin));
        Assert.False((Route with
        {
            ExtensionId = "invalid"
        }).Valid(origin));
        Assert.False(Route.Valid("chrome-extension://pppppppppppppppppppppppppppppppp/"));
    }
    [Fact]
    public void SameSourceRenewalAllowsNewTokenAndTimeButNotAnyChangedWatermark()
    {
        var original = Authority();
        Assert.True(BrowserInput.SameAuthority(original, Authority(token: "10000000-0000-0000-0000-000000000004", observed: 120)));
        Assert.False(BrowserInput.SameAuthority(original, Authority(input: 2)));
        Assert.False(BrowserInput.SameAuthority(original, Authority(focus: 3)));
        Assert.False(BrowserInput.SameAuthority(original, Authority(content: 4)));
        Assert.False(BrowserInput.SameAuthority(original, Authority(metadata: "changed")));
        Assert.False(BrowserInput.SameAuthority(original, Authority(token: "invalid")));
        Assert.False(BrowserInput.SameAuthority(original, default));
    }
    [Fact]
    public void ContinuousCapturesKeepTheirOwnExactObservationAndRetireOnlyTheOldestArrival()
    {
        var history = new BrowserObservationHistory();
        var target = new TargetId("windows-browser", Document + ":1", Guid.NewGuid());
        var generation = Guid.NewGuid();
        BrowserObservation Record(long at) => new(null!, Route, Authority(observed: at), target, at, 0,
            new(0, 0, 0, 0, 0, generation), new(target, generation, SessionShortcutBindings.Default, true, true));
        for (int at = 1; at <= 130; at++)
        {
            // Only retention is under test. No authenticated peer or browser
            // authority is claimed by these in-memory records.
            var current = Record(at);
            history.Remember(current);
            Assert.Same(current, history.Observation(target, at));
            Assert.Same(current, history.Latest(target));
        }
        Assert.Null(history.Observation(target, 1));
        Assert.Null(history.Observation(target, 2));
        Assert.NotNull(history.Observation(target, 3));
        var renewed = Record(3);
        history.Remember(renewed);
        Assert.Same(renewed, history.Observation(target, 3));
        Assert.Same(renewed, history.Latest(target));
        var other = Record(131) with
        {
            Target = target with
            {
                Value = Document + ":2"
            }
        };
        history.Remember(other);
        Assert.Null(history.Observation(target, 4));
        Assert.Same(renewed, history.Observation(target, 3));
        Assert.Same(renewed, history.Latest(target));
        Assert.Same(other, history.Latest(other.Target));
        Assert.NotNull(history.Observation(target, 130));
    }
    [Fact]
    public void FailedHotkeyReleaseCannotBecomePageOwnershipAndSameConfigurationCanRestore()
    {
        var registered = new HashSet<int>();
        bool refuseDirectRelease = true, refuseWakeRegistration = false;
        using var hotkeys = new OnDemandHotkeys(0,
            (_, id, _, _) => !(id == 0x5451 && refuseWakeRegistration) && registered.Add(id),
            (_, id) => id == 0x5452 && refuseDirectRelease ? false : registered.Remove(id));
        hotkeys.Configure(true, "Ctrl+Shift+F9", "Ctrl+Shift+F10");
        Assert.True(hotkeys.Registered);
        Assert.False(hotkeys.Suspend());
        Assert.True(hotkeys.HasRegistration);
        Assert.Equal([0x5452], registered);
        Assert.True(hotkeys.Resume());
        Assert.Equal(2, registered.Count);
        refuseDirectRelease = false;
        Assert.True(hotkeys.Suspend());
        Assert.Empty(registered);
        Assert.True(hotkeys.Resume()); // Same desired tuple must really re-register.
        Assert.Equal(2, registered.Count);
        Assert.True(hotkeys.Suspend());
        refuseWakeRegistration = true;
        Assert.False(hotkeys.Resume());
        Assert.False(hotkeys.Registered);
        Assert.True(hotkeys.HasRegistration);
        Assert.Equal([0x5452], registered);
        Assert.Contains("唤醒翻译 UI", hotkeys.Status);
        Assert.True(hotkeys.Suspend());
        Assert.Empty(registered);
    }
    [Fact]
    public void BrowserWireUsesSharedPageEnumSpelling()
    {
        var frame = JsonSerializer.SerializeToElement(new
        {
            state = SessionState.EditingTranslation,
            ime = ImeState.Clear
        }, BrowserServer.Json);
        Assert.Equal("EditingTranslation", frame.GetProperty("state").GetString());
        Assert.Equal("Clear", frame.GetProperty("ime").GetString());
    }
}
