namespace Automation.MemoryProbe;

internal static class MemoryPatternSearch
{
    internal static IReadOnlyList<int> FindOffsets(
        ReadOnlySpan<byte> haystack,
        ReadOnlySpan<byte> needle)
    {
        if (needle.IsEmpty || haystack.Length < needle.Length)
        {
            return [];
        }

        var offsets = new List<int>();
        var searchOffset = 0;
        while (searchOffset <= haystack.Length - needle.Length)
        {
            var relativeOffset = haystack[searchOffset..].IndexOf(needle);
            if (relativeOffset < 0)
            {
                break;
            }

            var absoluteOffset = searchOffset + relativeOffset;
            offsets.Add(absoluteOffset);
            searchOffset = absoluteOffset + 1;
        }

        return offsets;
    }
}