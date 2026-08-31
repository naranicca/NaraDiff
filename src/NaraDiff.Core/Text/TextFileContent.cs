using System.Text;

namespace NaraDiff.Core.Text;

/// <summary>How the loader decided between text and binary handling.</summary>
public enum ContentKind
{
    Text,
    Binary
}

/// <summary>
/// A decoded file: its lines, the encoding used, the terminator style, and the raw bytes
/// (kept for binary comparison and for byte level equality checks).
/// </summary>
public sealed class TextFileContent
{
    private TextFileContent(byte[] bytes, ContentKind kind, EncodingChoice encoding, string text, List<TextLine> lines)
    {
        Bytes = bytes;
        Kind = kind;
        Encoding = encoding;
        Text = text;
        Lines = lines;
        LineEnding = LineEndings.Detect(lines);
    }

    public byte[] Bytes { get; }

    public ContentKind Kind { get; }

    public EncodingChoice Encoding { get; }

    public string Text { get; }

    public IReadOnlyList<TextLine> Lines { get; }

    public LineEndingKind LineEnding { get; }

    public bool IsBinary => Kind == ContentKind.Binary;

    public long ByteLength => Bytes.LongLength;

    public static TextFileContent Empty { get; }= new([], ContentKind.Text, EncodingCatalog.Utf8, string.Empty, [new TextLine(string.Empty, LineEndingKind.None)]);

    /// <summary>Decodes a buffer. A forced encoding skips detection; forcing text skips binary sniffing.</summary>
    public static TextFileContent FromBytes(byte[] bytes, EncodingChoice? forcedEncoding = null, bool forceText = false)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var encoding = forcedEncoding ?? EncodingCatalog.Detect(bytes);
        var kind = !forceText && LooksBinary(bytes, encoding) ? ContentKind.Binary : ContentKind.Text;
        if (kind == ContentKind.Binary)
            return new TextFileContent(bytes, kind, encoding, string.Empty, [new TextLine(string.Empty, LineEndingKind.None)]);
        var text = Decode(bytes, encoding);
        return new TextFileContent(bytes, kind, encoding, text, LineEndings.Split(text));
    }

    public static TextFileContent FromText(string text, EncodingChoice? encoding = null)
    {
        var choice = encoding ?? EncodingCatalog.Utf8;
        return new TextFileContent(choice.GetBytes(text), ContentKind.Text, choice, text, LineEndings.Split(text));
    }

    /// <summary>Re-decodes the same bytes with a different encoding, keeping the binary decision.</summary>
    public TextFileContent WithEncoding(EncodingChoice encoding) => FromBytes(Bytes, encoding, Kind == ContentKind.Text);

    private static string Decode(byte[] bytes, EncodingChoice encoding)
    {
        var offset = 0;
        var preamble = encoding.Encoding.GetPreamble();
        if (preamble.Length > 0 && bytes.Length >= preamble.Length && bytes.AsSpan(0, preamble.Length).SequenceEqual(preamble)) offset = preamble.Length;
        else if (EncodingCatalog.GetByteOrderMarkLength(bytes, out _) is var markLength && markLength > 0 && markLength <= bytes.Length) offset = markLength;
        try
        {
            return encoding.Encoding.GetString(bytes, offset, bytes.Length - offset);
        }
        catch (Exception ex) when (ex is ArgumentException or DecoderFallbackException)
        {
            return System. Text.Encoding.Latin1.GetString(bytes, offset, bytes.Length - offset);
        }
    }

    /// <summary>
    /// Treats a buffer as binary when it contains a NUL byte outside a UTF-16 stream, or when too
    /// many bytes are non-printable control characters.
    /// </summary>
    private static bool LooksBinary(ReadOnlySpan<byte> bytes, EncodingChoice encoding)
    {
        if (bytes.Length == 0) return false;
        if (encoding.Id is EncodingCatalog.Utf16LeId or EncodingCatalog.Utf16BeId or EncodingCatalog.Utf32LeId) return false;
        var sample = bytes.Length > 8192 ? bytes[ .. 8192] : bytes;
        var control = 0;
        foreach (var b in sample)
        {
            if (b == 0) return true;
            if (b < 0x09 || (b > 0x0D && b < 0x20)) control++;
        }
        return control * 100 > sample.Length * 20;
    }
}