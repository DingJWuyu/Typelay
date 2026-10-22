using System.IO;
using System.Text.Json;
using System.Windows.Automation;
using TypeLay.App;
using Xunit;
using Forms = System.Windows.Forms;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class ShellTrayRegistrationTests(WpfDesktopFixture desktop, ITestOutputHelper output)
{
    // This probes NotifyIcon/API/Shell discovery, not registration of the production
    // instance. Hidden overflow is deliberately never opened or activated.
    [Fact]
    public Task BothNotificationCreationOrdersExposeTheirExactSyntheticNames() => desktop.RunAsync(async () =>
    {
        using var total = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var results = new List<Observation>();
        try
        {
            foreach (bool textFirst in new[] { false, true })
            {
                total.Token.ThrowIfCancellationRequested();
                var result = new Observation { Order = textFirst ? "text-visible" : "visible-text" };
                results.Add(result);
                string name = "TypeLay · shell-order-" + result.Order;
                Forms.NotifyIcon? tray = null;
                System.Drawing.Icon? icon = null;
                try
                {
                    using var observe = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
                    observe.CancelAfter(TimeSpan.FromSeconds(5));
                    var before = await SampleAsync(name, observe.Token);
                    result.Before = before;
                    observe.Token.ThrowIfCancellationRequested();
                    Assert.Equal(0, before.ExactMatches);
                    icon = Program.MakeTrayIcon();
                    observe.Token.ThrowIfCancellationRequested();
                    tray = new Forms.NotifyIcon { Icon = icon };
                    tray.Disposed += (_, _) => result.DisposedEvent = true;
                    if (textFirst)
                        tray.Text = name;
                    observe.Token.ThrowIfCancellationRequested();
                    tray.Visible = true;
                    if (!textFirst)
                        tray.Text = name;
                    result.Created = true;
                    while (true)
                    {
                        var sample = await SampleAsync(name, observe.Token);
                        result.First ??= sample;
                        result.Last = sample;
                        Assert.InRange(sample.ExactMatches, 0, 1);
                        if (sample.VisibleExactMatches == 1)
                        {
                            result.Found = true;
                            break;
                        }
                        await Task.Delay(50, observe.Token);
                    }
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    result.ErrorType = error.GetType().Name;
                }
                finally
                {
                    // Never allow observation cancellation to skip owned cleanup.
                    try
                    {
                        if (tray is not null)
                        {
                            try
                            {
                                tray.Visible = false;
                            }
                            finally { tray.Dispose(); }
                            result.DisposeReturned = true;
                        }
                    }
                    catch (Exception error) when (error is not OutOfMemoryException)
                    {
                        result.CleanupErrorType = error.GetType().Name;
                    }
                    finally
                    {
                        try
                        {
                            icon?.Dispose();
                            result.IconDisposeReturned = icon is not null;
                        }
                        catch (Exception error) when (error is not OutOfMemoryException)
                        {
                            result.CleanupErrorType = error.GetType().Name;
                        }
                    }
                }
                if (result.CleanupErrorType is not null || (tray is not null && (!result.DisposeReturned || !result.DisposedEvent)))
                    break;
                try
                {
                    result.After = await SampleAsync(name, total.Token);
                    // Absence is only from this accessible tree, not proof about
                    // the unopened overflow or native Shell registration state.
                    if (result.After.ExactMatches != 0)
                        break;
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    result.CleanupErrorType = error.GetType().Name;
                    break;
                }
            }
        }
        finally
        {
            string json = JsonSerializer.Serialize(new
            {
                boundary = "owned-notifyicon-api-shell-accessible-tree",
                unopenedOverflowObserved = false,
                results
            }, new JsonSerializerOptions { WriteIndented = true });
            // Early failure upload includes TRX; the final upload also keeps JSON.
            output.WriteLine(json);
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "TypeLay.slnx")))
                repository = repository.Parent;
            Assert.NotNull(repository);
            string path = Path.Combine(repository.FullName, "artifacts/test-results/shell-tray-registration.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json + "\n");
        }
        Assert.Equal(2, results.Count);
        Assert.All(results, result =>
        {
            Assert.Null(result.ErrorType);
            Assert.True(result.Found, "Exact synthetic icon was not discoverable in the current accessible Shell tree.");
            Assert.True(result.DisposeReturned && result.DisposedEvent && result.IconDisposeReturned);
            Assert.Null(result.CleanupErrorType);
            Assert.NotNull(result.After);
            Assert.Equal(0, result.After.ExactMatches);
        });
    });

    // UIA's native calls cannot be cancelled. WaitAsync bounds the caller; a
    // late worker remains read-only and never writes observations or acts on UI.
    private static Task<Sample> SampleAsync(string expected, CancellationToken ct) => Task.Run(() =>
    {
        ct.ThrowIfCancellationRequested();
        var roots = AutomationElement.RootElement.FindAll(TreeScope.Children, new OrCondition(
            new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd"),
            new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_SecondaryTrayWnd"),
            new PropertyCondition(AutomationElement.ClassNameProperty, "NotifyIconOverflowWindow"),
            new PropertyCondition(AutomationElement.ClassNameProperty, "TopLevelWindowForOverflowXamlIsland")));
        int empty = 0, other = 0, product = 0, exact = 0, visibleExact = 0;
        foreach (AutomationElement root in roots)
        {
            ct.ThrowIfCancellationRequested();
            foreach (AutomationElement button in root.FindAll(TreeScope.Descendants,
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)))
            {
                ct.ThrowIfCancellationRequested();
                var current = button.Current;
                string name = current.Name;
                if (name.Length == 0)
                    empty++;
                else if (name == expected && name.StartsWith("TypeLay · ", StringComparison.Ordinal))
                {
                    exact++;
                    if (!current.IsOffscreen)
                        visibleExact++;
                }
                else if (name.StartsWith("TypeLay · ", StringComparison.Ordinal))
                    product++;
                else
                    other++;
            }
        }
        return new Sample(roots.Count, empty, other, product, exact, visibleExact);
    }, ct).WaitAsync(ct);

    private sealed record Sample(int Roots, int EmptyNames, int NonemptyOtherNames,
        int ProductPrefixNames, int ExactMatches, int VisibleExactMatches);
    private sealed class Observation
    {
        public string Order { get; init; } = "";
        public bool Created
        {
            get; set;
        }
        public bool Found
        {
            get; set;
        }
        public bool DisposedEvent
        {
            get; set;
        }
        public bool DisposeReturned
        {
            get; set;
        }
        public bool IconDisposeReturned
        {
            get; set;
        }
        public string? ErrorType
        {
            get; set;
        }
        public string? CleanupErrorType
        {
            get; set;
        }
        public Sample? Before
        {
            get; set;
        }
        public Sample? First
        {
            get; set;
        }
        public Sample? Last
        {
            get; set;
        }
        public Sample? After
        {
            get; set;
        }
    }
}
