using System.IO;
using System.IO.Pipes;
using TypeLay.Windows;
using Xunit;

namespace TypeLay.App.Tests;

public sealed class BrowserPeerIdentityTests
{
    [Fact]
    public async Task PipeAuthenticationUsesActualPeerAndRejectsAnotherExecutable()
    {
        string name = "typelay-peer-test-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connected = server.WaitForConnectionAsync(timeout.Token);
        await client.ConnectAsync(timeout.Token);
        await connected;
        string executable = Environment.ProcessPath!;
        using var actualServer = BrowserPeerIdentity.AuthenticateServer(client, executable);
        using var actualClient = BrowserPeerIdentity.AuthenticateClient(server, executable);
        Assert.Equal(Environment.ProcessId, actualServer.ProcessId);
        Assert.Equal(Environment.ProcessId, actualClient.ProcessId);
        Assert.True(actualServer.Alive);
        Assert.True(actualClient.Alive);
        Assert.Throws<UnauthorizedAccessException>(() => BrowserPeerIdentity.AuthenticateServer(client,
            Path.Combine(Path.GetDirectoryName(executable)!, "not-the-peer.exe")));
        Assert.Throws<UnauthorizedAccessException>(() => BrowserPeerIdentity.AuthenticateClient(server,
            Path.Combine(Path.GetDirectoryName(executable)!, "not-the-peer.exe")));
    }
}
