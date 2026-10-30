using System.Windows;
using System.Windows.Automation;
using TypeLay.Runtime;

namespace TypeLay.Windows.Testing;

// Public UIA navigation shared by the actual-release lifecycle and its WPF
// regression. It only reveals existing details/actions; it never enables or invokes an action.
internal static class SettingsPackageActions
{
    internal sealed record PreparationStatus(string State, bool Ready, string? FailureCode);
    internal sealed record VisibilityObservation(double[]? ButtonBounds, double[]? ViewportBounds,
        bool Offscreen, bool? HasClickablePoint, bool? PointInsideViewport);
    internal sealed class ActionDeadlineException() : TimeoutException("The package action did not become available through the settings controls.")
    {
        public string FailureCode => "local-action-deadline";
        public VisibilityObservation? FirstVisibility
        {
            get; init;
        }
        public VisibilityObservation? LastVisibility
        {
            get; init;
        }
        public int VisibilitySamples
        {
            get; init;
        }
    }

    public static PreparationStatus ReadPreparationStatus(AutomationElement settings)
    {
        // Preserve the lifecycle's single status-element query and Ready predicate.
        // Only finite categories survive the observation; never retain UI text.
        var element = settings.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.AutomationIdProperty, "PreparationStatus"));
        string? status = element?.Current.Name;
        var failure = Enum.GetValues<SetupFailureCategory>().Select(SetupFailurePresentation.Create)
            .FirstOrDefault(item => item.Message == status);
        bool ready = status?.StartsWith("本地翻译已就绪", StringComparison.Ordinal) == true;
        string state = failure is not null ? "failed-" + failure.Code : status switch
        {
            null => "missing",
            "正在准备本地翻译组件…" => "preparing",
            "正在暂停，保留已验证的下载进度…" => "pausing",
            "正在启动本地翻译…" => "starting-model",
            "本地翻译尚未就绪" => "not-ready",
            "本地翻译组件尚未准备" => "not-prepared",
            "本地组件安装记录无法读取" => "inventory-unreadable",
            _ when status == SetupFailurePresentation.PausedMessage => "paused",
            _ when ready => "ready",
            _ => "unrecognized"
        };
        return new(state, ready, failure?.Code);
    }

    public static string?[] ReadStatuses(AutomationElement details, IEnumerable<string> packages) =>
        packages.Select(package =>
        {
            var matches = details.FindAll(TreeScope.Descendants, new AndCondition(
                new PropertyCondition(AutomationElement.AutomationIdProperty, "ModelPackage_" + package + "_Status"),
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Text)));
            if (matches.Count > 1)
                throw new InvalidOperationException("package-status-ambiguous");
            return matches.Count == 1 ? matches[0].Current.Name : null;
        }).ToArray();

    public static Task<AutomationElement> OpenDetailsAsync(AutomationElement settings,
        Action<string> stage, CancellationToken ct) =>
        Task.Run(() => OpenDetailsCoreAsync(settings, stage, ct), ct).WaitAsync(ct);

    private static async Task<AutomationElement> OpenDetailsCoreAsync(AutomationElement settings,
        Action<string> stage, CancellationToken ct)
    {
        stage("find-package-details");
        ct.ThrowIfCancellationRequested();
        var details = settings.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.NameProperty, "组件与安装说明"),
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group)));
        stage("check-package-details-enabled");
        if (details is null || !details.Current.IsEnabled)
            throw new InvalidOperationException("package-details-unavailable");
        stage("get-package-details-expand-pattern");
        var expand = (ExpandCollapsePattern)details.GetCurrentPattern(ExpandCollapsePattern.Pattern);
        stage("read-package-details-expand-state");
        if (expand.Current.ExpandCollapseState == ExpandCollapseState.Collapsed)
        {
            ct.ThrowIfCancellationRequested();
            stage("invoke-package-details-expand");
            expand.Expand();
        }
        stage("await-package-details-expanded");
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
        VisibilityObservation? first = null, last = null;
        int samples = 0;
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
            var viewport = FindViewport(button, settings, token);
            while (button.Current.BoundingRectangle.IsEmpty || button.Current.BoundingRectangle.Height <= 0
                || viewport.Current.BoundingRectangle.IsEmpty || viewport.Current.BoundingRectangle.Height <= 0)
                await Task.Delay(40, token);
            bool Visible()
            {
                Rect bounds = button.Current.BoundingRectangle;
                Rect frame = viewport.Current.BoundingRectangle;
                bool offscreen = button.Current.IsOffscreen;
                bool? hasPoint = null, inside = null;
                bool visible = !offscreen && !bounds.IsEmpty && bounds.Width > 0 && bounds.Height > 0;
                if (visible)
                {
                    hasPoint = button.TryGetClickablePoint(out Point point);
                    visible = hasPoint.Value;
                    if (visible)
                    {
                        inside = frame.Contains(point);
                        visible = inside.Value;
                    }
                }
                // Retain only the original predicate's first/last finite observations.
                // No extra UIA queries, retries, text capture or visibility relaxation.
                last = new(FiniteBounds(bounds), FiniteBounds(frame), offscreen, hasPoint, inside);
                first ??= last;
                samples++;
                return visible;
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
        catch (OperationCanceledException) when (limit.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new ActionDeadlineException { FirstVisibility = first, LastVisibility = last, VisibilitySamples = samples };
        }
    }, ct).WaitAsync(ct);

    private static double[]? FiniteBounds(Rect value) => !value.IsEmpty
        && double.IsFinite(value.X) && double.IsFinite(value.Y)
        && double.IsFinite(value.Width) && double.IsFinite(value.Height)
        ? [value.X, value.Y, value.Width, value.Height] : null;

    private static AutomationElement FindViewport(AutomationElement button, AutomationElement settings, CancellationToken ct)
    {
        int[] owner = settings.GetRuntimeId();
        if (owner.Length == 0)
            throw new InvalidOperationException("package-viewport-owner-unknown");
        AutomationElement? viewport = null;
        var visited = new List<int[]>();
        var ancestor = button;
        // Use this button's real ancestry, not an unrelated scrollbar earlier
        // in the settings tree. Keep walking after finding a viewport to prove
        // that it belongs to the supplied settings window.
        for (int depth = 0; depth < 64; depth++)
        {
            ct.ThrowIfCancellationRequested();
            ancestor = TreeWalker.RawViewWalker.GetParent(ancestor);
            if (ancestor is null)
                break;
            int[] identity = ancestor.GetRuntimeId();
            if (identity.Length == 0 || visited.Any(previous => previous.SequenceEqual(identity)))
                break;
            if (identity.SequenceEqual(owner))
                return viewport ?? throw new InvalidOperationException("package-viewport-unavailable");
            visited.Add(identity);
            if (viewport is null && ancestor.TryGetCurrentPattern(ScrollPattern.Pattern, out _))
                viewport = ancestor;
        }
        throw new InvalidOperationException("package-viewport-ancestry-unconfirmed");
    }
}
