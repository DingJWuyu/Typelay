using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;

namespace TypeLay.BrowserIdentityFixture;

internal static class NativeHost
{
    internal static async Task<int> RunAsync(string[] args)
    {
        // Native messaging stdout contains framed metadata only, including on failure.
        try
        {
            string origin = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "allowed-origin.txt"));
            if (args.Length != 2 || args[0] != origin || !args[1].StartsWith("--parent-window=", StringComparison.Ordinal) ||
                !long.TryParse(args[1][16..], out long parent) || parent < 0)
                return 2;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            using Stream input = Console.OpenStandardInput();
            using Stream output = Console.OpenStandardOutput();
            var stopwatch = Stopwatch.StartNew();
            byte[] lengthBytes = new byte[4];
            await input.ReadExactlyAsync(lengthBytes, deadline.Token);
            int length = BinaryPrimitives.ReadInt32LittleEndian(lengthBytes);
            if (length is < 1 or > 256)
                return 2;
            byte[] bytes = new byte[length];
            await input.ReadExactlyAsync(bytes, deadline.Token);
            using JsonDocument request = JsonDocument.Parse(bytes);
            var properties = request.RootElement.EnumerateObject().ToArray();
            if (properties.Length != 1 || properties[0].Name != "challenge" || properties[0].Value.ValueKind != JsonValueKind.String)
                return 2;
            string challenge = properties[0].Value.GetString()!;
            if (challenge.Length != 64 || challenge.Any(c => !(c is >= '0' and <= '9' or >= 'a' and <= 'f')))
                return 2;
            bool valid = Windows.IsWindow((nint)parent);
            nint root = valid ? Windows.GetAncestor((nint)parent, 2) : 0;
            Windows.GetWindowThreadProcessId(root, out uint ownerPid);
            byte[] response = JsonSerializer.SerializeToUtf8Bytes(new
            {
                challenge,
                parentWindow = parent.ToString(System.Globalization.CultureInfo.InvariantCulture),
                isWindow = valid,
                rootWindow = ((long)root).ToString(System.Globalization.CultureInfo.InvariantCulture),
                ownerPid,
                hostPid = Environment.ProcessId,
                connectionId = Guid.NewGuid().ToString("N"),
                lifetimeMs = stopwatch.ElapsedMilliseconds
            });
            BinaryPrimitives.WriteInt32LittleEndian(lengthBytes, response.Length);
            await output.WriteAsync(lengthBytes, deadline.Token);
            await output.WriteAsync(response, deadline.Token);
            await output.FlushAsync(deadline.Token);
            // Keep the binding's native port alive until disconnect. No second request is accepted.
            return await input.ReadAsync(new byte[1], deadline.Token) == 0 ? 0 : 2;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            return 2;
        }
    }
}
