using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using TypeLay.Windows.Testing;
using N = TypeLay.Platform.Windows.NativeMethods;

internal static partial class HyPackageCheck
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    internal sealed class Scene(string name)
    {
        public string Name { get; } = name;
        public string Outcome { get; set; } = "not-run";
        public string Stage { get; set; } = "not-started";
        public string Failure { get; set; } = "none";
        public string ErrorType { get; set; } = "none";
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? NativeOperation
        {
            get; set;
        }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public uint? NativeMessageId
        {
            get; set;
        }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? NativeWin32Error
        {
            get; set;
        }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? TextLengthChangeCount
        {
            get; set;
        }
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public TextReadDiagnostic? LastTextReadFailure
        {
            get; set;
        }
        internal void RecordTextReadFailure(N.TextReadException error) =>
            LastTextReadFailure = new(error.Reason.ToString(), error.Limit, error.InitialLength,
                error.ReturnedCount, error.ManagedLength, error.FinalLength);
        public long ElapsedMs
        {
            get; set;
        }
        public object? Facts
        {
            get; set;
        }
    }
    internal sealed record TextReadDiagnostic(string Reason, int Limit, ulong InitialLength,
        ulong? ReturnedCount, int? ManagedLength, ulong? FinalLength);
    internal sealed class Report(string suite, int budget, params string[] names)
    {
        public string Suite { get; } = suite;
        public int BudgetSeconds { get; } = budget;
        public int CleanupReserveSeconds => 20;
        public int OperationBudgetSeconds => BudgetSeconds - CleanupReserveSeconds;
        public bool OperationDeadlineReached
        {
            get; set;
        }
        internal CancellationToken OperationToken
        {
            get; set;
        }
        public string Outcome { get; set; } = "not-run";
        public string? ReleaseIdentity
        {
            get; set;
        }
        public bool LoadedReleaseAssembliesMatched
        {
            get; set;
        }
        public List<Scene> Scenes { get; } = names.Select(name => new Scene(name)).ToList();
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? EnglishAcceptance
        {
            get; set;
        }
        public List<string> CleanupErrors { get; } = [];
        public string Quality => "not-reviewed";
        public async Task Phase(string name, Func<Scene, Task> action)
        {
            OperationToken.ThrowIfCancellationRequested();
            var scene = Scenes.Single(item => item.Name == name);
            scene.Stage = name;
            var watch = Stopwatch.StartNew();
            try
            {
                await action(scene);
                OperationToken.ThrowIfCancellationRequested();
                scene.Outcome = "passed";
                scene.Stage = "complete";
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                scene.Outcome = "failed";
                scene.ErrorType = error.GetType().Name;
                scene.Failure = error is Failure failure ? failure.Code : error is NativeSourceSetup.Failure sourceSetup ? sourceSetup.Code
                    : error is SettingsPackageActions.ActionDeadlineException actionDeadline ? actionDeadline.FailureCode
                    : error is OperationCanceledException ? "deadline" : "operation-failed";
                if (error is N.NativeMessageException native)
                {
                    scene.NativeOperation = native.Operation;
                    scene.NativeMessageId = native.MessageId;
                    scene.NativeWin32Error = native.Win32Error; // Zero does not establish a timeout.
                }
                if (error is N.TextReadException text)
                    scene.RecordTextReadFailure(text);
                throw;
            }
            finally { scene.ElapsedMs = watch.ElapsedMilliseconds; }
        }
        public async Task<int> Save(string directory)
        {
            Outcome = EnglishAcceptance == "catalog-rejected" ? "failed"
                : Scenes[0].Name.EndsWith("prerequisite", StringComparison.Ordinal) && Scenes[0].Outcome != "passed" ? "not-run"
                : !OperationDeadlineReached && Scenes.All(scene => scene.Outcome == "passed") && CleanupErrors.Count == 0 ? "passed" : "failed";
            await File.WriteAllTextAsync(Path.Combine(directory, "result.json"), JsonSerializer.Serialize(this, Json));
            Console.WriteLine($"{Suite}: {Outcome}; {string.Join(", ", Scenes.Select(scene => scene.Name + "=" + scene.Outcome))}");
            return Outcome == "passed" ? 0 : 1;
        }
    }

}
