using System.Text;

namespace Prt.Core.Export;

/// <summary>
/// Undoes UTF-8 text that was decoded as CP850 (the OEM code page of a Spanish Windows console):
/// "Revis├®" → "Revisé", "┬┐" → "¿", "ÔÇª" → "…". Only sequences that form valid UTF-8 are changed,
/// so correct text, mixed text and real box-drawing characters stay as they are.
/// </summary>
public static class TextRepair
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // Byte that each character of the upper half of CP850 came from.
    private static readonly Dictionary<char, byte> Cp850Bytes = BuildCp850Bytes();

    public static string Fix(string text)
    {
        if (!text.Any(Cp850Bytes.ContainsKey))
            return text;

        var sb = new StringBuilder(text.Length);
        var i = 0;
        while (i < text.Length)
        {
            if (TryDecode(text, i, out var decoded))
            {
                sb.Append(decoded.Text);
                i += decoded.Length;
            }
            else
            {
                sb.Append(text[i]);
                i++;
            }
        }
        return sb.ToString();
    }

    private static bool TryDecode(string text, int start, out (string Text, int Length) decoded)
    {
        decoded = default;
        if (!Cp850Bytes.TryGetValue(text[start], out var lead))
            return false;

        var length = lead switch
        {
            >= 0xC2 and <= 0xDF => 2,
            >= 0xE0 and <= 0xEF => 3,
            >= 0xF0 and <= 0xF4 => 4,
            _ => 0,
        };
        if (length == 0 || start + length > text.Length)
            return false;

        var bytes = new byte[length];
        bytes[0] = lead;
        for (var k = 1; k < length; k++)
        {
            if (!Cp850Bytes.TryGetValue(text[start + k], out var b) || b is < 0x80 or > 0xBF)
                return false;
            bytes[k] = b;
        }

        string result;
        try
        {
            result = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
        // C1 control characters are never what the text meant.
        if (char.ConvertToUtf32(result, 0) < 0xA0)
            return false;

        decoded = (result, length);
        return true;
    }

    private static Dictionary<char, byte> BuildCp850Bytes()
    {
        var cp850 = CodePagesEncodingProvider.Instance.GetEncoding(850)
            ?? throw new InvalidOperationException("La codificación CP850 no está disponible.");
        var map = new Dictionary<char, byte>();
        for (var b = 0x80; b <= 0xFF; b++)
            map[cp850.GetString([(byte)b])[0]] = (byte)b;
        return map;
    }
}
