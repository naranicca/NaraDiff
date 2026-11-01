using System.IO;

namespace NaraDiff.App.Services;

public static class PathNaming
{
    private static readonly char[] Separators = [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar];

    public static (string Left, string Right) Shorten(string? left, string? right)
    {
        var leftName = Name(left);
        var rightName = Name(right);
        // Different file names already say which side is which.
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)
            || !string.Equals(leftName, rightName, StringComparison.OrdinalIgnoreCase))
            return (leftName, rightName);

        var leftParts = left.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        var rightParts = right.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        var shared = 0;
        while (shared < leftParts.Length - 1 && shared < rightParts.Length - 1
            && string.Equals(leftParts[shared], rightParts[shared], StringComparison.OrdinalIgnoreCase))
            shared++;
        return (Decorate(leftParts, shared, leftName), Decorate(rightParts, shared, rightName));
    }

    private static string Decorate(string[] parts, int shared, string name) =>
        shared < parts.Length - 1 ? $"[{parts[shared]}] {name}" : name;

    private static string Name(string? path)
    {
        var name = string.IsNullOrEmpty(path) ? null : Path.GetFileName(path);
        return string.IsNullOrEmpty(name) ? "untitled" : name;
    }
}
