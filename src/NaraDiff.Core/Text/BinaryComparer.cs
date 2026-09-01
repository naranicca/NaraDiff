using System.Security.Cryptography;
using System.Text;

namespace NaraDiff.Core.Text;

/// <summary>One row of a hex dump, with a per-byte mask of the bytes that differ from the other side.</summary>
public sealed class HexRow
{
    public HexRow(long offset, byte[] bytes, bool[] differences)
    {
        Offset = offset;
        Bytes = bytes;
        Differences = differences;
    }

    public long Offset { get; }

    public byte[] Bytes { get; }

    public bool[] Differences { get; }

    public bool HasDifference => Differences.Any(value => value);

    public string OffsetText => Offset.ToString("X8");

    public string HexText
    {
        get
        {
            var builder = new StringBuilder(Bytes.Length * 3);
            for (var i = 0; i < Bytes.Length; i++)
            {
                if (i > 0) builder.Append(i % 8 == 0 ? "  " : " ");
                builder.Append(Bytes[i].ToString("X2"));
            }
            return builder.ToString();
        }
    }

    public string AsciiText
    {
        get
        {
            var builder = new StringBuilder(Bytes.Length);
            foreach (var b in Bytes) builder.Append(b >= 0x20 && b < 0x7F ? (char)b : '.');
            return builder.ToString();
        }
    }
}

/// <summary>Summary of a byte level comparison used when at least one side is binary.</summary>
public sealed class BinaryComparisonSummary
{
    public required long LeftLength { get; init; }

    public required long RightLength { get; init; }

    public required bool Identical { get; init; }

    public long? FirstDifferenceOffset { get; init; }

    public long DifferentByteCount { get; init; }

    public string LeftHash { get; init; } = string.Empty;

    public string RightHash { get; init; } = string.Empty;
}

public static class BinaryComparer
{
    public const int BytesPerRow = 16;

    public static BinaryComparisonSummary Compare(byte[] left, byte[] right, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        long? first = null;
        long different = 0;
        var shared = Math.Min(left.Length, right.Length);
        for (var i = 0; i < shared; i++)
        {
            if ((i & 0xFFFF) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (left[i] == right[i]) continue;
            first ??= i;
            different++;
        }
        if (left.Length != right.Length)
        {
            first ??= shared;
            different += Math.Abs((long)left.Length - right.Length);
        }
        return new BinaryComparisonSummary
        {
            LeftLength = left.LongLength,
            RightLength = right.LongLength,
            Identical = first is null,
            FirstDifferenceOffset = first,
            DifferentByteCount = different,
            LeftHash = Hash(left),
            RightHash = Hash(right)
        };
    }

    public static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes))[..16];

    /// <summary>Builds the hex rows of one side, marking bytes that differ from the other side.</summary>
    public static List<HexRow> BuildRows(byte[] bytes, byte[] other, int maxRows = 20000)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentNullException.ThrowIfNull(other);
        var rows = new List<HexRow>();
        for (long offset = 0; offset < bytes.Length && rows.Count < maxRows; offset += BytesPerRow)
        {
            var length = (int)Math.Min(BytesPerRow, bytes.Length - offset);
            var slice = new byte[length];
            Array.Copy(bytes, offset, slice, 0, length);
            var mask = new bool[length];
            for (var i = 0; i < length; i++)
            {
                var index = offset + i;
                mask[i] = index >= other.Length || other[index] != slice[i];
            }
            rows.Add(new HexRow(offset, slice, mask));
        }
        return rows;
    }
}
