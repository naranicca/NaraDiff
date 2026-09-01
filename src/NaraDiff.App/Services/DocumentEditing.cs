using System.Text;
using ICSharpCode.AvalonEdit.Document;
using NaraDiff.Core.Text;

namespace NaraDiff.App.Services;

/// <summary>
/// Line based edits on an AvalonEdit document. All hunk operations go through here so that line
/// terminators stay consistent and every operation is a single undo step.
/// </summary>
public static class DocumentEditing
{
    /// <summary>
    /// Replaces <paramref name="count"/> lines starting at <paramref name="startLine"/> with the
    /// given lines. An empty range inserts, an empty replacement deletes.
    /// </summary>
    public static void ReplaceLines(TextDocument document, int startLine, int count, IReadOnlyList<TextLine> source, LineEndingKind fallbackEnding)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(source);
        var eol = LineEndings.ToLiteral(fallbackEnding == LineEndingKind.None ? LineEndingKind.Lf : fallbackEnding);
        var lineCount = document.LineCount;
        startLine = Math.Clamp(startLine, 0, lineCount);
        int startOffset;
        var length = 0;
        var replacedDelimiter = false;
        // True when the new lines go after the last line of the document rather than in front of an
        // existing one. A document that ends with a terminator still has a last (empty) line, so this
        // cannot be decided from the offset alone.
        var appendAfterLastLine = count <= 0 && startLine >= lineCount;
        if (count <= 0)
        {
            startOffset = appendAfterLastLine ? document.TextLength : document.GetLineByNumber(startLine + 1).Offset;
        }
        else
        {
            var first = document.GetLineByNumber(Math.Min(startLine + 1, lineCount));
            var last = document.GetLineByNumber(Math.Clamp(startLine + count, 1, lineCount));
            startOffset = first.Offset;
            length = last.EndOffset + last.DelimiterLength - first.Offset;
            replacedDelimiter = last.DelimiterLength > 0;
            // Deleting up to the end of the document has to take the terminator of the previous line
            // with it, otherwise the file keeps an empty line where the deleted block used to start.
            if (source.Count == 0 && last.DelimiterLength == 0 && startLine > 0)
            {
                var previous = document.GetLineByNumber(startLine);
                startOffset -= previous.DelimiterLength;
                length += previous.DelimiterLength;
            }
        }
        var text = BuildText(source, eol);
        if (count > 0 && replacedDelimiter && text.Length > 0 && !EndsWithBreak(text)) text += eol;
        // A source range that only holds the empty last line still means "the target ends with a
        // terminator", so the emptiness of the built text must not skip this step.
        if (count <= 0 && source.Count > 0)
        {
            // Inserting before an existing line needs a terminator at the end of the new text;
            // appending after the last line needs one in front of it, because the last line keeps its
            // content and only gains a terminator.
            if (!appendAfterLastLine)
            {
                if (!EndsWithBreak(text)) text += eol;
            }
            else
            {
                text = eol + text;
            }
        }
        document.Replace(startOffset, length, text);
    }

    /// <summary>Joins lines, filling in the fallback terminator where a source line had none.</summary>
    public static string BuildText(IReadOnlyList<TextLine> source, string eol)
    {
        ArgumentNullException.ThrowIfNull(source);
        var builder = new StringBuilder();
        for (var i = 0; i < source.Count; i++)
        {
            var line = source[i];
            builder.Append(line.Text);
            if (line.Ending != LineEndingKind.None) builder.Append(LineEndings.ToLiteral(line.Ending));
            else if (i < source.Count - 1) builder.Append(eol);
        }
        return builder.ToString();
    }

    private static bool EndsWithBreak(string text) => text.EndsWith('\n') || text.EndsWith('\r');

    /// <summary>The lines of a document range, used as the source of a copy operation.</summary>
    public static List<TextLine> ReadLines(IReadOnlyList<TextLine> lines, int start, int count)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var result = new List<TextLine>(Math.Max(0, count));
        for (var i = start; i < start + count && i < lines.Count; i++) result.Add(lines[i]);
        return result;
    }
}
