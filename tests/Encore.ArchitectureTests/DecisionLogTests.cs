using System.Text;
using System.Text.RegularExpressions;

namespace Encore.ArchitectureTests;

public sealed partial class DecisionLogTests
{
    private static readonly string LogPath = Path.Combine(EncoreTree.Root, "DECISIONS.md");

    [GeneratedRegex(@"^## (?<number>\d{3}) — (?<title>.+)$", RegexOptions.Multiline)]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"^- \[(?<number>\d{3})\]\(#(?<anchor>[^)]+)\) — (?<title>.+)$", RegexOptions.Multiline)]
    private static partial Regex IndexPattern();

    [GeneratedRegex(@"^## \d{3}\b", RegexOptions.Multiline)]
    private static partial Regex AnyNumberedHeading();

    /// <summary>Catches a heading with a hyphen for the em dash, which every other test here misses.</summary>
    [Fact]
    public void EveryNumberedHeadingShouldBeWrittenAsAnEntry() =>
        Assert.Equal(AnyNumberedHeading().Matches(Log()).Count, Headings().Count);

    [Fact]
    public void ThereShouldBeEntriesAndIndexLinesToCompare()
    {
        Assert.NotEmpty(Headings());
        Assert.NotEmpty(IndexLines());
    }

    [Fact]
    public void TheIndexShouldListEveryEntryInOrder()
    {
        var entries = Headings().Select(entry => $"{entry.Number} — {entry.Title}").ToList();
        var indexed = IndexLines().Select(entry => $"{entry.Number} — {entry.Title}").ToList();

        var missing = entries.Except(indexed, StringComparer.Ordinal).ToList();
        var extra = indexed.Except(entries, StringComparer.Ordinal).ToList();

        Assert.True(
            missing.Count == 0,
            $"DECISIONS.md has entries with no line in the index. Missing: {string.Join("; ", missing)}");

        Assert.True(
            extra.Count == 0,
            $"The index in DECISIONS.md lists entries that are not in the log, or gives one a title it does not have. Found: {string.Join("; ", extra)}");

        Assert.Equal(entries, indexed);
    }

    [Fact]
    public void EntryNumbersShouldRunFrom001WithoutGaps()
    {
        var expected = Headings().Select((_, index) => (index + 1).ToString("000")).ToList();
        var actual = Headings().Select(entry => entry.Number).ToList();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EveryIndexAnchorShouldMatchItsHeading()
    {
        var wrong = IndexLines()
            .Select(entry => (entry.Number, entry.Anchor, Expected: Slug($"{entry.Number} — {entry.Title}")))
            .Where(entry => entry.Anchor != entry.Expected)
            .Select(entry => $"{entry.Number}: links to #{entry.Anchor}, heading slugs to #{entry.Expected}")
            .ToList();

        Assert.True(
            wrong.Count == 0,
            $"An index anchor in DECISIONS.md does not match the heading it points at. Found: {string.Join("; ", wrong)}");
    }

    // GitHub's heading slug. The em dash leaves a double hyphen.
    private static string Slug(string heading)
    {
        var slug = new StringBuilder(heading.Length);

        foreach (var character in heading.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(character) || character is '-' or '_')
            {
                slug.Append(character);
            }
            else if (character is ' ')
            {
                slug.Append('-');
            }
        }

        return slug.ToString();
    }

    // On a CRLF checkout .+ keeps the \r, since $ matches only before \n. TrimEnd drops it.
    private static List<(string Number, string Title)> Headings() =>
        [.. HeadingPattern()
            .Matches(Log())
            .Select(match => (match.Groups["number"].Value, match.Groups["title"].Value.TrimEnd()))];

    private static List<(string Number, string Anchor, string Title)> IndexLines() =>
        [.. IndexPattern()
            .Matches(Log())
            .Select(match => (
                match.Groups["number"].Value,
                match.Groups["anchor"].Value,
                match.Groups["title"].Value.TrimEnd()))];

    private static string Log() => File.ReadAllText(LogPath);
}
