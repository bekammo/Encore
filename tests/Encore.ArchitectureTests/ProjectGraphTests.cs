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
    [Fact]
    public void NoProjectShouldDeclareAProjectReferenceToTheHost()
    {
        var offenders = EncoreTree.SourceProjects()
            .Where(project => EncoreTree.DeclaredProjectReferences(project.Value).Contains("Encore.Api"))
            .Select(project => project.Key)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"The host composes the modules; nothing may depend on it. Found: {string.Join(", ", offenders)}");
    }

    private static IReadOnlyList<string> Declared(string project)
    {
        var projects = EncoreTree.SourceProjects();

        Assert.True(projects.ContainsKey(project), $"No csproj named {project} under src/.");

        return EncoreTree.DeclaredProjectReferences(projects[project]);
    }
}
