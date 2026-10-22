using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using TypeLay.App;
using TypeLay.Core;
using TypeLay.Windows.Contracts;
using Xunit;

namespace TypeLay.App.Tests;

// A real WPF text provider exposes the editable-combobox role used by Chromium's
// realbox. Production Broker IPC performs capture; this is not a Chrome/NTP test.
[Collection("Desktop")]
public sealed class EditableComboBoxCaptureTests(WpfDesktopFixture desktop)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EditableComboBoxPreviewsCommittedTextButNeverGrantsWritePermission(bool captureDirect)
    {
        await desktop.RunAsync(async () =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            using var broker = new BrokerClient();
            await broker.StartAsync().WaitAsync(deadline.Token);
            var editable = Field("editable", "ordinary search without punctuation");
            var readOnly = Field("readonly", "read only text");
            readOnly.IsReadOnly = true;
            var password = Field("protected", "synthetic protected value");
            password.Protected = true;
            var selector = new ComboBox { IsEditable = false, Height = 40, Margin = new(8) };
            selector.Items.Add("Choice A");
            selector.SelectedIndex = 0;
            AutomationProperties.SetAutomationId(selector, "selector");
            AutomationProperties.SetName(selector, "Synthetic choices");
            var panel = new StackPanel();
            foreach (var field in new Control[] { editable, readOnly, password, selector })
                panel.Children.Add(field);
            var window = new Window
            {
                Title = "TypeLay editable combobox capture fixture",
                Width = 640,
                Height = 380,
                Content = panel
            };
            try
            {
                window.Show();
                async Task<BrokerResponse> Capture(Control field)
                {
                    Assert.True(window.Activate());
                    Assert.True(field.Focus());
                    Assert.Same(field, Keyboard.FocusedElement);
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    // Confirm the actual UIA type and focus, independently of the Broker.
                    var observed = await Task.Run(() =>
                    {
                        var focused = AutomationElement.FocusedElement;
                        return (focused.Current.ControlType, focused.Current.AutomationId, focused.Current.ProcessId);
                    }, deadline.Token).WaitAsync(deadline.Token);
                    Assert.Equal(ControlType.ComboBox, observed.ControlType);
                    Assert.Equal(AutomationProperties.GetAutomationId(field), observed.AutomationId);
                    Assert.Equal(Environment.ProcessId, observed.ProcessId);
                    Assert.Equal(new WindowInteropHelper(window).Handle, Native.GetForegroundWindow());
                    return await broker.CallAsync(new(BrokerProtocol.Version, 0, "capture", "", CaptureDirect: captureDirect))
                        .WaitAsync(deadline.Token);
                }
                var first = (await Capture(editable)).Snapshot;
                Assert.NotNull(first);
                Assert.Equal(editable.Text, first.PreviewText ?? first.Text);
                Assert.False(first.WriteSupported);
                Assert.False(first.DirectWriteSupported);
                editable.AppendText(" continues");
                var updated = (await Capture(editable)).Snapshot;
                Assert.NotNull(updated);
                Assert.Equal(editable.Text, updated.PreviewText ?? updated.Text);
                Assert.True(updated.InputEpoch > first.InputEpoch);
                Assert.False(updated.WriteSupported);
                Assert.False(updated.DirectWriteSupported);
                foreach (var rejected in new Control[] { readOnly, password, selector })
                {
                    var reply = await Capture(rejected);
                    Assert.Equal("silent", reply.Status);
                    Assert.Null(reply.Snapshot);
                }
                Assert.Equal("ordinary search without punctuation continues", editable.Text);
                Assert.Equal("read only text", readOnly.Text);
                Assert.Equal("synthetic protected value", password.Text);
            }
            finally { window.Close(); }
        });
    }

    private static ComboBoxText Field(string id, string text)
    {
        var field = new ComboBoxText { Text = text, Height = 40, Margin = new(8) };
        AutomationProperties.SetAutomationId(field, id);
        AutomationProperties.SetName(field, "Synthetic search");
        return field;
    }

    private sealed class ComboBoxText : TextBox
    {
        internal bool Protected
        {
            get; set;
        }
        protected override AutomationPeer OnCreateAutomationPeer() => new ComboBoxTextPeer(this);
    }

    private sealed class ComboBoxTextPeer(ComboBoxText owner) : TextBoxAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ComboBox;
        protected override bool IsPasswordCore() => owner.Protected;
    }
}
