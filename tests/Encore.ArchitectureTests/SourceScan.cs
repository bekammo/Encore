using System.Text;
using System.Text.RegularExpressions;

namespace Encore.ArchitectureTests;

/// <summary>
/// Tells code from comments and literals and no more: it resolves no names, so tests built on it
/// compare names, not symbols.
/// </summary>
internal static partial class SourceScan
{
    internal static IReadOnlyList<string> Files(string relativeDirectory)
    {
        var directory = Path.Combine(EncoreTree.Root, relativeDirectory);

        Assert.True(Directory.Exists(directory), $"Expected source under {directory}.");

        return
        [
            .. Directory
                .EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                .Where(file => !IsBuildOutput(file))
                .Order(StringComparer.Ordinal)
        ];
    }

    internal static IReadOnlyList<string> ModuleFiles() =>
        [
            .. Directory
                .EnumerateDirectories(Path.Combine(EncoreTree.Root, "src"), "Encore.Modules.*")
                .SelectMany(directory => Files(Path.GetRelativePath(EncoreTree.Root, directory)))
        ];

    internal static string Relative(string file) =>
        Path.GetRelativePath(EncoreTree.Root, file).Replace('\\', '/');

    /// <summary>
    /// Blanks comments and string and char literals, keeping line breaks so line numbers hold. An
    /// interpolated string is blanked whole, holes included, and a string nested in a hole ends
    /// the outer one early; neither shape appears where this is used.
    /// </summary>
    internal static string CodeOnly(string source)
    {
        var code = new StringBuilder(source);
        var i = 0;

        while (i < source.Length)
        {
            var current = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (current == '/' && next == '/')
            {
                i = Blank(code, source, i, source.IndexOf('\n', i));
            }
            else if (current == '/' && next == '*')
            {
                var close = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = Blank(code, source, i, close < 0 ? -1 : close + 2);
            }
            else if (current == '"')
            {
                i = Blank(code, source, i, StringEnd(source, i));
            }
            else if (current == '\'')
            {
                i = Blank(code, source, i, CharEnd(source, i));
            }
            else
            {
                i++;
            }
        }

        return code.ToString();
    }

    internal static IEnumerable<(int Number, string Text)> Lines(string text) =>
        text.Split('\n').Select((line, index) => (index + 1, line.TrimEnd('\r')));

    internal static IEnumerable<string> Identifiers(string line) =>
        IdentifierPattern().Matches(line).Select(match => match.Value);

    internal static IReadOnlySet<string> DeclaredTypes(IEnumerable<string> files) =>
        files
            .SelectMany(file => TypeDeclarationPattern().Matches(CodeOnly(File.ReadAllText(file))))
            .Select(match => match.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);

    internal static IReadOnlySet<string> DeclaredExtensionMethods(IEnumerable<string> files) =>
        files
            .SelectMany(file => ExtensionMethodPattern().Matches(CodeOnly(File.ReadAllText(file))))
            .Select(match => match.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);

    // Code only, so "record of" in a comment declares nothing; PascalCase only, so
    // `var record in records` and `where T : class` do not either.
    [GeneratedRegex(@"\b(?:class|struct|interface|enum|record(?:\s+(?:class|struct))?)\s+(?<name>[A-Z]\w*)")]
    private static partial Regex TypeDeclarationPattern();

    [GeneratedRegex(@"\bstatic\s+(?:[\w<>\[\],.?]+\s+)+?(?<name>[A-Za-z_]\w*)\s*(?:<[^>()]*>)?\s*\(\s*this\s")]
    private static partial Regex ExtensionMethodPattern();

    [GeneratedRegex(@"\b[A-Za-z_]\w*\b")]
    private static partial Regex IdentifierPattern();

    private static int Blank(StringBuilder code, string source, int start, int end)
    {
        if (end < 0)
        {
            end = source.Length;
        }

        for (var j = start; j < end; j++)
        {
            if (source[j] is not ('\r' or '\n'))
            {
                code[j] = ' ';
            }
        }

        return Math.Max(end, start + 1);
    }

    // One past the closing quotes, or -1 if the text ends first. A regular string also stops at
    // a line break.
    private static int StringEnd(string source, int start)
    {
        var quotes = 0;

        while (start + quotes < source.Length && source[start + quotes] == '"')
        {
            quotes++;
        }

        if (quotes >= 3)
        {
            var close = source.IndexOf(new string('"', quotes), start + quotes, StringComparison.Ordinal);
            return close < 0 ? -1 : close + quotes;
        }

        var verbatim = start > 0 && source[start - 1] == '@'
            || start > 1 && source[start - 1] == '$' && source[start - 2] == '@';

        for (var j = start + 1; j < source.Length; j++)
        {
            if (verbatim)
            {
                if (source[j] != '"')
                {
                    continue;
                }

                if (j + 1 < source.Length && source[j + 1] == '"')
                {
                    j++;
                    continue;
                }

                return j + 1;
            }

            if (source[j] == '\\')
            {
                j++;
            }
            else if (source[j] == '"' || source[j] == '\n')
            {
                return j + 1;
            }
        }

        return -1;
    }

    private static int CharEnd(string source, int start)
    {
        for (var j = start + 1; j < source.Length; j++)
        {
            if (source[j] == '\\')
            {
                j++;
            }
            else if (source[j] == '\'' || source[j] == '\n')
            {
                return j + 1;
            }
        }

        return -1;
    }

    private static bool IsBuildOutput(string file) =>
        file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
        || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
}
