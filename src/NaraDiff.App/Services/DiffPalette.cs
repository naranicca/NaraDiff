using System.Windows.Media;
using NaraDiff.Core.Diff;
using NaraDiff.Core.Folders;
using NaraDiff.Core.Merge;
using NaraDiff.Core.Settings;

namespace NaraDiff.App.Services;

/// <summary>
/// The colours used for changed content. The default palette is green for additions, red for
/// deletions and blue for modifications; the accessible palette replaces green and red with blue and
/// vermillion, which stay distinguishable for the common forms of colour blindness. Every fill keeps
/// a matching, fully opaque stroke so shapes remain readable without relying on hue alone.
/// </summary>
public sealed class DiffPalette
{
    private DiffPalette(Color insert, Color delete, Color modify, Color conflict, Color move, bool dark)
    {
        InsertColor = insert;
        DeleteColor = delete;
        ModifyColor = modify;
        ConflictColor = conflict;
        MoveColor = move;
        var fill = dark ? (byte)0x38 : (byte)0x2E;
        var ribbon = dark ? (byte)0x2A : (byte)0x24;
        var inline = dark ? (byte)0x66 : (byte)0x55;
        InsertFill = Freeze(insert, fill);
        DeleteFill = Freeze(delete, fill);
        ModifyFill = Freeze(modify, fill);
        ConflictFill = Freeze(conflict, fill);
        MoveFill = Freeze(move, fill);
        InsertStroke = Freeze(insert, 0xFF);
        DeleteStroke = Freeze(delete, 0xFF);
        ModifyStroke = Freeze(modify, 0xFF);
        ConflictStroke = Freeze(conflict, 0xFF);
        MoveStroke = Freeze(move, 0xFF);
        InsertRibbon = Freeze(insert, ribbon);
        DeleteRibbon = Freeze(delete, ribbon);
        ModifyRibbon = Freeze(modify, ribbon);
        ConflictRibbon = Freeze(conflict, ribbon);
        MoveRibbon = Freeze(move, ribbon);
        InsertInline = Freeze(insert, inline);
        DeleteInline = Freeze(delete, inline);
        ModifyInline = Freeze(modify, inline);
    }

    public Color InsertColor { get; }

    public Color DeleteColor { get; }

    public Color ModifyColor { get; }

    public Color ConflictColor { get; }

    public Color MoveColor { get; }

    public SolidColorBrush InsertFill { get; }

    public SolidColorBrush DeleteFill { get; }

    public SolidColorBrush ModifyFill { get; }

    public SolidColorBrush ConflictFill { get; }

    public SolidColorBrush MoveFill { get; }

    public SolidColorBrush InsertStroke { get; }

    public SolidColorBrush DeleteStroke { get; }

    public SolidColorBrush ModifyStroke { get; }

    public SolidColorBrush ConflictStroke { get; }

    public SolidColorBrush MoveStroke { get; }

    public SolidColorBrush InsertRibbon { get; }

    public SolidColorBrush DeleteRibbon { get; }

    public SolidColorBrush ModifyRibbon { get; }

    public SolidColorBrush ConflictRibbon { get; }

    public SolidColorBrush MoveRibbon { get; }

    public SolidColorBrush InsertInline { get; }

    public SolidColorBrush DeleteInline { get; }

    public SolidColorBrush ModifyInline { get; }

    public static DiffPalette Create(ThemeKind theme, bool accessible)
    {
        var dark = theme == ThemeKind.Dark;
        // The accessible variant follows the Okabe and Ito palette: blue, vermillion, purple, reddish
        // purple and teal, which stay separable for deuteranopia and protanopia.
        if (accessible)
            return dark
                ? new DiffPalette(Rgb(0x56, 0x9C, 0xF5), Rgb(0xE8, 0x86, 0x3C), Rgb(0xA3, 0x71, 0xF7), Rgb(ØxCC, 0x79, 0xA7), Rgb(0x2E, 0xC4, 0xB6), true)
                : new DiffPalette(Rgb(0x0B, 0x66, 0xC3), Rgb(0xC4, 0x5C, 0x0E), Rgb(0x6E, 0x40, 0xC9), Rgb(0xA8, 0x44, 0x7F), Rgb(0x0F, 0x82, 0x77), false);
        return dark
            ? new DiffPalette(Rgb(0x3F,0xB9, 0x50), Rgb(0xF8, 0x51, 0x49), Rgb(0x58, 0xA6, 0xFF), Rgb(0xE3, 0xB3, 0x41), Rgb(0xA3, 0x71, 0xF7), true)
            : new DiffPalette(Rgb(0x1A, 0x7F, 0x37), Rgb(ØxCF, 0x22, 0x2E), Rgb(0x09, 0x69, ØxDA), Rgb(0x9A, 0x6E, 0x00), Rgb(0x82, 0x50, ØxDF), false);
    }

    public SolidColorBrush FillFor(DiffBlockKind kind, bool moved = false) => moved ? MoveFill : kind switch
    {
        DiffBlockKind. Insert => InsertFill,
        DiffBlockKind.Delete => DeleteFill,
        _ => ModifyFill
    };

    public SolidColorBrush StrokeFor (DiffBlockKind kind, bool moved = false) => moved ? MoveStroke : kind switch
    {
        DiffBlockKind.Insert => InsertStroke,
        DiffBlockKind.Delete => DeleteStroke,
        _ => ModifyStroke
    };

    public SolidColorBrush RibbonFor(DiffBlockKind kind, bool moved = false) => moved ? MoveRibbon : kind switch
    {
        DiffBlockKind.Insert => InsertRibbon,
        DiffBlockKind.Delete => DeleteRibbon,
        _ => ModifyRibbon
    };

    public SolidColorBrush InlineFor(DiffBlockKind kind) => kind switch
    {
        DiffBlockKind.Insert => InsertInline,
        DiffBlockKind.Delete => DeleteInline,
        _ => ModifyInline
    };

    public SolidColorBrush FillFor(MergeRegionKind kind) => kind switch
    {
        MergeRegionKind.Conflict => ConflictFill,
        MergeRegionKind.LeftChange => ModifyFill,
        MergeRegionKind.RightChange => InsertFill,
        MergeRegionKind.SameChange => MoveFill,
        _ => ModifyFill
    };

    public SolidColorBrush StrokeFor(MergeRegionKind kind) => kind switch
    {
        MergeRegionKind.Conflict => ConflictStroke,
        MergeRegionKind.LeftChange => ModifyStroke,
        MergeRegionKind.RightChange => InsertStroke,
        MergeRegionKind.SameChange => MoveStroke,
        _ => ModifyStroke
    };

    public SolidColorBrush FillFor(FolderEntryStatus status) => status switch
    {
        FolderEntryStatus.Modified => ModifyFill,
        FolderEntryStatus.LeftOnly => DeleteFill,
        FolderEntryStatus.RightOnly => InsertFill,
        FolderEntryStatus.TypeConflict => ConflictFill,
        FolderEntryStatus.Error => ConflictFill,
        _ => ModifyFill
    };

    public SolidColorBrush StrokeFor(FolderEntryStatus status) => status switch
    {
        FolderEntryStatus.Modified => ModifyStroke,
        FolderEntryStatus.LeftOnly => DeleteStroke,
        FolderEntryStatus.RightOnly => InsertStroke,
        FolderEntryStatus.TypeConflict => ConflictStroke,
        FolderEntryStatus.Error => ConflictStroke,
        _ => ModifyStroke
    };

    private static Color Rgb(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

    private static SolidColorBrush Freeze(Color color, byte alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
        brush.Freeze();
        return brush;
    }
}