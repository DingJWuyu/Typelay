using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using TypeLay.App;
using Xunit;
using N = TypeLay.Platform.Windows.NativeMethods;

namespace TypeLay.App.Tests;

public sealed partial class SetupRenderingTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public Task ANewOnboardingShowInsideTheOldVisibilityCallbackKeepsItsOwnActivationPolicy(bool inheritedShow, bool activate) => desktop.RunAsync(async () =>
    {
        await using var scene = new TrayPreparationScene("en", "light");
        var window = new OnboardingWindow(scene.Settings, scene.Model, scene.Host);
        var destination = new Window { Width = 300, Height = 200 };
        bool reopened = false;
        void Visible(object sender, DependencyPropertyChangedEventArgs args)
        {
            if (!(bool)args.NewValue || reopened)
                return;
            reopened = true;
            window.Hide();
            destination.Show();
            Assert.True(destination.Activate());
            window.ShowActivated = activate;
            if (inheritedShow)
                ((Window)window).Show();
            else
                window.ShowOrActivate();
            AssertOnboardingShown(window, destination, activate);
        }
        window.IsVisibleChanged += Visible;
        try
        {
            window.ShowOrActivate();
            Assert.True(reopened);
            AssertOnboardingShown(window, destination, activate);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            AssertOnboardingShown(window, destination, activate);
            Assert.Equal(activate, window.ShowActivated);
            Assert.Equal(activate, window.ReadLocalValue(Window.ShowActivatedProperty));
            Assert.False(scene.Settings.Value.OnboardingComplete);
            Assert.Equal(0, scene.Attempts);
        }
        finally
        {
            window.IsVisibleChanged -= Visible;
            try
            {
                window.Close();
            }
            finally { destination.Close(); }
        }
    });

    [Theory]
    [InlineData("false", false)]
    [InlineData("true", true)]
    [InlineData("clear", true)]
    public Task AHiddenRetiredOnboardingShowPreservesNewPolicyAndAllowsALaterInheritedShow(string policy, bool activate) => desktop.RunAsync(async () =>
    {
        await using var scene = new TrayPreparationScene("en", "light");
        var window = new OnboardingWindow(scene.Settings, scene.Model, scene.Host) { ShowActivated = false };
        var destination = new Window { Width = 300, Height = 200 };
        bool hidden = false;
        void Visible(object sender, DependencyPropertyChangedEventArgs args)
        {
            if (!(bool)args.NewValue || hidden)
                return;
            hidden = true;
            window.Hide();
            if (policy == "clear")
                window.ClearValue(Window.ShowActivatedProperty);
            else
                window.ShowActivated = activate;
            destination.Show();
            Assert.True(destination.Activate());
        }
        window.IsVisibleChanged += Visible;
        try
        {
            window.ShowOrActivate();
            Assert.True(hidden);
            AssertOnboardingHidden(window, destination);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            AssertOnboardingHidden(window, destination);
            Assert.Equal(activate, window.ShowActivated);
            Assert.Equal(policy == "clear" ? DependencyProperty.UnsetValue : activate,
                window.ReadLocalValue(Window.ShowActivatedProperty));
            ((Window)window).Show();
            AssertOnboardingShown(window, destination, activate);
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            AssertOnboardingShown(window, destination, activate);
        }
        finally
        {
            window.IsVisibleChanged -= Visible;
            try
            {
                window.Close();
            }
            finally { destination.Close(); }
        }
    });

    [Fact]
    public Task ClosingOnboardingInsideItsFirstVisibilityCallbackDoesNotReviveOrActivateIt() => desktop.RunAsync(async () =>
    {
        await using var scene = new TrayPreparationScene("en", "light");
        var window = new OnboardingWindow(scene.Settings, scene.Model, scene.Host);
        var destination = new Window { Width = 300, Height = 200 };
        bool closed = false;
        nint handle = 0;
        void Visible(object sender, DependencyPropertyChangedEventArgs args)
        {
            if (!(bool)args.NewValue || closed)
                return;
            closed = true;
            handle = new WindowInteropHelper(window).Handle;
            window.Close();
            destination.Show();
            Assert.True(destination.Activate());
        }
        window.IsVisibleChanged += Visible;
        try
        {
            window.ShowOrActivate();
            Assert.True(closed);
            Assert.NotEqual(0, handle);
            Assert.False(N.IsWindow(handle));
            Assert.True(destination.IsActive);
            window.ShowOrActivate(); // The closed public entry remains a no-op.
            await Dispatcher.Yield(DispatcherPriority.ContextIdle);
            Assert.False(N.IsWindow(handle));
            Assert.True(destination.IsActive);
        }
        finally
        {
            window.IsVisibleChanged -= Visible;
            try
            {
                window.Close();
            }
            finally { destination.Close(); }
        }
    });

    [Fact]
    public Task AVisibilityCallbackFailureIsNotReplacedByRetiredShowPolicyCleanup() => desktop.RunAsync(async () =>
    {
        await using var scene = new TrayPreparationScene("en", "light");
        var window = new OnboardingWindow(scene.Settings, scene.Model, scene.Host);
        var failure = new InvalidOperationException("controlled visibility callback");
        var cleanupFailure = new InvalidOperationException("controlled policy observer");
        bool hidden = false;
        var descriptor = System.ComponentModel.DependencyPropertyDescriptor.FromProperty(Window.ShowActivatedProperty, typeof(Window));
        EventHandler changed = (_, _) =>
        {
            if (hidden && window.ShowActivated)
                throw cleanupFailure;
        };
        void Visible(object sender, DependencyPropertyChangedEventArgs args)
        {
            if (!(bool)args.NewValue || hidden)
                return;
            hidden = true;
            window.Hide();
            throw failure;
        }
        descriptor.AddValueChanged(window, changed);
        window.IsVisibleChanged += Visible;
        try
        {
            Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => window.ShowOrActivate()));
            Assert.True(hidden);
            Assert.False(window.IsVisible);
            Assert.True(window.ShowActivated);
            Assert.Equal(DependencyProperty.UnsetValue, window.ReadLocalValue(Window.ShowActivatedProperty));
        }
        finally
        {
            window.IsVisibleChanged -= Visible;
            descriptor.RemoveValueChanged(window, changed);
            window.Close();
        }
    });

    private static void AssertOnboardingHidden(OnboardingWindow window, Window destination)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        Assert.NotEqual(0, handle);
        Assert.False(window.IsVisible);
        Assert.False(N.IsWindowVisible(handle));
        Assert.False(window.IsActive);
        Assert.True(destination.IsActive);
        Assert.Equal(new WindowInteropHelper(destination).Handle, N.GetForegroundWindow());
    }

    private static void AssertOnboardingShown(OnboardingWindow window, Window destination, bool activate)
    {
        nint handle = new WindowInteropHelper(window).Handle;
        Assert.NotEqual(0, handle);
        Assert.True(window.IsVisible);
        Assert.True(N.IsWindowVisible(handle));
        Assert.Equal(activate, window.IsActive);
        Assert.Equal(!activate, destination.IsActive);
        Assert.Equal(activate ? handle : new WindowInteropHelper(destination).Handle, N.GetForegroundWindow());
    }
}
