using System.Runtime.InteropServices;

namespace NaraDiff.Core.Text;

/// <summary>
/// Windows code page encoding backed by MultiByteToWideChar / WideCharToMultiByte.
/// </summary>
/// <remarks>
/// The shared .NET framework ships only Unicode and Latin-1 encodings, and the
/// System.Text.Encoding.CodePages package is not part of this build, so ANSI and DBCS support
/// (949, 1252, 932, ...) is provided by the operating system instead.
/// </remarks>
public sealed class CodePageEncoding : System.Text.Encoding
{
    private readonly int _codePage;
    private readonly string _name;

    public CodePageEncoding(int codePage, string? name = null)
    {
        _codePage = codePage;
        _name = name ?? $"cp{codePage}";
    }

    /// <summary>The ANSI code page configured for the current system (CP_ACP).</summary>
    public static CodePageEncoding Ansi { get; } = new(0, "ANSI");

    public static bool IsSupported(int codePage) => codePage == 0 || IsValidCodePage((uint)codePage);

    public override int CodePage => _codePage == 0 ? GetACP() : _codePage;

    public override string EncodingName => _name;

    public override string WebName => _name;

    public override int GetByteCount(char[] chars, int index, int count)
    {
        ArgumentNullException.ThrowIfNull(chars);
        if (count == 0) return 0;
        unsafe
        {
            fixed (char* source = &chars[index])
                return Checked(WideCharToMultiByte((uint)_codePage, 0, source, count, null, 0, IntPtr.Zero, IntPtr.Zero));
        }
    }

    public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
    {
        ArgumentNullException.ThrowIfNull(chars);
        ArgumentNullException.ThrowIfNull(bytes);
        if (charCount == 0) return 0;
        unsafe
        {
            fixed (char* source = &chars[charIndex])
            fixed (byte* destination = &bytes[byteIndex])
                return Checked(WideCharToMultiByte((uint)_codePage, 0, source, charCount, destination, bytes.Length - byteIndex, IntPtr.Zero, IntPtr.Zero));
        }
    }

    public override int GetCharCount(byte[] bytes, int index, int count)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (count == 0) return 0;
        unsafe
        {
            fixed (byte* source = &bytes[index])
                return Checked(MultiByteToWideChar((uint)_codePage, 0, source, count, null, 0));
        }
    }
    public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(chars);
        if (byteCount == 0) return 0;
        unsafe
        {
            fixed (byte* source = &bytes[byteIndex])
            fixed (char* destination = &chars[charIndex])
                return Checked(MultiByteToWideChar((uint)_codePage, 0, source, byteCount, destination, chars.Length - charIndex));
        }
    }

    public override int GetMaxByteCount(int charCount) => (charCount + 1) * 4;

    public override int GetMaxCharCount(int byteCount) => byteCount + 1;

    private static int Checked(int result) => result >= 0 ? result : throw new ArgumentException("The text could not be converted with the selected code page.");

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern int GetACP();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsValidCodePage(uint codePage);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern unsafe int MultiByteToWideChar(uint codePage, uint flags, byte* source, int sourceBytes, char* destination, int destinationChars);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern unsafe int WideCharToMultiByte(uint codePage, uint flags, char* source, int sourceChars, byte* destination, int destinationBytes, IntPtr defaultChar, IntPtr usedDefaultChar);
}
