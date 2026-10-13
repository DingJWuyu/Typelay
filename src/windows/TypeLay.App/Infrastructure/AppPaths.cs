using System.Diagnostics;
namespace TypeLay.App;

internal static class AppPaths
{
    public static string Home => Environment.GetEnvironmentVariable("TYPELAY_HOME") ?? AppContext.BaseDirectory;
    public static string Data { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TypeLay");
    public static string Runtime => Path.Combine(Data, "runtime");
    public static string Python => Path.Combine(Runtime, "python", "tools", "python.exe");
    public static string Models => Path.Combine(Data, "models");
    public static string DotNet => Environment.GetEnvironmentVariable("TYPELAY_DOTNET") ?? "dotnet";
    public static string Worker => Path.Combine(Home, "worker", "run_worker.py");
    public static ProcessStartInfo Child(string assembly)
    {
        string appHost = Path.Combine(AppContext.BaseDirectory, assembly + ".exe");
        bool nativeHost = File.Exists(appHost);
        var info = new ProcessStartInfo(nativeHost ? appHost : DotNet)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (!nativeHost)
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, assembly + ".dll"));
        return info;
    }
}
