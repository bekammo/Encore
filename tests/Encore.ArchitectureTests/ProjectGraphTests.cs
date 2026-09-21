namespace Encore.ArchitectureTests;

/// <summary>
/// What the csprojs <em>declare</em>, as opposed to what the compiler ended up
/// emitting.
/// </summary>
/// <remarks>
/// <para>
/// This suite exists because <see cref="AssemblyReferenceTests"/> cannot see a
/// declared-but-unused <c>ProjectReference</c>: the compiler emits a reference
/// only when a type is actually named, so a project can take a dependency on a
/// module it must not touch and stay invisible to assembly metadata until the day
/// somebody uses it. That latent edge is the one a future contributor will find
/// and treat as permission.
/// </para>
/// <para>
/// Two questions, two mechanisms. DECISIONS 037.
/// </para>
/// </remarks>
public class ProjectGraphTests
{
    [Fact]
    public void InventoryDomain_ShouldDeclareOnlyTheSharedProjectReference()
    {
        var references = Declared("Encore.Modules.Inventory.Domain");

        Assert.Equal(["Encore.Shared"], references);
    }

    [Theory]
    [InlineData("Encore.Modules.Catalog.Contracts")]
    [InlineData("Encore.Modules.Inventory.Contracts")]
    [InlineData("Encore.Modules.Payments.Contracts")]
    public void ContractsProject_ShouldDeclareNoProjectReference(string project)
    {
        var references = Declared(project);

        Assert.True(
            references.Count == 0,
            $"{project} must declare zero ProjectReference items — a consumer depends on it and nothing else. Found: {string.Join(", ", references)}");
    }

    /// <summary>
    /// The declared half of the module-boundary rule. Exact names, never prefixes:
    /// <c>Encore.Modules.Inventory.Contracts</c> starts with
    /// <c>Encore.Modules.Inventory</c>.
    /// </summary>
    [Theory]
    [InlineData("Encore.Modules.Catalog")]
    [InlineData("Encore.Modules.Orders")]
    [InlineData("Encore.Modules.Payments")]
    [InlineData("Encore.Modules.Inventory")]
    public void Module_ShouldDeclareNoProjectReferenceToAnotherModulesImplementation(string module)
    {
        var forbidden = EncoreTree
            .OtherModules(module)
            .Where(other => !(module == "Encore.Modules.Inventory" && other == "Encore.Modules.Inventory.Domain"))
            .ToHashSet(StringComparer.Ordinal);

        var declared = Declared(module).Where(forbidden.Contains).ToList();

        Assert.True(
            declared.Count == 0,
            $"{module} declares a ProjectReference to another module's implementation: {string.Join(", ", declared)}. The seam is that module's .Contracts assembly.");
    }

    /// <summary>
    /// The shared persistence project declares no <c>ProjectReference</c> at all.
    /// DECISIONS 058.
    /// </summary>
    /// <remarks>
    /// The load-bearing half of 058. This project carries EF Core and Npgsql
    /// deliberately, so every edge <i>out</i> of it is a route by which those
    /// arrive somewhere they are forbidden. Zero outbound edges is what makes the
    /// direction of the dependency a fact rather than a habit.
    /// </remarks>
    [Fact]
    public void SharedPersistence_ShouldDeclareNoProjectReference()
    {
        var references = Declared(EncoreTree.SharedPersistence);

        Assert.True(
            references.Count == 0,
            $"{EncoreTree.SharedPersistence} must declare zero ProjectReference items — modules name it, it names nothing. Found: {string.Join(", ", references)}");
    }

    /// <summary>
    /// Nothing zero-dependency may reference the shared persistence project.
    /// DECISIONS 058.
    /// </summary>
    /// <remarks>
    /// The other half of the same rule, asserted from the far end. It matters most
    /// for <c>Encore.Shared</c>: that is <c>Encore.Modules.Inventory.Domain</c>'s
    /// only <c>ProjectReference</c>, so EF Core arriving there arrives on the
    /// Domain's compile surface. ENCORE003 would catch it at build time; this says
    /// which rule was broken and why, rather than leaving a reader to work out what
    /// <c>Npgsql</c> is doing in a closure listing.
    /// </remarks>
    [Theory]
    [InlineData("Encore.Shared")]
    [InlineData("Encore.Modules.Catalog.Contracts")]
    [InlineData("Encore.Modules.Inventory.Contracts")]
    [InlineData("Encore.Modules.Payments.Contracts")]
    [InlineData("Encore.Modules.Inventory.Domain")]
    public void ZeroDependencyProject_ShouldNotReferenceSharedPersistence(string project)
    {
        Assert.DoesNotContain(EncoreTree.SharedPersistence, Declared(project));
    }

    /// <summary>
    /// The set the previous theory covers is the set that actually opts in.
    /// </summary>
    /// <remarks>
    /// A theory over a hand-written list stops being a rule the moment a sixth
    /// project declares <c>EncoreZeroDependency</c> and nobody adds a row. This
    /// reads the property out of the csprojs and fails when the two disagree, in
    /// both directions.
    /// </remarks>
    [Fact]
    public void TheZeroDependencyListShouldMatchTheProjectsThatDeclareIt()
    {
        var declaring = EncoreTree.SourceProjects()
            .Where(project => project.Value
                .Descendants("EncoreZeroDependency")
                .Any(element => string.Equals(element.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase)))
            .Select(project => project.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(EncoreTree.ZeroDependencyProjects.Order(StringComparer.Ordinal), declaring);
    }

    /// <summary>
    /// Nothing under <c>src/</c> may depend on the host.
    /// </summary>
    /// <remarks>
    /// This suite itself declares one, which is why it reads only <c>src/</c>.
    /// That reference carries <c>ReferenceOutputAssembly="false"</c>, so it is a
    /// build-order edge and nothing else: the host's output never reaches this
    /// project's compile surface, and no type here can name one of its. A real
    /// dependency and an ordering edge are different things, and only the first
    /// is what this rule forbids.
    /// </remarks>
    [Theory]
    [InlineData("Encore.Api")]
    [InlineData("Encore.Payments.Api")]
    public void NoProjectShouldDeclareAProjectReferenceToAHost(string host)
    {
        var offenders = EncoreTree.SourceProjects()
            .Where(project => EncoreTree.DeclaredProjectReferences(project.Value).Contains(host))
            .Select(project => project.Key)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"A host composes modules; nothing may depend on one. Found a reference to {host} from: {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// A host composes modules; it does not compose another host. DECISIONS 061.
    /// </summary>
    /// <remarks>
    /// The Payments host and the monolith both serve Payments, and the thing that
    /// makes that a Strangler Fig rather than a mess is that neither knows the other
    /// exists. They meet over HTTP and at no other point.
    /// </remarks>
    [Fact]
    public void NoHostShouldReferenceAnotherHost()
    {
        var offenders = EncoreTree.Hosts
            .SelectMany(host => EncoreTree
                .Hosts
                .Where(other => other != host && Declared(host).Contains(other))
                .Select(other => $"{host} -> {other}"))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"Hosts meet over HTTP, not through the project graph. Found: {string.Join(", ", offenders)}");
    }

    private static IReadOnlyList<string> Declared(string project)
    {
        var projects = EncoreTree.SourceProjects();

        Assert.True(projects.ContainsKey(project), $"No csproj named {project} under src/.");

        return EncoreTree.DeclaredProjectReferences(projects[project]);
    }
}
