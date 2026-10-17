using TypeLay.Runtime;

namespace TypeLay.Linux;

internal static class LinuxModelFactory
{
    public static ModelClient Create(string home, string data) => new(
        new LinuxModelInstallationStore(home, data).InitialPaths(),
        start => new LinuxChildProcess(start, Path.Combine(home, "native", "typelay-supervise")));
}
