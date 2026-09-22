using System.Text;
using System.Text.RegularExpressions;

namespace Encore.ArchitectureTests;

/// <summary>
/// <c>DECISIONS.md</c>'s index against the entries it indexes.
/// </summary>
/// <remarks>
/// <para>
/// The log is append-only and only gets longer, so 060 gave it an index — and an
/// index is a second copy of something, which is the thing
/// <c>Directory.Build.targets</c> opens by arguing against. This is what stops it
/// becoming a list that used to be true.
/// </para>
/// <para>
/// The log is not code, and that is deliberate rather than an oversight in scope.
/// Every other rule in this project that matters is enforced by the build or by a
/// test (043), and the reasoning behind those rules lives here — a log nobody can
/// navigate is a log nobody reads.
/// </para>
/// </remarks>
public partial class DecisionLogTests
{
    private static readonly string LogPath = Path.Combine(EncoreTree.Root, "DECISIONS.md");

    /// <summary>An entry heading: <c>## 058 — Title</c>.</summary>
    [GeneratedRegex(@"^## (?<number>\d{3}) — (?<title>.+)$", RegexOptions.Multiline)]
    private static partial Regex HeadingPattern();

    /// <summary>An index line: <c>- [058](#anchor) — Title</c>.</summary>
    [GeneratedRegex(@"^- \[(?<number>\d{3})\]\(#(?<anchor>[^)]+)\) — (?<title>.+)$", RegexOptions.Multiline)]
    private static partial Regex IndexPattern();

    /// <summary>
    /// Sanity first: an index of nothing matches a log of nothing, and everything
    /// below passes by finding neither.
    /// </summary>
    [Fact]
    public void ThereShouldBeEntriesAndIndexLinesToCompare()
    {
        Assert.NotEmpty(Headings());
        Assert.NotEmpty(IndexLines());
    }

    /// <summary>
    /// The same entries, in the same order, under the same titles.
    /// </summary>
    /// <remarks>
    /// Order matters as much as membership. The log is read top to bottom to see
    /// what was decided when, wrong turns included, and an index sorted some other
    /// way would be describing a different document.
    /// </remarks>
    [Fact]
    public void TheIndexShouldListEveryEntryInOrder()
    {
        var entries = Headings().Select(entry => $"{entry.Number} — {entry.Title}").ToList();
        var indexed = IndexLines().Select(entry => $"{entry.Number} — {entry.Title}").ToList();

        var missing = entries.Except(indexed, StringComparer.Ordinal).ToList();
        var extra = indexed.Except(entries, StringComparer.Ordinal).ToList();

        Assert.True(
            missing.Count == 0,
            $"DECISIONS.md has entries with no line in the index (DECISIONS 060). Missing: {string.Join("; ", missing)}");

        Assert.True(
            extra.Count == 0,
            $"The index in DECISIONS.md lists entries that are not in the log, or gives one a title it does not have. Found: {string.Join("; ", extra)}");

        Assert.Equal(entries, indexed);
    }

    /// <summary>
    /// Numbered from 001 upwards with nothing skipped. Entries are never removed,
    /// so a gap means a number was skipped when one was written.
    /// </summary>
    [Fact]
    public void EntryNumbersShouldRunFrom001WithoutGaps()
    {
        var expected = Headings().Select((_, index) => (index + 1).ToString("000")).ToList();
        var actual = Headings().Select(entry => entry.Number).ToList();

        Assert.Equal(expected, actual);
    }

    /// <summary>
    /// Every index anchor is the slug GitHub would give that heading.
    /// </summary>
    /// <remarks>
    /// The rule is GitHub's: lower-case, drop everything that is not a letter, a
    /// digit, a space, a hyphen or an underscore, then spaces to hyphens. The em
    /// dash disappears and leaves the two spaces around it behind, which is why a
    /// correct anchor has a double hyphen after the number.
    /// <para>
    /// What this cannot check is that the rule is GitHub's. That was checked once,
    /// by clicking. A renderer that disagreed would break all sixty links at the
    /// same moment, which a reader finds immediately — unlike a single stale link,
    /// which is what this is for.
    /// </para>
    /// </remarks>
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
            $"An index anchor in DECISIONS.md does not match the heading it points at (DECISIONS 060). Found: {string.Join("; ", wrong)}");
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

    /// <remarks>
    /// Read whole and matched with <see cref="RegexOptions.Multiline"/> rather than
    /// line by line, so <c>$</c> does the work of stripping the carriage return the
    /// file carries on every line.
    /// </remarks>
    private static string Log() => File.ReadAllText(LogPath);
}
