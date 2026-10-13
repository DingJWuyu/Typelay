using System.Diagnostics;

namespace TypeLay.Runtime;

public sealed record ModelRuntimePaths(string Python, string Worker, string Models);

// The factory must either return a supervised process or fail without leaving a child behind.
// Disposal terminates the entire owned process family and drains redacted stderr.
public interface IChildProcess : IDisposable
{
    Process Process
    {
        get;
    }
}
