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
    CheckComponent
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
    bool ShowInMore);

public sealed record ApplicationSupportLabels(
    string Confirm, string Cancel, string ChooseComponent, string OpenFolder, string CheckAgain, string Details,
    string PrepareTranslation, string ConnectApplications, string TryIt, string Recovery, string MoreApplications,
    string OpenBrowser);

public sealed record ApplicationSupportView(
    string Title, string Summary, string CapabilityNote, ApplicationSupportRow[] Rows, ApplicationSupportLabels Labels);
