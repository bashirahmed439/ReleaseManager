using System.IO;
using System.Text;

namespace DeploymentManager.Services;

public sealed class IniConfigurationMerger : IIniConfigurationMerger
{
    private const int MaximumIniFileSize = 16 * 1024 * 1024;

    public async Task<int> CountMissingEntriesAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken = default)
    {
        var sourceText = await File.ReadAllTextAsync(sourcePath, cancellationToken);
        var destinationText = await File.ReadAllTextAsync(destinationPath, cancellationToken);
        return FindMissingEntries(sourceText, destinationText).Sum(group => group.Entries.Count);
    }

    public async Task<int> AppendMissingEntriesAsync(
        string sourcePath,
        string destinationPath,
        Func<byte[], Task>? beforeAppend = null,
        CancellationToken cancellationToken = default)
    {
        var sourceText = await File.ReadAllTextAsync(sourcePath, cancellationToken);
        var sourceEntries = ParseEntries(sourceText);

        await using var destination = new FileStream(
            destinationPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None,
            4096,
            useAsync: true);
        if (destination.Length > MaximumIniFileSize)
        {
            throw new InvalidDataException($"The destination INI file is larger than {MaximumIniFileSize / 1024 / 1024} MB.");
        }

        var originalBytes = new byte[(int)destination.Length];
        await destination.ReadExactlyAsync(originalBytes, cancellationToken);
        var (encoding, preambleLength) = GetEncoding(originalBytes);
        var destinationText = encoding.GetString(originalBytes.AsSpan(preambleLength));
        var missingGroups = FindMissingEntries(sourceEntries, destinationText);
        var missingCount = missingGroups.Sum(group => group.Entries.Count);
        if (missingCount == 0)
        {
            return 0;
        }

        if (beforeAppend is not null)
        {
            await beforeAppend(originalBytes);
        }

        var newline = destinationText.Contains("\r\n", StringComparison.Ordinal)
            ? "\r\n"
            : destinationText.Contains('\r') ? "\r" : "\n";
        var sectionBlocks = FindSectionBlocks(destinationText);
        var insertions = new SortedDictionary<int, List<string>>();
        var sectionsToAppend = new List<MissingEntryGroup>();
        var rootEntriesToAppend = new List<string>();
        foreach (var group in missingGroups)
        {
            if (group.Section.Length == 0)
            {
                rootEntriesToAppend.AddRange(group.Entries.Select(entry => entry.RawLine));
                continue;
            }

            var existingBlock = sectionBlocks.LastOrDefault(block =>
                block.Name.Equals(group.Section, StringComparison.OrdinalIgnoreCase));
            if (existingBlock is null)
            {
                sectionsToAppend.Add(group);
                continue;
            }

            AddInsertion(insertions, existingBlock.End, group.Entries.Select(entry => entry.RawLine));
        }

        var appendedLines = new List<string>(rootEntriesToAppend);
        foreach (var group in sectionsToAppend)
        {
            if (appendedLines.Count > 0)
            {
                appendedLines.Add(string.Empty);
            }

            appendedLines.Add($"[{group.Section}]");
            appendedLines.AddRange(group.Entries.Select(entry => entry.RawLine));
        }

        if (appendedLines.Count > 0)
        {
            AddInsertion(insertions, destinationText.Length, appendedLines);
        }

        var byteInsertions = insertions
            .Select(insertion => new ByteInsertion(
                preambleLength + encoding.GetByteCount(destinationText.AsSpan(0, insertion.Key)),
                encoding.GetBytes(FormatInsertion(destinationText, insertion.Key, insertion.Value, newline))))
            .OrderByDescending(insertion => insertion.Offset)
            .ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var insertion in byteInsertions)
        {
            await InsertBytesAsync(destination, insertion.Offset, insertion.Bytes);
        }

        await destination.FlushAsync(cancellationToken);
        destination.Flush(flushToDisk: true);
        return missingCount;
    }

    private static List<MissingEntryGroup> FindMissingEntries(string sourceText, string destinationText) =>
        FindMissingEntries(ParseEntries(sourceText), destinationText);

    private static List<MissingEntryGroup> FindMissingEntries(
        IReadOnlyList<IniEntry> sourceEntries,
        string destinationText)
    {
        var destinationEntries = ParseEntries(destinationText);
        var destinationKeys = destinationEntries
            .Select(entry => (entry.Section, entry.Key))
            .ToHashSet(EntryIdentityComparer.Instance);
        var sectionGroups = new List<MissingEntryGroup>();
        var groupsBySection = new Dictionary<string, MissingEntryGroup>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in sourceEntries)
        {
            if (!destinationKeys.Add((entry.Section, entry.Key)))
            {
                continue;
            }

            if (!groupsBySection.TryGetValue(entry.Section, out var group))
            {
                group = new MissingEntryGroup(entry.Section);
                groupsBySection.Add(entry.Section, group);
                sectionGroups.Add(group);
            }

            group.Entries.Add(entry);
        }

        if (groupsBySection.ContainsKey(string.Empty)
            && destinationEntries.Any(entry => entry.Section.Length > 0))
        {
            throw new InvalidDataException(
                "New unsectioned configuration.ini entries cannot be appended safely because the destination already contains sections.");
        }

        return sectionGroups;
    }

    private static List<IniEntry> ParseEntries(string text)
    {
        var entries = new List<IniEntry>();
        var currentSection = string.Empty;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith(';') || trimmed.StartsWith('#'))
            {
                continue;
            }

            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                currentSection = trimmed[1..^1].Trim();
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            if (key.Length > 0)
            {
                entries.Add(new IniEntry(currentSection, key, line));
            }
        }

        return entries;
    }

    private static List<SectionBlock> FindSectionBlocks(string text)
    {
        var blocks = new List<SectionBlock>();
        var lines = ReadLines(text).ToArray();
        for (var index = 0; index < lines.Length; index++)
        {
            var trimmed = lines[index].Text.Trim();
            if (!trimmed.StartsWith('[') || !trimmed.EndsWith(']'))
            {
                continue;
            }

            var end = text.Length;
            for (var next = index + 1; next < lines.Length; next++)
            {
                var nextHeader = lines[next].Text.Trim();
                if (nextHeader.StartsWith('[') && nextHeader.EndsWith(']'))
                {
                    end = lines[next].Start;
                    break;
                }
            }

            blocks.Add(new SectionBlock(trimmed[1..^1].Trim(), end));
        }

        return blocks;
    }

    private static IEnumerable<IniLine> ReadLines(string text)
    {
        var start = 0;
        while (start < text.Length)
        {
            var end = text.IndexOfAny(['\r', '\n'], start);
            if (end < 0)
            {
                yield return new IniLine(text[start..], start);
                yield break;
            }

            yield return new IniLine(text[start..end], start);
            start = end + (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1);
        }
    }

    private static void AddInsertion(SortedDictionary<int, List<string>> insertions, int index, IEnumerable<string> lines)
    {
        if (!insertions.TryGetValue(index, out var insertionLines))
        {
            insertionLines = [];
            insertions.Add(index, insertionLines);
        }

        insertionLines.AddRange(lines);
    }

    private static string FormatInsertion(string originalText, int index, IEnumerable<string> lines, string newline)
    {
        var insertion = new StringBuilder();
        if (index > 0 && originalText[index - 1] is not ('\r' or '\n'))
        {
            insertion.Append(newline);
        }

        insertion.Append(string.Join(newline, lines));
        insertion.Append(newline);
        return insertion.ToString();
    }

    private static async Task InsertBytesAsync(FileStream stream, long offset, byte[] insertion)
    {
        var originalLength = stream.Length;
        stream.SetLength(originalLength + insertion.Length);
        var buffer = new byte[64 * 1024];
        var readPosition = originalLength;
        while (readPosition > offset)
        {
            var chunkLength = (int)Math.Min(buffer.Length, readPosition - offset);
            readPosition -= chunkLength;
            stream.Position = readPosition;
            await stream.ReadExactlyAsync(buffer.AsMemory(0, chunkLength));
            stream.Position = readPosition + insertion.Length;
            await stream.WriteAsync(buffer.AsMemory(0, chunkLength));
        }

        stream.Position = offset;
        await stream.WriteAsync(insertion);
    }

    private static (Encoding Encoding, int PreambleLength) GetEncoding(byte[] content)
    {
        if (content.AsSpan().StartsWith(Encoding.UTF8.Preamble))
        {
            return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), Encoding.UTF8.Preamble.Length);
        }

        if (content.AsSpan().StartsWith(Encoding.Unicode.Preamble))
        {
            return (new UnicodeEncoding(bigEndian: false, byteOrderMark: false), Encoding.Unicode.Preamble.Length);
        }

        if (content.AsSpan().StartsWith(Encoding.BigEndianUnicode.Preamble))
        {
            return (new UnicodeEncoding(bigEndian: true, byteOrderMark: false), Encoding.BigEndianUnicode.Preamble.Length);
        }

        return (new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 0);
    }

    private sealed record IniEntry(string Section, string Key, string RawLine);
    private sealed record IniLine(string Text, int Start);
    private sealed record SectionBlock(string Name, int End);
    private sealed record ByteInsertion(int Offset, byte[] Bytes);

    private sealed class MissingEntryGroup(string section)
    {
        public string Section { get; } = section;
        public List<IniEntry> Entries { get; } = [];
    }

    private sealed class EntryIdentityComparer : IEqualityComparer<(string Section, string Key)>
    {
        public static EntryIdentityComparer Instance { get; } = new();

        public bool Equals((string Section, string Key) x, (string Section, string Key) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Section, y.Section)
            && StringComparer.OrdinalIgnoreCase.Equals(x.Key, y.Key);

        public int GetHashCode((string Section, string Key) value) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Section),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Key));
    }
}