using System.Text.Json;
using System.Text.RegularExpressions;

namespace Encore.ArchitectureTests;

/// <summary>
/// The hand-written OpenAPI document against the routes the code actually maps.
/// </summary>
/// <remarks>
/// <para>
/// DECISIONS 014 refused Swashbuckle and <c>Microsoft.AspNetCore.OpenApi</c> so
/// that <c>Encore.Api</c> keeps its zero-package property, and 049 recorded the
/// bill honestly: the document is maintained by hand, so a route could change and
/// nothing would fail. This is what fails. The refusal stands; what it used to
/// cost does not. DECISIONS 059.
/// </para>
/// <para>
/// <b>It reads source text rather than a running application.</b> Enumerating a
/// booted host's endpoints would need a reference from this project to
/// <c>Encore.Api</c> — the edge <see cref="ProjectGraphTests"/> forbids — and a
/// Postgres and a Redis to boot against, which turns a convention check into an
/// integration test that cannot run without Docker.
/// <see cref="MigrationConventionTests"/> reads source for the same reason.
/// </para>
/// <para>
/// <b>What that costs, stated rather than hidden.</b> This understands the shape
/// the endpoint files are written in, not C# in general: a route registered
/// through a receiver it cannot follow, or built from a constant rather than a
/// literal, is invisible to it and would pass unnoticed. The two sanity tests are
/// what keep that from decaying into a suite that passes by finding nothing, and
/// the reverse direction — every documented route must be mapped — is what
/// catches the case where the scan goes blind and a route quietly disappears.
/// </para>
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
            $"These routes are mapped but absent from src/Encore.Api/wwwroot/docs/openapi.json, which is maintained by hand (DECISIONS 049, 059): {string.Join("; ", missing)}");
    }

    /// <summary>A route the document promises and the code no longer serves.</summary>
    /// <remarks>
    /// The more embarrassing direction of the two. The first costs a caller a
    /// feature they cannot find; this one sends them at a 404 the page told them
    /// was there.
    /// </remarks>
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
    /// Which host serves a route, and whether the document says so.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One document, two hosts, and until <c>DECISIONS.md</c> 071 no way to tell
    /// which.</b> The monolith serves this page at <c>/docs/</c> and does not map
    /// <c>/internal/payments/*</c> — only <c>Encore.Payments.Api</c> calls
    /// <c>MapPaymentsServiceApi</c> — so a reader pressing "Try it out" on those
    /// three got a 404 from the host that had just advertised them.
    /// </para>
    /// <para>
    /// <b>The rule this checks is narrow on purpose.</b> A path carries its own
    /// <c>servers</c> entry exactly when the monolith does not serve it. It does not
    /// say which other hosts do — <c>/health</c> and the two <c>/payments</c> read
    /// routes are served by both and are documented plainly — because the question
    /// this page's reader has is "will the thing serving this document answer me",
    /// and a fuller answer would mean a <c>servers</c> array on all nineteen paths to
    /// state something eighteen of them do not need.
    /// </para>
    /// <para>
    /// <b>Attribution walks the call graph from each host's <c>Program.cs</c></b>,
    /// through the <c>Map*</c> extensions, to the files that declare routes — at
    /// method granularity rather than file, because <c>PaymentsModule</c> declares
    /// both <c>MapPaymentsModule</c> and <c>MapPaymentsServiceApi</c> and the whole
    /// point is that a host may call one without the other.
    /// </para>
    /// </remarks>
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
            $"The document and the hosts disagree about who serves what (DECISIONS 071): {string.Join("; ", wrong)}");
    }

    /// <summary>
    /// Sanity for the test above: both hosts serve something, and the two sets are
    /// not the same set.
    /// </summary>
    /// <remarks>
    /// Without this, a walker that resolved nothing would attribute every route to
    /// neither host, and the check above would then demand a <c>servers</c> entry on
    /// all of them — loudly. A walker that over-resolved is the quiet failure, and
    /// this is what catches it: if the two hosts served identical route sets, the
    /// distinction the entry exists to draw would have stopped existing.
    /// </remarks>
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

        // And something both of them serve, so "different" does not quietly become
        // "disjoint" — the two hosts share the health routes and the payment reads.
        Assert.Contains(new Route("GET", "/health"), monolith);
        Assert.Contains(new Route("GET", "/health"), payments);
    }

    /// <summary>
    /// The guard on the scan itself: every route registration in the tree is one
    /// of the forms the two patterns above can read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what stops the honest limitation in the class comment from becoming
    /// a silent one. A pattern built from a constant rather than a literal, a
    /// group chained straight onto a map call with no variable in between, a verb
    /// this does not list — each is invisible to the scan, and each would make a
    /// route vanish from both sides of the comparison at once, which no amount of
    /// comparing finds.
    /// </para>
    /// <para>
    /// So count the registrations loosely and the readable ones strictly, and
    /// require the two to agree. A route written in a new shape does not go
    /// unnoticed; it fails here, naming the file, and the choice is then to write
    /// it in a shape this reads or to teach this the shape.
    /// </para>
    /// </remarks>
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
            $"A route is registered in a shape this suite cannot read, so it is missing from both sides of the comparison rather than failing one of them (DECISIONS 059). Found: {string.Join("; ", unreadable)}");
    }

    /// <summary>One verb and one path, with route constraints already stripped.</summary>
    private readonly record struct Route(string Method, string Path)
    {
        public override string ToString() => $"{Method} {Path}";
    }

    /// <summary>
    /// <c>var name = parent.MapGroup("pattern")</c>, on one line or several.
    /// </summary>
    /// <remarks>
    /// <c>\s*</c> spans newlines, which is what lets this see
    /// <c>PaymentEndpoints</c>' form — the receiver on one line and the call on
    /// the next.
    /// </remarks>
    [GeneratedRegex(@"var\s+(?<name>\w+)\s*=\s*(?<parent>\w+)\s*\.\s*MapGroup\s*\(\s*""(?<pattern>[^""]*)""")]
    private static partial Regex GroupPattern();

    /// <summary>
    /// <c>receiver.MapGet("pattern", ...)</c> and its four siblings.
    /// </summary>
    /// <remarks>
    /// <c>MapGroup</c> cannot match this: after <c>Map</c> the alternation wants
    /// <c>Get</c> and finds <c>Gro</c>. Nor can <c>MapCatalogModule()</c>, which
    /// has no string literal where one is required.
    /// </remarks>
    [GeneratedRegex(@"(?<receiver>\w+)\s*\.\s*Map(?<method>Get|Post|Put|Delete|Patch)\s*\(\s*""(?<pattern>[^""]*)""")]
    private static partial Regex MapPattern();

    /// <summary>
    /// Any route registration at all, readable or not. Counted against
    /// <see cref="MapPattern"/> by
    /// <see cref="EveryRouteRegistrationShouldBeOneThisCanRead"/>.
    /// </summary>
    [GeneratedRegex(@"\.\s*Map(Get|Post|Put|Delete|Patch)\s*\(")]
    private static partial Regex AnyMapPattern();

    /// <summary>Any route group, readable or not. The twin of <see cref="AnyMapPattern"/>.</summary>
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
    /// Every route group in one file, resolved to a full prefix.
    /// </summary>
    /// <remarks>
    /// A fixed point rather than a single pass, so a group whose parent is
    /// declared further down the file still resolves. A cycle cannot spin the
    /// loop: it stops the first time a pass resolves nothing.
    /// </remarks>
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

    /// <summary>
    /// Joins a prefix and a pattern into the path OpenAPI would name.
    /// </summary>
    /// <remarks>
    /// Empty segments disappear, which quietly settles the two forms the endpoint
    /// files use for "the group itself": Orders maps <c>""</c> and Payments maps
    /// <c>"/"</c>, and both mean the group's own path.
    /// </remarks>
    private static string Combine(string prefix, string pattern) =>
        "/" + string.Join(
            '/',
            $"{prefix}/{pattern}"
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(WithoutConstraint));

    /// <summary>
    /// <c>{orderId:guid}</c> is what ASP.NET Core routes on; <c>{orderId}</c> is
    /// what OpenAPI names. A constraint, a default and an optional marker each end
    /// the parameter name.
    /// </summary>
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
    /// Drops whole comment lines, so a <c>MapGet</c> named in an XML doc is not
    /// read as a route.
    /// </summary>
    /// <remarks>
    /// Line-leading only, which leaves a string literal containing <c>//</c> — a
    /// URL — intact where cutting at the first <c>//</c> would not.
    /// </remarks>
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

    /// <summary>
    /// One <c>Map*</c> extension method, or a <c>Program.cs</c> — the unit the walk
    /// below moves through.
    /// </summary>
    /// <param name="Routes">Routes registered directly in this body.</param>
    /// <param name="Calls">Other <c>Map*</c> extensions this body calls.</param>
    private sealed record Body(List<Route> Routes, List<string> Calls);

    /// <summary>
    /// Every route reachable from one host's <c>Program.cs</c>.
    /// </summary>
    /// <remarks>
    /// A breadth-first walk over <see cref="Bodies"/>, which is as much call-graph
    /// resolution as this needs: the edges here are all "an extension method calls
    /// another extension method by name", with no indirection and no generics. An
    /// extension that is called and cannot be found is skipped rather than failing —
    /// <c>MapGroup</c>, <c>MapStaticAssets</c> and anything else the framework
    /// provides live outside <c>src/</c>, and routes only ever come from bodies this
    /// tree declares.
    /// </remarks>
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
    /// Every route-mapping body in <c>src/</c>, keyed by the name a caller uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A file is cut into bodies at its <c>Map*</c> extension declarations, and each
    /// body runs to the next declaration. That is coarser than parsing C# and finer
    /// than taking the file whole, which is the granularity the question needs:
    /// <c>PaymentsModule</c> declares <c>MapPaymentsModule</c> and
    /// <c>MapPaymentsServiceApi</c> in one file, and attributing both to any host
    /// that calls either would erase the very distinction being checked.
    /// </para>
    /// <para>
    /// A <c>Program.cs</c> has no declaration to cut at, so the whole file is one
    /// body keyed by its host directory — <c>&lt;Encore.Api&gt;</c>. The angle
    /// brackets cannot collide with a method name.
    /// </para>
    /// </remarks>
    private static Dictionary<string, Body> Bodies()
    {
        var bodies = new Dictionary<string, Body>(StringComparer.Ordinal);

        foreach (var file in SourceFiles())
        {
            var source = WithoutComments(File.ReadAllText(file));

            // Group prefixes are resolved per file, as everywhere else here: a group
            // is declared and used inside one method in this codebase, and reading
            // them file-wide costs nothing and survives the day one moves.
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

    /// <summary>
    /// <c>public static IEndpointRouteBuilder MapSomething(</c>, however it wraps.
    /// </summary>
    [GeneratedRegex(@"static\s+IEndpointRouteBuilder\s+(?<name>Map\w+)\s*\(")]
    private static partial Regex ExtensionDeclarationPattern();

    /// <summary>
    /// A call to one of those, such as <c>app.MapCatalogModule()</c> or
    /// <c>endpoints.MapSeatEndpoints()</c>.
    /// </summary>
    /// <remarks>
    /// The verbs and <c>MapGroup</c> are excluded by name: they are route
    /// registrations rather than edges, and <see cref="MapPattern"/> already has
    /// them.
    /// </remarks>
    [GeneratedRegex(@"\.\s*(?<name>Map(?!Get\b|Put\b|Post\b|Delete\b|Patch\b|Group\b)[A-Z]\w+)\s*\(")]
    private static partial Regex ExtensionCallPattern();

    /// <summary>
    /// Everything under <c>src/</c> rather than the <c>*Endpoints.cs</c> files
    /// alone — the host maps <c>/health</c> straight from <c>Program.cs</c>, and a
    /// route mapped somewhere nobody expected is exactly the one worth catching.
    /// </summary>
    private static List<string> SourceFiles() =>
        [.. Directory
            .EnumerateFiles(Path.Combine(EncoreTree.Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))];
}
