using TypeLay.App;
using Xunit;

namespace TypeLay.App.Tests;

public sealed class HotkeyRegistrationTests
{
    [Fact]
    public void FailedRegistrationCanRetryTheSavedChordsAfterTheExternalOwnerReleasesThem()
    {
        // Thread-owned registrations do not require or change foreground focus.
        const uint modifiers = 2 | 4 | 0x4000;
        const uint key = 0x87; // F24, kept away from the product defaults.
        const int blocker = 0x5400;
        Assert.True(Native.RegisterHotKey(0, blocker, modifiers, key));
        try
        {
            using var hotkeys = new OnDemandHotkeys(0);
            hotkeys.Configure(true, "Ctrl+Shift+F24", "Ctrl+Shift+F23");
            Assert.DoesNotContain(hotkeys.Bindings, binding => binding.Action == "wake");
            Assert.True(hotkeys.RegistrationFailed);
            Assert.Contains("唤醒翻译 UI", hotkeys.Status);
            Assert.False(hotkeys.Resume());
            Assert.True(hotkeys.RegistrationFailed);
            Assert.True(Native.UnregisterHotKey(0, blocker));
            hotkeys.Configure(true, "Ctrl+Shift+F24", "Ctrl+Shift+F23");
            Assert.True(hotkeys.RegistrationFailed); // Merely saving the same tuple is not a retry.
            Assert.True(hotkeys.Resume());
            Assert.False(hotkeys.RegistrationFailed);
            Assert.Equal(2, hotkeys.Bindings.Length);
            hotkeys.Configure(false, "Ctrl+Shift+F24", "Ctrl+Shift+F23");
            Assert.Empty(hotkeys.Bindings);
            Assert.False(hotkeys.RegistrationFailed);
            Assert.True(Native.RegisterHotKey(0, blocker + 1, modifiers, 0x86));
            Native.UnregisterHotKey(0, blocker + 1);
        }
        finally { Native.UnregisterHotKey(0, blocker); }
    }

    [Fact]
    public void ShiftFunctionCommandsReserveOnlyTheirExactModifierCombination()
    {
        var attempts = new List<(uint Modifiers, uint Key)>();
        using var hotkeys = new OnDemandHotkeys(0, (_, _, modifiers, key) =>
        {
            attempts.Add((modifiers, key));
            return true;
        }, (_, _) => true);
        hotkeys.Configure(true, "Shift+F1", "Shift+F24");
        Assert.True(hotkeys.Registered);
        Assert.Equal(new[] { (4u | 0x4000u, 0x70u), (4u | 0x4000u, 0x87u) }, attempts);
        Assert.Collection(hotkeys.Bindings,
            wake => Assert.Equal(new RegisteredShortcut("wake", 0x70, TypeLay.Core.ShortcutModifiers.Shift), wake),
            direct => Assert.Equal(new RegisteredShortcut("direct", 0x87, TypeLay.Core.ShortcutModifiers.Shift), direct));
        hotkeys.Configure(false, "Shift+F1", "Shift+F24");
        Assert.Empty(hotkeys.Bindings);
        Assert.False(hotkeys.HasRegistration);
    }

    [Fact]
    public void RetryKeepsSuccessfulReservationsAndDoesNotProbeUnrelatedChords()
    {
        var attempts = new List<uint>();
        bool blocked = true;
        using var hotkeys = new OnDemandHotkeys(0, (_, _, _, key) =>
        {
            attempts.Add(key);
            return key != 0x87 || !blocked;
        }, (_, _) => true);
        hotkeys.Configure(true, "Ctrl+Shift+F24", "Ctrl+Shift+F23");
        Assert.True(hotkeys.RegistrationFailed);
        Assert.Equal(new uint[] { 0x87, 0x86 }, attempts);
        attempts.Clear();
        Assert.False(hotkeys.Resume());
        Assert.Equal(new uint[] { 0x87 }, attempts);
        Assert.Equal("direct", Assert.Single(hotkeys.Bindings).Action);
        attempts.Clear();
        blocked = false;
        Assert.True(hotkeys.Resume());
        Assert.Equal(new uint[] { 0x87 }, attempts);
        Assert.False(hotkeys.RegistrationFailed);
        Assert.True(hotkeys.Suspend());
        Assert.False(hotkeys.RegistrationFailed);
    }

    [Fact]
    public void UnconfirmedReleaseRetiresTheOldSavedTupleInsteadOfMakingItRetryable()
    {
        bool refuseRelease = true;
        var attempts = new List<uint>();
        using var hotkeys = new OnDemandHotkeys(0, (_, _, _, key) =>
        {
            attempts.Add(key);
            return key != 0x87;
        }, (_, _) => !refuseRelease);
        hotkeys.Configure(true, "Ctrl+Shift+F24", "Ctrl+Shift+F23");
        Assert.True(hotkeys.RegistrationFailed);
        hotkeys.Configure(true, "Ctrl+Shift+F22", "Ctrl+Shift+F21");
        Assert.False(hotkeys.RegistrationFailed);
        Assert.Contains("注销未确认", hotkeys.Status);
        attempts.Clear();
        Assert.False(hotkeys.Resume());
        Assert.Empty(attempts);
        Assert.Empty(hotkeys.Bindings);
        refuseRelease = false;
        Assert.True(hotkeys.Suspend());
        Assert.True(hotkeys.Resume());
        Assert.Empty(attempts);
        Assert.False(hotkeys.RegistrationFailed);
        Assert.DoesNotContain("已启用", hotkeys.Status);
    }

    [Fact]
    public void InvalidOrIntentionallyDeferredBindingsDoNotOfferRegistrationRetry()
    {
        using var hotkeys = new OnDemandHotkeys(0, (_, _, _, _) => true, (_, _) => true);
        hotkeys.Configure(true, "not-a-shortcut", "Ctrl+Shift+F23");
        Assert.False(hotkeys.RegistrationFailed);
        hotkeys.Configure(true, "Ctrl+Shift+F24", "Ctrl+Shift+F23", registerNow: false);
        Assert.False(hotkeys.RegistrationFailed);
        Assert.Empty(hotkeys.Bindings);
    }
}
