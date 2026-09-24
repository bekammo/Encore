using System.Text;
using System.Text.RegularExpressions;

namespace Encore.ArchitectureTests;

/// <summary>
/// Reads C# source as text, for the rules that live inside one assembly or that a compiled
/// reference cannot show. It knows enough C# to tell code from comments and literals, and no
/// more: it does not resolve a name, so the tests built on it compare names, not symbols.
/// </summary>
internal static partial class SourceScan
{
    /// <summary>Every <c>.cs</c> file under a directory, given relative to the root, build output excluded.</summary>
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

    /// <summary>Every <c>.cs</c> file of every project whose directory starts with <c>Encore.Modules.</c>.</summary>
    internal static IReadOnlyList<string> ModuleFiles() =>
        [
            .. Directory
                .EnumerateDirectories(Path.Combine(EncoreTree.Root, "src"), "Encore.Modules.*")
                .SelectMany(directory => Files(Path.GetRelativePath(EncoreTree.Root, directory)))
        ];

    /// <summary>A file's path from the root, with forward slashes, for a failure message.</summary>
    internal static string Relative(string file) =>
        Path.GetRelativePath(EncoreTree.Root, file).Replace('\\', '/');

    /// <summary>
    /// The source with every comment and every string or character literal blanked to spaces.
    /// Line breaks survive, so line <c>n</c> of the result is line <c>n</c> of the file.
    /// </summary>
    /// <remarks>
    /// An interpolated string is blanked whole, holes included, and a string nested inside a
    /// hole ends the outer one early. Neither shape appears where this is used.
    /// </remarks>
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

    /// <summary>The lines of a text, numbered from one.</summary>
    internal static IEnumerable<(int Number, string Text)> Lines(string text) =>
        text.Split('\n').Select((line, index) => (index + 1, line.TrimEnd('\r')));

    /// <summary>The identifiers on one line, in order.</summary>
    internal static IEnumerable<string> Identifiers(string line) =>
        IdentifierPattern().Matches(line).Select(match => match.Value);

    /// <summary>The names of the types declared in these files, nested ones included.</summary>
    internal static IReadOnlySet<string> DeclaredTypes(IEnumerable<string> files) =>
        files
            .SelectMany(file => TypeDeclarationPattern().Matches(CodeOnly(File.ReadAllText(file))))
            .Select(match => match.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>The names of the extension methods declared in these files.</summary>
    internal static IReadOnlySet<string> DeclaredExtensionMethods(IEnumerable<string> files) =>
        files
            .SelectMany(file => ExtensionMethodPattern().Matches(CodeOnly(File.ReadAllText(file))))
            .Select(match => match.Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// <c>class Seat</c>, <c>record struct Outcome</c> and the rest. Run over code only, so a
    /// sentence in a comment that says "record of" declares nothing, and PascalCase only, so
    /// <c>var record in records</c> and <c>where T : class</c> do not either.
    /// </summary>
    [GeneratedRegex(@"\b(?:class|struct|interface|enum|record(?:\s+(?:class|struct))?)\s+(?<name>[A-Z]\w*)")]
    private static partial Regex TypeDeclarationPattern();

    /// <summary><c>static IServiceCollection AddXModule(this ...</c>, however it wraps.</summary>
    [GeneratedRegex(@"\bstatic\s+(?:[\w<>\[\],.?]+\s+)+?(?<name>[A-Za-z_]\w*)\s*(?:<[^>()]*>)?\s*\(\s*this\s")]
    private static partial Regex ExtensionMethodPattern();

    [GeneratedRegex(@"\b[A-Za-z_]\w*\b")]
    private static partial Regex IdentifierPattern();

    /// <summary>
    /// Blanks <paramref name="start"/> up to <paramref name="end"/>, or to the end of the text
    /// when <paramref name="end"/> is negative, keeping line breaks. Returns where to carry on.
    /// </summary>
    private static int Blank(StringBuilder code, string source, int start, int end)
    {
        if (end < 0 || end > source.Length)
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

    /// <summary>
    /// Where the string literal opening at <paramref name="start"/> ends, one past its last
    /// quote. Raw (<c>"""</c>), verbatim (<c>@"</c>) and regular strings.
    /// </summary>
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

    /// <summary>Where the character literal opening at <paramref name="start"/> ends, one past its closing quote.</summary>
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
