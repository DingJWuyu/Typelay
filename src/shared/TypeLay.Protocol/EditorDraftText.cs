using System.Text.Json;

namespace TypeLay.Protocol;

// Drafts can contain a temporarily unpaired UTF-16 unit while an editor receives
// a supplementary character. Decoding text does not grant write eligibility.
public static class EditorDraftText
{
    public static bool TryRead(JsonElement value, out string text)
    {
        text = "";
        if (value.ValueKind == JsonValueKind.String)
        {
            try
            {
                text = value.GetString()!;
                return true;
            }
            catch (InvalidOperationException) { return false; }
        }
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 1
            || !value.TryGetProperty("utf16", out var units) || units.ValueKind != JsonValueKind.Array
            // Even single-digit units need two JSON bytes each. Keep the wire
            // bound, not the smaller write-size limit: invalid drafts revoke too.
            || units.GetArrayLength() >= Wire.MaxFrameBytes / 2)
            return false;
        char[] decoded = new char[units.GetArrayLength()];
        int index = 0;
        foreach (var unit in units.EnumerateArray())
        {
            if (unit.ValueKind != JsonValueKind.Number || !unit.TryGetUInt16(out ushort code))
                return false;
            decoded[index++] = (char)code;
        }
        text = new string(decoded);
        return true;
    }
}
