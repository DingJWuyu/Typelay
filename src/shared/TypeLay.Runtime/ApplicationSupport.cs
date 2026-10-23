namespace TypeLay.Runtime;

public enum ApplicationSupportState
{
    Unknown,
    BuiltInAvailable,
    NeedsInstall,
    AwaitingApproval,
    RestartRequired,
    Registered,
    Connected,
    Unsupported,
    Error
}

public enum ApplicationSupportReason
{
    Unknown,
    None,
    ComponentMissing,
    RegistrationMissing,
    RegistrationInvalid,
    RegistrationConflict,
    AwaitingApproval,
    RestartRequired,
    AwaitingConnection,
    NotImplemented,
    NoFocusedField,
    FieldUnobserved,
    PreviewOnly,
    NoDirectWrite,
    LimitedFields,
    UserApprovalRequired,
    RegistrationFailed,
    InvalidExtensionId,
    DetectionFailed,
    ActionFailed,
    Paused
}

public enum ApplicationSupportAction
{
    Refresh,
    OpenInstructions,
    Install,
    Connect,
    Repair,
    Remove,
    CheckComponent,
    Disconnect
}

// Installation and connection facts never grant a Session acceptance permit.
// Capabilities describe the adapter's most recent verified observation only.
public sealed record ApplicationSupportCapabilities(bool Preview = false, bool TabAccept = false, bool DirectReplace = false);

public sealed record ApplicationSupportFact(
    string Id,
    ApplicationSupportState State,
    ApplicationSupportReason Reason = ApplicationSupportReason.Unknown,
    ApplicationSupportCapabilities? Capabilities = null,
    ApplicationSupportAction[]? SupportedActions = null,
    string? Details = null,
    DateTimeOffset? CheckedAt = null,
    bool? ApplicationDetected = null);

public sealed record ApplicationSupportActionView(
    ApplicationSupportAction Id, string Label, bool RequiresConfirmation, string? ConfirmationText = null);

public sealed record ApplicationSupportRow(
    string Id,
    string Title,
    string Description,
    ApplicationSupportState State,
    string StatusText,
    ApplicationSupportReason Reason,
    string ReasonText,
    ApplicationSupportCapabilities Capabilities,
    string CapabilityText,
    ApplicationSupportActionView[] Actions,
    string? Details,
    DateTimeOffset? CheckedAt,
    string Guidance,
    string CapabilitySummary,
    ApplicationSupportActionView? PrimaryAction,
    ApplicationSupportActionView[] SecondaryActions,
    bool ShowInMore,
    string SummaryText);

public sealed record ApplicationSupportLabels(
    string Confirm, string Cancel, string ChooseComponent, string OpenFolder, string CheckAgain, string Details,
    string PrepareTranslation, string ConnectApplications, string TryIt, string Recovery, string MoreApplications,
    string OpenBrowser, string Copy);

public sealed record ApplicationSupportView(
    string Title, string Summary, string CapabilityNote, ApplicationSupportRow[] Rows, ApplicationSupportLabels Labels);

// These preparation facts are independent of application/field capabilities.
public enum TranslationPreparationPhase
{
    Idle, Preparing, Pausing, Paused, StartingModel, Failed
}

public enum TranslationPreparationAction
{
    Prepare, Pause, Resume, Retry, NextStep, Repair, CompleteDirections
}

public enum TranslationPreparationNextStep
{
    TryIt, ChooseConnection
}

public sealed record TranslationPreparationFact(
    bool ModelReady,
    bool ModelInstalled,
    TranslationPreparationPhase Phase = TranslationPreparationPhase.Idle,
    SetupFailureCategory? FailureCategory = null,
    SetupFailureCategory? RecoveryFailureCategory = null,
    IReadOnlyList<string>? InstalledDirections = null,
    IReadOnlyList<string>? ReadyDirections = null,
    bool InstallationIntentKnown = true,
    ModelPackageManagementFact? Packages = null);

public sealed record TranslationPreparationActionView(
    TranslationPreparationAction Id, string Label, bool Enabled = true);

public sealed record TranslationPreparationView(
    string StatusText, string Guidance, TranslationPreparationActionView PrimaryAction,
    TranslationPreparationActionView[] SecondaryActions)
{
    public TranslationPreparationDirectionView[] Directions { get; init; } = [];
    public ModelPackageRow[] PackageRows { get; init; } = [];
    public string CurrentModelText { get; init; } = "";
    public bool CanSelectDirections
    {
        get; init;
    }
}

public sealed record TranslationPreparationDirectionView(string Id, string Label, bool Installed);

// These are exact inventory/selection/hello observations, never inferred from files or catalog coverage.
public sealed record ModelPackageManagementFact(
    string? Generation,
    bool InventoryKnown,
    IReadOnlyList<ModelPackageSelection> InstalledPackages,
    IReadOnlyList<ModelPackageSelection> CurrentPackages,
    IReadOnlyList<ModelPackageSelection> ReadyPackages,
    bool HyRuntimeAvailable,
    string? HyUnavailableReason,
    string Source,
    string Target,
    bool OperationInProgress,
    string RuntimeDownloadSources = "Astral、PyPI");

public enum ModelPackageOperation
{
    Install, Repair, Select, Remove
}

// The UI returns this frozen request unchanged. The installer revalidates every field under its lease.
public sealed record ModelPackageRequest(int Format, ModelPackageOperation Operation,
    string[] PackageIds, string[] ActivePackages, string? ExpectedCurrent)
{
    // Format2 is only the explicit, offline supplement to an installed Hy package.
    // Omitting this field preserves the exact format1 wire shape for all existing UI.
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string[]? AddDirections
    {
        get; init;
    }
}

public sealed record ModelPackageActionView(string Label, bool Enabled, bool RequiresConfirmation,
    string ConfirmationText, ModelPackageRequest Request);

public sealed record ModelPackageRow(string Id, string Version, string Title, bool Preview,
    string[] DirectionIds, string CoverageText, bool Installed, bool Current, bool Ready,
    string[] InstalledDirectionIds, string[] CurrentDirectionIds, string[] ReadyDirectionIds,
    long? DownloadBytes, string DownloadText, string LicenseUrl, string Details,
    string AvailabilityText, ModelPackageActionView[] Actions);

public enum InputConnectionState
{
    Unavailable, Connecting, Connected, ListenerUnavailable, Reconnecting
}
