using System.Text;
using System.Text.RegularExpressions;

namespace Encore.ArchitectureTests;

/// <summary>
/// <c>DECISIONS.md</c>'s index against its entries, so the index cannot drift.
/// </summary>
public partial class DecisionLogTests
{
    private static readonly string LogPath = Path.Combine(EncoreTree.Root, "DECISIONS.md");

    /// <summary>An entry heading: <c>## 001 — Title</c>.</summary>
    [GeneratedRegex(@"^## (?<number>\d{3}) — (?<title>.+)$", RegexOptions.Multiline)]
    private static partial Regex HeadingPattern();

    /// <summary>An index line: <c>- [001](#anchor) — Title</c>.</summary>
    [GeneratedRegex(@"^- \[(?<number>\d{3})\]\(#(?<anchor>[^)]+)\) — (?<title>.+)$", RegexOptions.Multiline)]
    private static partial Regex IndexPattern();

    /// <summary>Any numbered heading, however it is punctuated.</summary>
    [GeneratedRegex(@"^## \d{3}\b", RegexOptions.Multiline)]
    private static partial Regex AnyNumberedHeading();

    /// <summary>
    /// A heading written with a hyphen instead of the em dash would match nothing above and pass
    /// every other test unseen. Counting headings loosely catches it.
    /// </summary>
    [Fact]
    public void EveryNumberedHeadingShouldBeWrittenAsAnEntry() =>
        Assert.Equal(AnyNumberedHeading().Matches(Log()).Count, Headings().Count);

    /// <summary>Sanity: an empty index matches an empty log, and the tests below would pass vacuously.</summary>
    [Fact]
    public void ThereShouldBeEntriesAndIndexLinesToCompare()
    {
        Assert.NotEmpty(Headings());
        Assert.NotEmpty(IndexLines());
    }

    /// <summary>The same entries, in the same order, under the same titles.</summary>
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

    /// <summary>Numbered from 001 upwards with nothing skipped.</summary>
    [Fact]
    public void EntryNumbersShouldRunFrom001WithoutGaps()
    {
        var expected = Headings().Select((_, index) => (index + 1).ToString("000")).ToList();
        var actual = Headings().Select(entry => entry.Number).ToList();

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Every index anchor is the slug GitHub gives that heading: lower-case, keep letters,
    /// digits, hyphens and underscores, spaces to hyphens. The em dash leaves a double hyphen.
    /// </summary>
    [Fact]
    public void EveryIndexAnchorShouldMatchItsHeading()
    {
        var wrong = IndexLines()
            .Select(entry => (entry.Number, entry.Anchor, Expected: Slug($"{entry.Number} — {entry.Title}")))
            .Where(entry => !string.Equals(entry.Anchor, entry.Expected, StringComparison.Ordinal))
            .Select(entry => $"{entry.Number}: links to #{entry.Anchor}, heading slugs to #{entry.Expected}")
            .ToList();

        Assert.True(
            wrong.Count == 0,
            $"An index anchor in DECISIONS.md does not match the heading it points at. Found: {string.Join("; ", wrong)}");
    }

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

    /// <remarks>Matched multiline, so <c>$</c> also strips each line's carriage return.</remarks>
    private static string Log() => File.ReadAllText(LogPath);
}
