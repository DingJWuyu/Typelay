using System.Text.Json;
using TypeLay.Windows;
namespace TypeLay.App;

internal sealed partial class BrowserInput
{
    private readonly string? _tracePath = Environment.GetEnvironmentVariable("TYPELAY_TRACE");
    private int _traceCount;
    private void Trace(string operation, object? detail = null)
    {
        if (string.IsNullOrWhiteSpace(_tracePath) || _traceCount++ >= 256)
            return;
        try
        {
            File.AppendAllText(_tracePath, JsonSerializer.Serialize(new
            {
                atMs = WindowsInterruptClock.NowMs,
                operation,
                detail
            }, BrowserServer.Json) + Environment.NewLine);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
