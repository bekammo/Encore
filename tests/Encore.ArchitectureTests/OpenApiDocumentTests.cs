using System.Text.Json;
using System.Text.RegularExpressions;

namespace Encore.ArchitectureTests;

/// <summary>
/// Reads source text rather than booting a host, which would need Postgres, Redis and a
/// forbidden project reference.
/// </summary>
public sealed partial class OpenApiDocumentTests
{
    private static readonly string DocumentPath =
        Path.Combine(EncoreTree.Root, "src", "Encore.Api", "wwwroot", "docs", "openapi.json");

    private static readonly string[] OperationKeys =
        ["get", "put", "post", "delete", "options", "head", "patch", "trace"];

    [Fact]
    public void TheDocumentShouldExist() =>
        Assert.True(
            File.Exists(DocumentPath),
            $"The OpenAPI document is served from wwwroot and maintained by hand; expected it at {DocumentPath}.");

    [Fact]
    public void ThereShouldBeRoutesToInspect()
    {
        Assert.NotEmpty(MappedRoutes());
        Assert.NotEmpty(DocumentedRoutes());
    }

    [Fact]
    public void EveryMappedRouteShouldAppearInTheDocument()
    {
        var documented = DocumentedRoutes().ToHashSet();

        var missing = MappedRoutes()
            .Where(route => !documented.Contains(route))
            .Distinct()
            .Select(route => route.ToString())
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"These routes are mapped but absent from src/Encore.Api/wwwroot/docs/openapi.json, which is maintained by hand: {string.Join("; ", missing)}");
    }

    [Fact]
    public void EveryDocumentedRouteShouldBeMapped()
    {
        var mapped = MappedRoutes().ToHashSet();

        var stale = DocumentedRoutes()
            .Where(route => !mapped.Contains(route))
            .Select(route => route.ToString())
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            stale.Count == 0,
            $"These routes are documented in src/Encore.Api/wwwroot/docs/openapi.json but nothing maps them: {string.Join("; ", stale)}");
    }

    [Theory]
    [InlineData("OrderResponse", "src/Encore.Modules.Orders/Models/OrderStatus.cs")]
    [InlineData("PaymentResponse", "src/Encore.Modules.Payments/Models/PaymentStatus.cs")]
    public void EveryDocumentedStatusShouldBeExactlyWhatTheCodeRenders(string schema, string enumSource)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(DocumentPath));

        var documented = document.RootElement
            .GetProperty("components").GetProperty("schemas").GetProperty(schema)
            .GetProperty("properties").GetProperty("status").GetProperty("enum")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .Order(StringComparer.Ordinal)
            .ToList();

        var rendered = EnumMemberPattern()
            .Matches(File.ReadAllText(Path.Combine(EncoreTree.Root, enumSource)))
            .Select(match => SnakeCase(match.Groups["name"].Value))
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(rendered);
        Assert.Equal(rendered, documented);
    }

    // Explicit values only, as every status enum is written: `TimedOut = 4,`
    [GeneratedRegex(@"^\s*(?<name>[A-Z]\w*)\s*=\s*\d+\s*,?\s*$", RegexOptions.Multiline)]
    private static partial Regex EnumMemberPattern();

    private static string SnakeCase(string name) =>
        string.Concat(name.Select((character, index) =>
            char.IsUpper(character) && index > 0
                ? $"_{char.ToLowerInvariant(character)}"
                : char.ToLowerInvariant(character).ToString()));

    /// <summary>
    /// A path carries its own <c>servers</c> entry exactly when the monolith, which serves the
    /// page, does not map it (008).
    /// </summary>
    [Fact]
    public void EveryDocumentedPathShouldSayWhetherThisHostServesIt()
    {
        var servedByMonolith = RoutesServedBy("Encore.Api");
        var wrong = new List<string>();

        using var document = JsonDocument.Parse(File.ReadAllText(DocumentPath));

        foreach (var path in document.RootElement.GetProperty("paths").EnumerateObject())
        {
            var overridden = path.Value.TryGetProperty("servers", out var servers);

            foreach (var operation in path.Value.EnumerateObject()
                .Where(member => OperationKeys.Contains(member.Name, StringComparer.OrdinalIgnoreCase)))
            {
                var route = new Route(operation.Name.ToUpperInvariant(), Combine(string.Empty, path.Name));
                var monolithServes = servedByMonolith.Contains(route);

                if (monolithServes && overridden)
                {
                    wrong.Add($"{route} carries a servers override but Encore.Api maps it");
                }
                else if (!monolithServes && !overridden)
                {
                    wrong.Add($"{route} is not mapped by Encore.Api, so it needs a servers entry naming the host that maps it");
                }
            }

            if (overridden && !servers.EnumerateArray().Any(server =>
                server.TryGetProperty("url", out var url)
                && url.GetString()?.Contains("8081", StringComparison.Ordinal) is true))
            {
                wrong.Add($"{path.Name} overrides servers without naming the Payments host");
            }
        }

        Assert.True(
            wrong.Count == 0,
            $"The document and the hosts disagree about who serves what: {string.Join("; ", wrong)}");
    }

    [Fact]
    public void TheTwoHostsShouldServeDifferentRouteSets()
    {
        var monolith = RoutesServedBy("Encore.Api");
        var payments = RoutesServedBy("Encore.Payments.Api");

        Assert.NotEmpty(monolith);
        Assert.NotEmpty(payments);

        Assert.False(monolith.SetEquals(payments), "Both hosts resolved to the same routes.");

        Assert.Contains(new Route("POST", "/internal/payments/authorize"), payments);
        Assert.DoesNotContain(new Route("POST", "/internal/payments/authorize"), monolith);

        // Something both serve, so "different" does not become "disjoint".
        Assert.Contains(new Route("GET", "/health"), monolith);
        Assert.Contains(new Route("GET", "/health"), payments);
    }

    [Fact]
    public void EveryRouteRegistrationShouldBeOneThisCanRead()
    {
        var unreadable = new List<string>();

        foreach (var file in SourceFiles())
        {
            var source = WithoutComments(File.ReadAllText(file));
            var relative = SourceScan.Relative(file);

            var registrations = AnyMapPattern().Count(source);
            var readable = MapPattern().Count(source);

            if (registrations != readable)
            {
                unreadable.Add($"{relative}: {registrations} route registration(s), {readable} readable");
            }

            var groups = AnyGroupPattern().Count(source);
            var readableGroups = GroupPattern().Count(source);

            if (groups != readableGroups)
            {
                unreadable.Add($"{relative}: {groups} route group(s), {readableGroups} readable");
            }
        }

        Assert.True(
            unreadable.Count == 0,
            $"A route is registered in a shape this suite cannot read, so it is missing from both sides of the comparison rather than failing one of them. Found: {string.Join("; ", unreadable)}");
    }

    private readonly record struct Route(string Method, string Path)
    {
        public override string ToString() => $"{Method} {Path}";
    }

    [GeneratedRegex(@"var\s+(?<name>\w+)\s*=\s*(?<parent>\w+)\s*\.\s*MapGroup\s*\(\s*""(?<pattern>[^""]*)""")]
    private static partial Regex GroupPattern();

    [GeneratedRegex(@"(?<receiver>\w+)\s*\.\s*Map(?<method>Get|Post|Put|Delete|Patch)\s*\(\s*""(?<pattern>[^""]*)""")]
    private static partial Regex MapPattern();

    [GeneratedRegex(@"\.\s*Map(Get|Post|Put|Delete|Patch)\s*\(")]
    private static partial Regex AnyMapPattern();

    [GeneratedRegex(@"\.\s*MapGroup\s*\(")]
    private static partial Regex AnyGroupPattern();

    private static IReadOnlyList<Route> MappedRoutes() =>
        [.. SourceFiles().SelectMany(file =>
        {
            var source = WithoutComments(File.ReadAllText(file));
            return BodyOf(source, GroupPrefixes(source)).Routes;
        })];

    private static IReadOnlyList<Route> DocumentedRoutes()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(DocumentPath));

        var paths = document.RootElement.GetProperty("paths");

        return
        [
            .. from path in paths.EnumerateObject()
               from operation in path.Value.EnumerateObject()
               where OperationKeys.Contains(operation.Name, StringComparer.OrdinalIgnoreCase)
               select new Route(operation.Name.ToUpperInvariant(), Combine(string.Empty, path.Name))
        ];
    }

    // Iterates to a fixed point, so a group whose parent is declared later still resolves.
    private static Dictionary<string, string> GroupPrefixes(string source)
    {
        var declared = GroupPattern().Matches(source)
            .Select(match => (
                Name: match.Groups["name"].Value,
                Parent: match.Groups["parent"].Value,
                Pattern: match.Groups["pattern"].Value))
            .ToList();

        var prefixes = new Dictionary<string, string>(StringComparer.Ordinal);

        bool progressed;

        do
        {
            progressed = false;

            foreach (var group in declared.Where(group => !prefixes.ContainsKey(group.Name)))
            {
                if (!prefixes.TryGetValue(group.Parent, out var parent))
                {
                    if (declared.Any(other => other.Name == group.Parent))
                    {
                        continue;
                    }

                    parent = string.Empty;
                }

                prefixes[group.Name] = Combine(parent, group.Pattern);
                progressed = true;
            }
        }
        while (progressed && prefixes.Count < declared.Count);

        return prefixes;
    }

    private static string Combine(string prefix, string pattern) =>
        "/" + string.Join(
            '/',
            $"{prefix}/{pattern}"
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(WithoutConstraint));

    private static string WithoutConstraint(string segment)
    {
        if (!segment.StartsWith('{') || !segment.EndsWith('}'))
        {
            return segment;
        }

        var inner = segment[1..^1];
        var end = inner.IndexOfAny([':', '=', '?']);

        return $"{{{(end < 0 ? inner : inner[..end])}}}";
    }

    // Line-leading comments only, so a "//" in a URL survives. CodeOnly would blank the route strings.
    private static string WithoutComments(string source) =>
        string.Join(
            '\n',
            source
                .Split('\n')
                .Where(line =>
                {
                    var trimmed = line.TrimStart();

                    return !trimmed.StartsWith("//", StringComparison.Ordinal)
                        && !trimmed.StartsWith('*')
                        && !trimmed.StartsWith("/*", StringComparison.Ordinal);
                }));

    private sealed record Body(List<Route> Routes, List<string> Calls);

    private static HashSet<Route> RoutesServedBy(string host)
    {
        var bodies = Bodies();
        var served = new HashSet<Route>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>([$"<{host}>"]);

        while (queue.Count > 0)
        {
            var name = queue.Dequeue();

            if (!visited.Add(name) || !bodies.TryGetValue(name, out var body))
            {
                continue;
            }

            served.UnionWith(body.Routes);

            foreach (var call in body.Calls)
            {
                queue.Enqueue(call);
            }
        }

        return served;
    }

    // Cut at each Map* declaration: one file can declare extensions for different hosts.
    private static Dictionary<string, Body> Bodies()
    {
        var bodies = new Dictionary<string, Body>(StringComparer.Ordinal);

        foreach (var file in SourceFiles())
        {
            var source = WithoutComments(File.ReadAllText(file));

            var prefixes = GroupPrefixes(source);

            var declarations = ExtensionDeclarationPattern().Matches(source)
                .Select(match => (Name: match.Groups["name"].Value, Start: match.Index))
                .OrderBy(declaration => declaration.Start)
                .ToList();

            if (declarations.Count == 0)
            {
                if (Path.GetFileName(file) is "Program.cs")
                {
                    bodies[$"<{Path.GetFileName(Path.GetDirectoryName(file))}>"] = BodyOf(source, prefixes);
                }

                continue;
            }

            for (var index = 0; index < declarations.Count; index++)
            {
                var start = declarations[index].Start;
                var end = index + 1 < declarations.Count ? declarations[index + 1].Start : source.Length;

                bodies[declarations[index].Name] = BodyOf(source[start..end], prefixes);
            }
        }

        return bodies;
    }

    private static Body BodyOf(string source, Dictionary<string, string> prefixes)
    {
        var routes = MapPattern().Matches(source)
            .Select(match => new Route(
                match.Groups["method"].Value.ToUpperInvariant(),
                Combine(
                    prefixes.TryGetValue(match.Groups["receiver"].Value, out var known) ? known : string.Empty,
                    match.Groups["pattern"].Value)))
            .ToList();

        var calls = ExtensionCallPattern().Matches(source)
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new Body(routes, calls);
    }

    [GeneratedRegex(@"static\s+IEndpointRouteBuilder\s+(?<name>Map\w+)\s*\(")]
    private static partial Regex ExtensionDeclarationPattern();

    [GeneratedRegex(@"\.\s*(?<name>Map(?!Get\b|Put\b|Post\b|Delete\b|Patch\b|Group\b)[A-Z]\w+)\s*\(")]
    private static partial Regex ExtensionCallPattern();

    private static List<string> SourceFiles() =>
        [.. SourceScan
            .Files("src")
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))];
}
