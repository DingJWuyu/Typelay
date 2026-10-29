using System.Diagnostics;
using System.Text.Json;
using System.Windows.Automation;
using TypeLay.Runtime;

internal static class PublicOnboardingActions
{
    // Drive the shipped first-run UI, never mark setup complete through a file
    // write or an internal callback. Optional practice cards remain a separate
    // lesson; the wizard's fourth-page Get started action completes setup.
    internal static async Task CompleteAsync(Process app, Func<UserSettings?> readSettings,
        Action<string> stage, CancellationToken ct)
    {
        var before = readSettings() ?? throw new InvalidOperationException("onboarding-settings-unreadable");
        Require(!before.OnboardingComplete, "onboarding-already-complete");
        AutomationElement? onboarding = null;
        stage("find-public-onboarding");
        using (var discovery = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            discovery.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                await WaitAsync(async () =>
                {
                    Require(!app.HasExited, "shipped-app-exited");
                    onboarding = await UiAsync(() => AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(
                        new PropertyCondition(AutomationElement.ProcessIdProperty, app.Id),
                        new PropertyCondition(AutomationElement.AutomationIdProperty, "TypeLayOnboarding"),
                        new PropertyCondition(AutomationElement.IsOffscreenProperty, false))), discovery.Token);
                    return onboarding is not null;
                }, discovery.Token);
            }
            catch (OperationCanceledException) when (discovery.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new InvalidOperationException("onboarding-discovery-deadline");
            }
        }
        string[] headings = ["欢迎使用 TypeLay", "你想译成什么语言？", "下载本地翻译模型", "试一试"];
        for (int step = 0; step < headings.Length; step++)
        {
            int currentStep = step + 1;
            stage("onboarding-step-" + currentStep + "-visible");
            await WaitAsync(async () => await UiAsync(() =>
            {
                Require(!app.HasExited, "shipped-app-exited");
                var heading = Find(onboarding!, AutomationElement.NameProperty, headings[step], ControlType.Text);
                return heading is not null && !heading.Current.IsOffscreen;
            }, ct), ct);
            stage(step == 2 ? "onboarding-await-model-ready" : "onboarding-step-" + currentStep + "-next");
            AutomationElement? next = null;
            await WaitAsync(async () => await UiAsync(() =>
            {
                next = Find(onboarding!, AutomationElement.AutomationIdProperty, "OnboardingNext", ControlType.Button);
                return next is not null && next.Current.IsEnabled && !next.Current.IsOffscreen;
            }, ct), ct);
            await UiAsync(() =>
            {
                ct.ThrowIfCancellationRequested();
                Require(next!.Current.Name == (step == 3 ? "开始使用" : "继续"), "onboarding-next-action-mismatch");
                ((InvokePattern)next.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
            }, ct);
        }
        stage("await-public-onboarding-save");
        await WaitAsync(() => Task.FromResult(readSettings()?.OnboardingComplete == true), ct);
        await WaitAsync(async () => await UiAsync(() => AutomationElement.RootElement.FindFirst(TreeScope.Children, new AndCondition(
            new PropertyCondition(AutomationElement.ProcessIdProperty, app.Id),
            new PropertyCondition(AutomationElement.AutomationIdProperty, "TypeLayOnboarding"),
            new PropertyCondition(AutomationElement.IsOffscreenProperty, false))) is null, ct), ct);
        var after = readSettings() ?? throw new InvalidOperationException("onboarding-settings-unreadable");
        // Compare the complete persisted preference contract, including newer
        // fields and preserved extension data. Only the public finish action's
        // OnboardingComplete transition is allowed.
        Require(JsonSerializer.Serialize(after with
        {
            OnboardingComplete = before.OnboardingComplete
        })
            == JsonSerializer.Serialize(before), "onboarding-changed-unselected-preferences");
    }

    private static void Require(bool condition, string code)
    {
        if (!condition)
            throw new InvalidOperationException(code);
    }
    private static AutomationElement? Find(AutomationElement parent, AutomationProperty property, string value, ControlType type) =>
        parent.FindFirst(TreeScope.Descendants, new AndCondition(new PropertyCondition(property, value),
            new PropertyCondition(AutomationElement.ControlTypeProperty, type)));
    private static Task<T> UiAsync<T>(Func<T> action, CancellationToken ct) => Task.Run(action, ct).WaitAsync(ct);
    private static Task UiAsync(Action action, CancellationToken ct) => Task.Run(action, ct).WaitAsync(ct);
    private static async Task WaitAsync(Func<Task<bool>> ready, CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (await ready())
                return;
            await Task.Delay(40, ct);
        }
    }
}
