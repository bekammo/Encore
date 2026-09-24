namespace Encore.ArchitectureTests;

/// <summary>
/// What the csprojs declare, as opposed to what the compiler emitted. Catches a declared but
/// unused <c>ProjectReference</c>, which assembly metadata cannot see.
/// </summary>
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
    [MemberData(nameof(TheoryRows.ComposedModuleAssemblies), MemberType = typeof(TheoryRows))]
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
    /// The shared persistence project declares no <c>ProjectReference</c>, so its EF Core has no
    /// route out.
    /// </summary>
    [Fact]
    public void SharedPersistence_ShouldDeclareNoProjectReference()
    {
        var references = Declared(EncoreTree.SharedPersistence);

        Assert.True(
            references.Count == 0,
            $"{EncoreTree.SharedPersistence} must declare zero ProjectReference items — modules name it, it names nothing. Found: {string.Join(", ", references)}");
    }

    /// <summary>
    /// Nothing zero-dependency may reference the shared persistence project, least of all
    /// <c>Encore.Shared</c>, the Domain's only reference.
    /// </summary>
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

    /// <summary>The list above matches the projects that actually declare <c>EncoreZeroDependency</c>.</summary>
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
    /// Nothing under <c>src/</c> may depend on the host. This suite's own reference to it is a
    /// build-order edge only (<c>ReferenceOutputAssembly="false"</c>).
    /// </summary>
    [Theory]
    [MemberData(nameof(TheoryRows.Hosts), MemberType = typeof(TheoryRows))]
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

    /// <summary>A host may not reference another host; they meet only over HTTP.</summary>
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

    /// <summary>The telemetry project declares no <c>ProjectReference</c>: it wires exporters and names nothing.</summary>
    [Fact]
    public void Telemetry_ShouldDeclareNoProjectReference()
    {
        var references = Declared(EncoreTree.Telemetry);

        Assert.True(
            references.Count == 0,
            $"{EncoreTree.Telemetry} must declare zero ProjectReference items — hosts name it, it names nothing. Found: {string.Join(", ", references)}");
    }

    /// <summary>
    /// Only a host may reference the telemetry project. Modules emit through the BCL's
    /// <c>ActivitySource</c> and <c>Meter</c>, so OpenTelemetry's packages never reach one.
    /// </summary>
    [Fact]
    public void OnlyAHostShouldReferenceTelemetry()
    {
        var offenders = EncoreTree.SourceProjects()
            .Where(project => !EncoreTree.Hosts.Contains(project.Key))
            .Where(project => EncoreTree.DeclaredProjectReferences(project.Value).Contains(EncoreTree.Telemetry))
            .Select(project => project.Key)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"{EncoreTree.Telemetry} is host wiring; a module that references it carries OpenTelemetry. Found: {string.Join(", ", offenders)}");
    }

    /// <summary>
    /// The declared half of the host seam: a host references module implementations, whose
    /// <c>{Module}Module</c> class is the seam, plus <c>Encore.Shared</c> and the telemetry
    /// project. Never a contracts assembly, the Domain or the shared persistence project.
    /// </summary>
    [Theory]
    [MemberData(nameof(TheoryRows.Hosts), MemberType = typeof(TheoryRows))]
    public void Host_ShouldDeclareProjectReferencesOnlyToModulesSharedAndTelemetry(string host)
    {
        var allowed = EncoreTree.ComposedModules
            .Select(module => $"Encore.Modules.{module}")
            .Append("Encore.Shared")
            .Append(EncoreTree.Telemetry)
            .ToHashSet(StringComparer.Ordinal);

        var stray = Declared(host).Where(reference => !allowed.Contains(reference)).ToList();

        Assert.True(
            stray.Count == 0,
            $"{host} reaches a module only through its Add/Map seam, so it references module implementations, Encore.Shared and {EncoreTree.Telemetry} and nothing else. Found: {string.Join(", ", stray)}");
    }

    private static IReadOnlyList<string> Declared(string project)
    {
        var projects = EncoreTree.SourceProjects();

        Assert.True(projects.ContainsKey(project), $"No csproj named {project} under src/.");

        return EncoreTree.DeclaredProjectReferences(projects[project]);
    }
}
