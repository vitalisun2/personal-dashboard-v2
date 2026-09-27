namespace PersonalDashboard.V2.Search.Domain;

/// <summary>Finds sentence and Markdown-list boundaries while preserving offsets in the original text.</summary>
public static class SentenceSegmenter
{
    public static IReadOnlyList<TextRange> Split(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var ranges = new List<TextRange>();
        var start = 0;
        for (var index = 0; index < text.Length; index++)
        {
            var character = text[index];
            var boundary = character is '\r' or '\n'
                || character is '.' or '!' or '?' && (index + 1 == text.Length || char.IsWhiteSpace(text[index + 1]));
            if (!boundary) continue;
            Add(text, start, character is '\r' or '\n' ? index : index + 1, ranges);
            start = index + 1;
            if (character == '\r' && start < text.Length && text[start] == '\n') start++;
        }
        Add(text, start, text.Length, ranges);
        return ranges;
    }

    private static void Add(string text, int start, int end, ICollection<TextRange> ranges)
    {
        while (start < end && char.IsWhiteSpace(text[start])) start++;
        while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
        if (start < end && text[start] is '-' or '*' or '•' && start + 1 < end && char.IsWhiteSpace(text[start + 1]))
        {
            start++;
            while (start < end && char.IsWhiteSpace(text[start])) start++;
        }
        if (end > start) ranges.Add(new TextRange(start, end - start));
    }
}
