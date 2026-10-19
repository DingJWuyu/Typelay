using TypeLay.Runtime;

namespace TypeLay.Linux;

internal static class LinuxModelFactory
{
    public static ModelClient Create(string home, string data) => new(
        new LinuxModelInstallationStore(home, data).InitialPaths(),
        start =>
        {
            string models = start.ArgumentList.Count >= 2 && start.ArgumentList[^2] == "--models"
                ? start.ArgumentList[^1] : throw new IOException("model-generation");
            string? root = Path.GetDirectoryName(models), generation = Path.GetFileName(root);
            if (!LinuxModelInstallationStore.ValidId(generation)
                || models != Path.Combine(data, "components", generation!, "models"))
                throw new IOException("model-generation");
            // The actual supervisor, not this .NET owner, retains the use lock
            // until it has reaped the complete model process family.
            start.Environment["TYPELAY_MODEL_USE_LOCK"] = Path.Combine(root!, "use.lock");
            return new LinuxChildProcess(start, Path.Combine(home, "native", "typelay-supervise"), requireExitProof: true);
        });
}
