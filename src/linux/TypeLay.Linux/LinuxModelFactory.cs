using TypeLay.Runtime;

namespace TypeLay.Linux;

internal static class LinuxModelFactory
{
    public static ModelClient Create(string home, string data) => new(
        new(Path.Combine(data, "components", "current", "runtime", "venv", "bin", "python"),
            Path.Combine(home, "worker", "run_worker.py"), Path.Combine(data, "components", "current", "models")),
        start => new LinuxChildProcess(start, Path.Combine(home, "native", "typelay-supervise")));
}
