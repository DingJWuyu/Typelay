using System.Text.RegularExpressions;
namespace TypeLay.Protocol;

public sealed record FieldMetadata(string Process, string Label, string AutomationId, string Help,
    string Aria, bool Password, bool Enabled, bool ReadOnly, bool Offscreen, bool InDocument,
    bool Multiline, bool HasValue, bool HasTextEdit);

/// <summary>Metadata-only gate. Call this BEFORE ValuePattern.Value / TextRange.GetText.</summary>
public static partial class FieldPolicy
{
    // Discover editable controls by capability; ordinary fields need no positive label.
    [GeneratedRegex(@"password|passwd|passcode|\bpin\b|\botp\b|\bcvv\b|\bcvc\b|security.?code|card.?number|credit.?card|debit.?card|one.?time|2fa|secret|token|api.?key|verification|authenticat|ssn|social.?security|bank|iban|account.?number|密码|密碼|口令|验证码|驗證碼|校验|银行卡|銀行卡|信用卡|安全码|安全碼|支付|身份证|身分證|密钥|金鑰|助记词|私钥", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Sensitive();
    [GeneratedRegex(@"terminal|shell|monaco|codemirror|ace[_ -]?editor|command|console|address.?bar|omnibox|code.?editor|终端|命令|代码|地址栏", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Machine();
    public static bool Allows(FieldMetadata field, IEnumerable<string> excluded)
    {
        if (excluded.Contains(field.Process, StringComparer.OrdinalIgnoreCase)) return false;
        if (field.Password || !field.Enabled || field.ReadOnly || field.Offscreen) return false;
        if (field.Process is "typelay.app" or "typelay.broker") return false;
        string metadata = string.Join(" ", field.Label, field.AutomationId, field.Help, field.Aria);
        return metadata.Length <= 8192 && !Sensitive().IsMatch(metadata) && !Machine().IsMatch(metadata)
            && !Machine().IsMatch(field.Process);
    }
}
