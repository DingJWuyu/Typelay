using System.Windows;
using System.Windows.Automation;
using TypeLay.Runtime;

namespace TypeLay.Windows.Testing;

// Public UIA navigation shared by the actual-release lifecycle and its WPF
// regression. It only reveals existing details/actions; it never enables or invokes an action.
internal static class SettingsPackageActions
{
    public static Task<AutomationElement> OpenDetailsAsync(AutomationElement settings,
        Action<string> stage, CancellationToken ct) =>
        Task.Run(() => OpenDetailsCoreAsync(settings, stage, ct), ct).WaitAsync(ct);

    private static async Task<AutomationElement> OpenDetailsCoreAsync(AutomationElement settings,
        Action<string> stage, CancellationToken ct)
    {
        stage("expand-package-details");
        ct.ThrowIfCancellationRequested();
        var details = settings.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.NameProperty, "组件与安装说明"),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group)));
        if (details is null || !details.Current.IsEnabled)
            throw new InvalidOperationException("package-details-unavailable");
        var expand = (ExpandCollapsePattern)details.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        if (expand.Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
        {
            ct.ThrowIfCancellationRequested();
            expand.Expand();
        }
        while (expand.Current.ExpandCollapseState != ExpandCollapseState.Expanded)
            await Task.Delay(40, ct);
        ct.ThrowIfCancellationRequested();
        return details;
    }

    public static Task<AutomationElement> FindAsync(AutomationElement settings, string package,
        ModelPackageOperation operation, Action<string> stage, CancellationToken ct) => Task.Run(async () =>
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(5));
        var token = limit.Token;
        try
        {
            var details = await OpenDetailsCoreAsync(settings, stage, token);

            stage("find-package-action-missing");
            AutomationElement? button;
            while ((button = details.FindFirst(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.AutomationIdProperty, "ModelPackage_" + package + "_" + operation),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))) is null)
                await Task.Delay(40, token);
            if (!button.Current.IsEnabled)
            {
                stage("package-action-disabled");
                throw new InvalidOperationException("package-action-disabled");
            }

            stage("scroll-package-action-into-view");
            var viewport = settings.FindFirst(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ClassNameProperty, "ScrollViewer"));
            if (viewport is null)
                throw new InvalidOperationException("package-viewport-unavailable");
            while (button.Current.BoundingRectangle.IsEmpty || button.Current.BoundingRectangle.Height <= 0
                || viewport.Current.BoundingRectangle.IsEmpty || viewport.Current.BoundingRectangle.Height <= 0)
                await Task.Delay(40, token);
            bool Visible()
            {
                Rect bounds = button.Current.BoundingRectangle;
                Rect frame = viewport.Current.BoundingRectangle;
                return !button.Current.IsOffscreen && !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0
                    && button.TryGetClickablePoint(out Point point) && frame.Contains(point);
            }
            if (!Visible())
            {
                var scroll = (ScrollPattern)viewport.GetCurrentPattern(ScrollPattern.Pattern);
                var current = scroll.Current;
                if (!current.VerticallyScrollable || current.VerticalViewSize is <= 0 or >= 100)
                    throw new InvalidOperationException("package-action-not-scrollable");
                Rect bounds = button.Current.BoundingRectangle;
                Rect frame = viewport.Current.BoundingRectangle;
                double range = frame.Height * (100 / current.VerticalViewSize - 1);
                double delta = bounds.Top + bounds.Height / 2 - frame.Top - frame.Height / 2;
                token.ThrowIfCancellationRequested();
                scroll.SetScrollPercent(ScrollPattern.NoScroll,
                    Math.Clamp(current.VerticalScrollPercent + 100 * delta / range, 0, 100));
            }
            stage("package-action-not-visible");
            while (!Visible())
                await Task.Delay(40, token);
            if (!button.Current.IsEnabled)
            {
                stage("package-action-disabled");
                throw new InvalidOperationException("package-action-disabled");
            }
            token.ThrowIfCancellationRequested();
            return button;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("The package action did not become available through the settings controls.");
        }
    }, ct).WaitAsync(ct);
}
