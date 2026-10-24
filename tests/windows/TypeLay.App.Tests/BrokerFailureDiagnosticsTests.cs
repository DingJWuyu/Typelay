using System.IO;
using System.Text.Json;
using TypeLay.Windows.Contracts;
using Xunit;

namespace TypeLay.App.Tests;

[Collection("Desktop")]
public sealed class BrokerFailureDiagnosticsTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OfflineFailureKeepsItsExceptionAndCleanupEvenWhenTracingCannotWrite(bool tracing, bool unwritable)
    {
        string path = Path.Combine(Path.GetTempPath(), "typelay-broker-diagnostics-" + Guid.NewGuid().ToString("N"));
        string? previous = Environment.GetEnvironmentVariable("TYPELAY_TRACE");
        Environment.SetEnvironmentVariable("TYPELAY_TRACE", tracing ? path : null);
        if (unwritable)
            Directory.CreateDirectory(path);
        try
        {
            using var broker = new BrokerClient();
            for (int i = 0; i < 20; i++)
            {
                var failure = await Assert.ThrowsAsync<IOException>(() => broker.CallAsync(
                    new(BrokerProtocol.Version, 0, "private-operation", "private-token")));
                Assert.Equal("broker-offline", failure.Message);
                Assert.False(broker.Available);
            }
            if (!tracing || unwritable)
            {
                Assert.False(File.Exists(path));
                return;
            }
            string trace = File.ReadAllText(path);
            Assert.DoesNotContain("private-", trace);
            string[] lines = File.ReadAllLines(path);
            Assert.Equal(16, lines.Length);
            foreach (string line in lines)
            {
                using var document = JsonDocument.Parse(line);
                var detail = document.RootElement.GetProperty("detail");
                Assert.Equal("acquire-child", detail.GetProperty("phase").GetString());
                Assert.Equal("io", detail.GetProperty("errorCategory").GetString());
                Assert.Equal(JsonValueKind.Null, detail.GetProperty("cancellationRequested").ValueKind);
                Assert.Equal(JsonValueKind.Null, detail.GetProperty("heldProcessExited").ValueKind);
                Assert.Equal(5, detail.EnumerateObject().Count());
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("TYPELAY_TRACE", previous);
            if (unwritable)
                Directory.Delete(path);
            else
                File.Delete(path);
        }
    }
}
