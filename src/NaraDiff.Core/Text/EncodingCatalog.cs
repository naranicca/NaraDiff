using System. Text;

namespace NaraDiff.Core.Text;

/// <summary>A user selectable encoding, including whether a byte order mark is written on save.</summary>
public sealed class EncodingChoice
{
    public EncodingChoice(string id, string displayName, Encoding encoding, bool writeByteOrderMark)
    {
        Id = id;
        DisplayName = displayName;
        Encoding = encoding;
        WriteByteOrderMark = writeByteOrderMark;
    }

    public string Id { get; }

    public string DisplayName { get; }

    public Encoding Encoding { get; }

    public bool WriteByteOrderMark { get; }

    public override string ToString() => DisplayName;

    public byte[] GetPreamble() => WriteByteOrderMark ? Encoding.GetPreamble() : [];

    public byte[] GetBytes(string text)
    {
        var body = Encoding.GetBytes(text);
        var preamble = GetPreamble();
        if (preamble.Length == 0) return body;
        var result = new byte[preamble.Length + body.Length];
        preamble.CopyTo(result, 0);
        body.CopyTo(result, preamble.Length);
        return result;
    }
}

/// <summary>The encodings offered in the UI plus byte order mark and heuristic detection.</summary>
public static class EncodingCatalog
{
    public const string Utf8Id = "utf-8";
    public const string Utf8BomId = "utf-8-bom";
    public const string Utf16LeId = "utf-16le";
    public const string Utf16BeId = "utf-16be";
    public const string Utf32LeId = "utf-32le";
    public const string Ansild = "ansi";

    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private static readonly List<EncodingChoice> Items = Build();

    public static IReadOnlyList<EncodingChoice> All => Items;

    public static EncodingChoice Utf8 => Get(Utf8Id);

    public static EncodingChoice Ansi => Get(AnsiId);

    public static EncodingChoice Get(string id) =>
        Items.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)) ?? Utf8;

    private static List<EncodingChoice> Build()
    {
        var items = new List<EncodingChoice>
        {
            new(Utf8Id, "UTF-8", new UTF8Encoding(false), false),
            new(Utf8BomId, "UTF-8 with BOM", new UTF8Encoding(true), true),
            new(Utf16LeId, "UTF-16 LE", new UnicodeEncoding(false, true), true),
            new(Utf16BeId, "UTF-16 BE", new UnicodeEncoding(true, true), true),
            new(Utf32LeId, "UTF-32 LE", new UTF32Encoding(false, true), true)
        };
        items.Add(new EncodingChoice(AnsiId, $"ANSI ({SafeCodePage(CodePageEncoding.Ansi)})", CodePageEncoding.Ansi, false));
        foreach (var (codePage, label) in new[] {(949,"Korean (949)"),(932,"Japanese (932)"),(936,"Simplified Chinese (936)"), (1252, "Western European (1252)") })
        if (CodePageEncoding.IsSupported(codePage)) items.Add(new EncodingChoice($"cp{codePage}", label, new CodePageEncoding(codePage, label), false));
        items.Add(new EncodingChoice("latin1", "Latin-1 (ISO-8859-1)", Encoding.Latin1, false));
        return items;
    }

    private static string SafeCodePage(CodePageEncoding encoding)
    {
        try { return encoding.CodePage.ToString(); }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException) { return "system"; }
    }

    /// <summary>Detects the byte order mark of a buffer and returns its length, or zero.</summary>
    public static int GetByteOrderMarkLength(ReadOnlySpan<byte> bytes, out EncodingChoice? choice)
    {
        if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0x00 && bytes[3] == 0x00) { choice = Get(Utf32LeId); return 4; }
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) { choice = Get(Utf8BomId); return 3; }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) { choice = Get(Utf16LeId); return 2; }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) { choice = Get(Utf16BeId); return 2; }
        choice = null;
        return 0;
    }

    /// <summary>
    /// Detects the encoding of a buffer: byte order mark first, then UTF-16 without mark,
    /// then strict UTF-8 validation, and finally the system ANSI code page.
    /// </summary>
    public static EncodingChoice Detect(ReadOnlySpan<byte> bytes)
    {
        if (GetByteOrderMarkLength(bytes, out var marked) > 0 && marked is not null) return marked;
        if (bytes.Length == 0) return Utf8;
        var utf16 = DetectUtf16WithoutMark(bytes);
        if (utf16 is not null) return utf16;
        return IsValidUtf8(bytes) ? Utf8 : Ansi;
    }

    private static EncodingChoice? DetectUtf16WithoutMark(ReadOnlySpan<byte> bytes)
    {
        var sample = bytes.Length > 4096 ? bytes[ .. 4096] : bytes;
        // Short buffers cannot be told apart reliably, and a binary file with a few NUL bytes must
        // not be mistaken for UTF-16.
        if (sample.Length < 16 || sample.Length % 2 != 0) return null;
        int evenZeros = 0, oddZeros = 0;
        for (var i = 0; i + 1 < sample.Length; i += 2)
        {
            if (sample[i] == 0) evenZeros++;
            if (sample[i +1] == 0) oddZeros++;
        }
        var pairs = sample.Length / 2;
        if (oddZeros > pairs * 0.3 && evenZeros < pairs * 0.05) return Get(Utf16LeId);
        if (evenZeros > pairs * 0.3 && oddZeros < pairs * 0.05) return Get(Utf16BeId);
        return null;
    }

    public static bool IsValidUtf8(ReadOnlySpan<byte> bytes)
    {
        try
        {
            _ = StrictUtf8.ReadOnlySpan<byte>(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}