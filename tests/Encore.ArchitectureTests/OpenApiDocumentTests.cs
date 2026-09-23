using System.Text.Json;
using System.Text.RegularExpressions;

namespace Encore.ArchitectureTests;

/// <summary>
/// The hand-written OpenAPI document against the routes the code actually maps, in both
/// directions, and against which host serves each route.
/// </summary>
/// <remarks>
/// Reads source text rather than booting a host, which would need Postgres, Redis and a
/// forbidden project reference. It understands the shapes the endpoint files are written
/// in, so a sanity test fails if any registration is written in a shape it cannot read.
/// </remarks>
public partial class OpenApiDocumentTests
{
    /// <summary>The document the host serves at <c>/docs/</c>.</summary>
    private static readonly string DocumentPath =
        Path.Combine(EncoreTree.Root, "src", "Encore.Api", "wwwroot", "docs", "openapi.json");

    /// <summary>The OpenAPI operation keys. Everything else under a path is not a route.</summary>
    private static readonly string[] OperationKeys =
        ["get", "put", "post", "delete", "options", "head", "patch", "trace"];

    [Fact]
    public void TheDocumentShouldExist() =>
        Assert.True(
            File.Exists(DocumentPath),
            $"The OpenAPI document is served from wwwroot and maintained by hand; expected it at {DocumentPath}.");

    /// <summary>
    /// Sanity, in both directions: a scan that finds nothing agrees with a
    /// document about nothing, and every comparison below then passes vacuously.
    /// </summary>
    [Fact]
    public void ThereShouldBeRoutesToInspect()
    {
        Assert.NotEmpty(MappedRoutes());
        Assert.NotEmpty(DocumentedRoutes());
    }

    /// <summary>A route the code serves and the document does not mention.</summary>
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

    /// <summary>A route the document promises and the code no longer serves.</summary>
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

    /// <summary>
    /// A status a client branches on is a closed vocabulary, and it is the part of a response
    /// shape that drifted (014 added <c>abandoned</c> unseen). The documented enum must name
    /// exactly the members of the C# enum it is rendered from, snake-cased. Read from source,
    /// like the routes.
    /// </summary>
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

    /// <summary>An enum member with an explicit value, as every status enum is written: <c>TimedOut = 4,</c>.</summary>
    [GeneratedRegex(@"^\s*(?<name>[A-Z]\w*)\s*=\s*\d+\s*,?\s*$", RegexOptions.Multiline)]
    private static partial Regex EnumMemberPattern();

    /// <summary>The spelling the response records use: <c>TimedOut</c> to <c>timed_out</c>.</summary>
    private static string SnakeCase(string name) =>
        string.Concat(name.Select((character, index) =>
            char.IsUpper(character) && index > 0
                ? $"_{char.ToLowerInvariant(character)}"
                : char.ToLowerInvariant(character).ToString()));

    /// <summary>
    /// A path carries its own <c>servers</c> entry exactly when the monolith, which serves
    /// the page, does not map it. Hosts are resolved by walking the call graph from each
    /// <c>Program.cs</c> through the <c>Map*</c> extensions.
    /// </summary>
    [Fact]
    public void EveryDocumentedPathShouldSayWhetherThisHostServesIt()
    {
        var servedByMonolith = RoutesServedBy(MonolithProgram);
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

    /// <summary>
    /// Sanity for the test above: both hosts serve something, and not the same set.
    /// </summary>
    [Fact]
    public void TheTwoHostsShouldServeDifferentRouteSets()
    {
        var monolith = RoutesServedBy(MonolithProgram);
        var payments = RoutesServedBy(PaymentsProgram);

        Assert.NotEmpty(monolith);
        Assert.NotEmpty(payments);

        // SetEquals rather than Assert.NotEqual: a HashSet compares by reference, so
        // NotEqual would pass here whatever the two sets contained.
        Assert.False(monolith.SetEquals(payments), "Both hosts resolved to the same routes.");

        // The three routes that are the whole reason this distinction exists.
        Assert.Contains(new Route("POST", "/internal/payments/authorize"), payments);
        Assert.DoesNotContain(new Route("POST", "/internal/payments/authorize"), monolith);

        // Something both serve, so "different" does not become "disjoint".
        Assert.Contains(new Route("GET", "/health"), monolith);
        Assert.Contains(new Route("GET", "/health"), payments);
    }

    /// <summary>
    /// Every route registration in the tree is one the patterns above can read, so a route
    /// cannot vanish from both sides of the comparison at once.
    /// </summary>
    [Fact]
    public void EveryRouteRegistrationShouldBeOneThisCanRead()
    {
        var unreadable = new List<string>();

        foreach (var file in SourceFiles())
        {
            var source = WithoutComments(File.ReadAllText(file));
            var relative = Path.GetRelativePath(EncoreTree.Root, file);

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

    /// <summary>One verb and one path, with route constraints already stripped.</summary>
    private readonly record struct Route(string Method, string Path)
    {
        public override string ToString() => $"{Method} {Path}";
    }

    /// <summary><c>var name = parent.MapGroup("pattern")</c>, on one line or several.</summary>
    [GeneratedRegex(@"var\s+(?<name>\w+)\s*=\s*(?<parent>\w+)\s*\.\s*MapGroup\s*\(\s*""(?<pattern>[^""]*)""")]
    private static partial Regex GroupPattern();

    /// <summary><c>receiver.MapGet("pattern", ...)</c> and its four siblings.</summary>
    [GeneratedRegex(@"(?<receiver>\w+)\s*\.\s*Map(?<method>Get|Post|Put|Delete|Patch)\s*\(\s*""(?<pattern>[^""]*)""")]
    private static partial Regex MapPattern();

    /// <summary>Any route registration at all, readable or not.</summary>
    [GeneratedRegex(@"\.\s*Map(Get|Post|Put|Delete|Patch)\s*\(")]
    private static partial Regex AnyMapPattern();

    /// <summary>Any route group, readable or not.</summary>
    [GeneratedRegex(@"\.\s*MapGroup\s*\(")]
    private static partial Regex AnyGroupPattern();

    private static IReadOnlyList<Route> MappedRoutes()
    {
        var routes = new List<Route>();

        foreach (var file in SourceFiles())
        {
            var source = WithoutComments(File.ReadAllText(file));
            var prefixes = GroupPrefixes(source);

            foreach (Match match in MapPattern().Matches(source))
            {
                // A receiver that is not a group declared in this file is the root
                // builder — `endpoints` in a module, `app` in Program.cs.
                var prefix = prefixes.TryGetValue(match.Groups["receiver"].Value, out var known)
                    ? known
                    : string.Empty;

                routes.Add(new Route(
                    match.Groups["method"].Value.ToUpperInvariant(),
                    Combine(prefix, match.Groups["pattern"].Value)));
            }
        }

        return routes;
    }

    private static IReadOnlyList<Route> DocumentedRoutes()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(DocumentPath));

        if (!document.RootElement.TryGetProperty("paths", out var paths))
        {
            return [];
        }

        return
        [
            .. from path in paths.EnumerateObject()
               from operation in path.Value.EnumerateObject()
               where OperationKeys.Contains(operation.Name, StringComparer.OrdinalIgnoreCase)
               select new Route(operation.Name.ToUpperInvariant(), Combine(string.Empty, path.Name))
        ];
    }

    /// <summary>
    /// Every route group in one file, resolved to a full prefix. Iterates to a fixed point so a
    /// group whose parent is declared later still resolves.
    /// </summary>
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
                        // A group whose parent is a group not reached yet.
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

    /// <summary>Joins a prefix and a pattern. Empty segments disappear, so "" and "/" both mean the group.</summary>
    private static string Combine(string prefix, string pattern) =>
        "/" + string.Join(
            '/',
            $"{prefix}/{pattern}"
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(WithoutConstraint));

    /// <summary>Strips route constraints: <c>{orderId:guid}</c> becomes <c>{orderId}</c>.</summary>
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

    /// <summary>
    /// Drops whole comment lines, so a <c>MapGet</c> named in a comment is not read as a route.
    /// Line-leading only, so a URL in a string survives.
    /// </summary>
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

    /// <summary>The monolith's composition root.</summary>
    private static string MonolithProgram =>
        Path.Combine(EncoreTree.Root, "src", "Encore.Api", "Program.cs");

    /// <summary>The Payments service's composition root.</summary>
    private static string PaymentsProgram =>
        Path.Combine(EncoreTree.Root, "src", "Encore.Payments.Api", "Program.cs");

    /// <summary>One <c>Map*</c> extension method or a <c>Program.cs</c>: a node in the walk.</summary>
    private sealed record Body(List<Route> Routes, List<string> Calls);

    /// <summary>
    /// Every route reachable from one host's <c>Program.cs</c>, by a breadth-first walk. Calls to
    /// framework <c>Map*</c> methods outside <c>src/</c> are skipped.
    /// </summary>
    private static HashSet<Route> RoutesServedBy(string programPath)
    {
        var bodies = Bodies();
        var served = new HashSet<Route>();
        var visited = new HashSet<string>(StringComparer.Ordinal);

        var root = Path.GetFileName(programPath) is "Program.cs"
            ? $"<{Path.GetFileName(Path.GetDirectoryName(programPath))}>"
            : throw new ArgumentException("Expected a host's Program.cs.", nameof(programPath));

        var queue = new Queue<string>([root]);

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

    /// <summary>
    /// Every route-mapping body in <c>src/</c>, keyed by name. A file is cut at its <c>Map*</c>
    /// declarations, because one file can declare extensions used by different hosts. A
    /// <c>Program.cs</c> is one body keyed by its host directory, in angle brackets.
    /// </summary>
    private static Dictionary<string, Body> Bodies()
    {
        var bodies = new Dictionary<string, Body>(StringComparer.Ordinal);

        foreach (var file in SourceFiles())
        {
            var source = WithoutComments(File.ReadAllText(file));

            // Group prefixes are resolved per file.
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

    /// <summary>What one body registers and what it calls.</summary>
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

    /// <summary><c>public static IEndpointRouteBuilder MapSomething(</c>, however it wraps.</summary>
    [GeneratedRegex(@"static\s+IEndpointRouteBuilder\s+(?<name>Map\w+)\s*\(")]
    private static partial Regex ExtensionDeclarationPattern();

    /// <summary>
    /// A call to one of those, such as <c>app.MapCatalogModule()</c>. The verbs and
    /// <c>MapGroup</c> are excluded: they are routes, not edges.
    /// </summary>
    [GeneratedRegex(@"\.\s*(?<name>Map(?!Get\b|Put\b|Post\b|Delete\b|Patch\b|Group\b)[A-Z]\w+)\s*\(")]
    private static partial Regex ExtensionCallPattern();

    /// <summary>Everything under <c>src/</c>, since a route can be mapped from <c>Program.cs</c> too.</summary>
    private static List<string> SourceFiles() =>
        [.. Directory
            .EnumerateFiles(Path.Combine(EncoreTree.Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))];
}
