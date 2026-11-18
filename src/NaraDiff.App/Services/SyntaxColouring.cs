using System.IO;
using System.Windows.Media;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Rendering;
using NaraDiff.Core.Settings;

namespace NaraDiff.App.Services;

/// <summary>
/// Colours code by language. AvalonEdit brings the language definitions; what it does not bring is
/// colours that work here, because its own are written for a white background and carry backgrounds
/// of their own that would cover the diff fills. Every colour a definition asks for is therefore
/// mapped onto this palette on its way to the screen, and no definition is modified: they are shared
/// singletons, and the editors would fight over them each time the theme changed.
/// </summary>
public sealed class SyntaxPalette
{
    private static readonly Dictionary<(ThemeKind Theme, bool Accessible), SyntaxPalette> Palettes = [];

    private readonly Dictionary<HighlightingColor, HighlightingColor> _mapped = [];
    private readonly Dictionary<SyntaxRole, Color> _roles;
    private readonly bool _dark;

    private SyntaxPalette(ThemeKind theme, bool accessiblePalette)
    {
        _dark = theme == ThemeKind.Dark;
        // A patch is a diff written down, so it is painted in the same colours the panes use for one.
        var diff = DiffPalette.Create(theme, accessiblePalette);
        _roles = _dark
            ? new Dictionary<SyntaxRole, Color>
            {
                [SyntaxRole.Comment] = Color.FromRgb(0x6A, 0x9D, 0x62),
                [SyntaxRole.String] = Color.FromRgb(0xCE, 0x91, 0x78),
                [SyntaxRole.Number] = Color.FromRgb(0xB5, 0xCE, 0xA8),
                [SyntaxRole.Keyword] = Color.FromRgb(0x62, 0xA6, 0xE8),
                [SyntaxRole.Type] = Color.FromRgb(0x4E, 0xC9, 0xB0),
                [SyntaxRole.Function] = Color.FromRgb(0xDC, 0xDC, 0xAA),
                [SyntaxRole.Preprocessor] = Color.FromRgb(0xC5, 0x86, 0xC0),
                [SyntaxRole.Tag] = Color.FromRgb(0x62, 0xA6, 0xE8),
                [SyntaxRole.Attribute] = Color.FromRgb(0x9C, 0xDC, 0xFE),
                [SyntaxRole.Punctuation] = Color.FromRgb(0xB8, 0xBE, 0xC6),
                [SyntaxRole.Added] = diff.InsertColor,
                [SyntaxRole.Removed] = diff.DeleteColor
            }
            : new Dictionary<SyntaxRole, Color>
            {
                [SyntaxRole.Comment] = Color.FromRgb(0x1A, 0x7F, 0x1A),
                [SyntaxRole.String] = Color.FromRgb(0xA3, 0x15, 0x15),
                [SyntaxRole.Number] = Color.FromRgb(0x09, 0x86, 0x58),
                [SyntaxRole.Keyword] = Color.FromRgb(0x00, 0x3C, 0xC8),
                [SyntaxRole.Type] = Color.FromRgb(0x1D, 0x6F, 0x86),
                [SyntaxRole.Function] = Color.FromRgb(0x79, 0x5E, 0x26),
                [SyntaxRole.Preprocessor] = Color.FromRgb(0x8E, 0x00, 0xB0),
                [SyntaxRole.Tag] = Color.FromRgb(0x80, 0x00, 0x00),
                [SyntaxRole.Attribute] = Color.FromRgb(0xC4, 0x00, 0x1D),
                [SyntaxRole.Punctuation] = Color.FromRgb(0x44, 0x49, 0x50),
                [SyntaxRole.Added] = diff.InsertColor,
                [SyntaxRole.Removed] = diff.DeleteColor
            };
    }

    public static SyntaxPalette For(ThemeKind theme) => For(theme, ThemeService.AccessiblePalette);

    public static SyntaxPalette For(ThemeKind theme, bool accessiblePalette)
    {
        var key = (theme, accessiblePalette);
        if (Palettes.TryGetValue(key, out var palette)) return palette;
        palette = new SyntaxPalette(theme, accessiblePalette);
        Palettes[key] = palette;
        return palette;
    }

    public static SyntaxPalette Current => For(ThemeService.Current, ThemeService.AccessiblePalette);

    /// <summary>This palette's reading of one colour of a definition. Answers are remembered.</summary>
    public HighlightingColor Map(HighlightingColor color)
    {
        ArgumentNullException.ThrowIfNull(color);
        if (_mapped.TryGetValue(color, out var mapped)) return mapped;
        mapped = Build(color);
        _mapped[color] = mapped;
        return mapped;
    }

    private HighlightingColor Build(HighlightingColor color)
    {
        var result = color.Clone();
        // The diff fills own the background of a line. A definition that paints one of its own, as
        // the XML one does behind CDATA, would hide which side a change is on.
        result.Background = null;
        if (RoleOf(color.Name) is { } role) result.Foreground = new SimpleHighlightingBrush(_roles[role]);
        else if (ForegroundOf(color) is { } existing) result.Foreground = new SimpleHighlightingBrush(Readable(existing));
        result.Freeze();
        return result;
    }

    /// <summary>
    /// A colour the definition names something this palette has no entry for is kept, but pulled far
    /// enough from the background to stay legible: a navy keyword written for white paper is all but
    /// invisible on a dark pane.
    /// </summary>
    private Color Readable(Color color)
    {
        var luminance = Luminance(color);
        if (_dark && luminance < 0.5) return Mix(color, Colors.White, Math.Min(0.85, (0.58 - luminance) / 0.58));
        if (!_dark && luminance > 0.62) return Mix(color, Colors.Black, Math.Min(0.85, (luminance - 0.42) / 0.58));
        return color;
    }

    private static Color Mix(Color color, Color towards, double amount) => Color.FromRgb(
        (byte)Math.Round(color.R + (towards.R - color.R) * amount),
        (byte)Math.Round(color.G + (towards.G - color.G) * amount),
        (byte)Math.Round(color.B + (towards.B - color.B) * amount));

    private static double Luminance(Color color) => (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;

    private static Color? ForegroundOf(HighlightingColor color)
    {
        try { return color.Foreground?.GetColor(null); }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// What a definition's colour is for, read from its name. The names are the definitions' own and
    /// differ between languages - C# alone names a dozen kinds of keyword - so they are matched by
    /// what they contain rather than listed one by one.
    /// </summary>
    private static SyntaxRole? RoleOf(string? name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        var lower = name.ToLowerInvariant();
        if (lower.Contains("comment")) return SyntaxRole.Comment;
        // A patch names the lines it adds and removes; they read as the change they are.
        if (lower.Contains("added") || lower.Contains("insert")) return SyntaxRole.Added;
        if (lower.Contains("removed") || lower.Contains("deleted")) return SyntaxRole.Removed;
        // Before the string test: a definition may well call a number a "NumberLiteral".
        if (lower.Contains("number") || lower.Contains("digit") || lower.Contains("numeric")) return SyntaxRole.Number;
        if (lower.Contains("string") || lower.Contains("char") || lower.Contains("regex")) return SyntaxRole.String;
        if (lower.Contains("preprocessor") || lower.Contains("directive") || lower.Contains("pragma")) return SyntaxRole.Preprocessor;
        // An attribute's value is quoted text, and reads best as the string it is.
        if (lower.Contains("attributevalue")) return SyntaxRole.String;
        if (lower.Contains("attribute")) return SyntaxRole.Attribute;
        if (lower.Contains("tag") || lower.Contains("element")) return SyntaxRole.Tag;
        if (lower.Contains("method") || lower.Contains("function")) return SyntaxRole.Function;
        // Before the type test: a definition that calls "class" and "struct" its "TypeKeywords" is
        // naming keywords, and a keyword painted as a type reads as the wrong thing entirely.
        if (lower.Contains("keyword") || lower.Contains("modifier") || lower.Contains("visibility")
            || lower.Contains("truefalse") || lower.Contains("thisorbase") || lower.Contains("getsetaddremove")
            || lower.Contains("namespace") || lower.Contains("statement")) return SyntaxRole.Keyword;
        if (lower.Contains("type") || lower.Contains("class") || lower.Contains("struct")
            || lower.Contains("interface") || lower.Contains("enum")) return SyntaxRole.Type;
        if (lower.Contains("punctuation") || lower.Contains("operator") || lower.Contains("brace")
            || lower.Contains("bracket")) return SyntaxRole.Punctuation;
        return null;
    }

    private enum SyntaxRole
    {
        Comment,
        String,
        Number,
        Keyword,
        Type,
        Function,
        Preprocessor,
        Tag,
        Attribute,
        Punctuation,
        Added,
        Removed
    }
}

/// <summary>Applies a language definition through a <see cref="SyntaxPalette"/>.</summary>
public sealed class ThemedHighlightingColorizer : HighlightingColorizer
{
    private readonly SyntaxPalette _palette;

    public ThemedHighlightingColorizer(IHighlightingDefinition definition, SyntaxPalette palette)
        : base(definition)
    {
        _palette = palette ?? throw new ArgumentNullException(nameof(palette));
    }

    protected override void ApplyColorToElement(VisualLineElement element, HighlightingColor color) =>
        base.ApplyColorToElement(element, _palette.Map(color));
}

/// <summary>Picks the language definition for a file.</summary>
public static class SyntaxCatalogue
{
    /// <summary>
    /// Highlighting walks the document from its start to work out what each visible line is inside
    /// of, so a file past this size is left plain rather than made to pay that cost on every jump.
    /// </summary>
    public const int MaximumCharacters = 8_000_000;

    public static IHighlightingDefinition? ForFile(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension)) return null;
        try { return HighlightingManager.Instance.GetDefinitionByExtension(extension); }
        catch (Exception) { return null; }
    }
}

