namespace Encore.ArchitectureTests;

/// <summary>Reads csproj XML, which sees a declared but unused <c>ProjectReference</c> the compiler drops (002).</summary>
public sealed class ProjectGraphTests
{
    [Fact]
    public void InventoryDomain_ShouldDeclareOnlyTheSharedProjectReference()
    {
        var references = Declared("Encore.Modules.Inventory.Domain");

        Assert.Equal(["Encore.Shared"], references);
    }

    [Theory]
    [MemberData(nameof(TheoryRows.ContractsAssemblies), MemberType = typeof(TheoryRows))]
    public void ContractsProject_ShouldDeclareNoProjectReference(string project)
    {
        var references = Declared(project);

        Assert.True(
            references.Count == 0,
            $"{project} must declare zero ProjectReference items — a consumer depends on it and nothing else. Found: {string.Join(", ", references)}");
    }

    [Theory]
    [MemberData(nameof(TheoryRows.ComposedModuleAssemblies), MemberType = typeof(TheoryRows))]
    public void Module_ShouldDeclareNoProjectReferenceToAnotherModulesImplementation(string module)
    {
        var forbidden = EncoreTree.OtherModules(module).ToHashSet(StringComparer.Ordinal);

        var declared = Declared(module).Where(forbidden.Contains).ToList();

        Assert.True(
            declared.Count == 0,
            $"{module} declares a ProjectReference to another module's implementation: {string.Join(", ", declared)}. The seam is that module's .Contracts assembly.");
    }

    [Theory]
    [MemberData(nameof(TheoryRows.SharedModuleProjects), MemberType = typeof(TheoryRows))]
    public void SharedModuleProject_ShouldDeclareNoProjectReference(string shared)
    {
        var references = Declared(shared);

        Assert.True(
            references.Count == 0,
            $"{shared} must declare zero ProjectReference items — modules name it, it names nothing. Found: {string.Join(", ", references)}");
    }

    [Theory]
    [MemberData(nameof(TheoryRows.ZeroDependencyProjects), MemberType = typeof(TheoryRows))]
    public void ZeroDependencyProject_ShouldNotReferenceASharedModuleProject(string project)
    {
        var declared = Declared(project);

        foreach (var shared in EncoreTree.SharedModuleProjects)
        {
            Assert.DoesNotContain(shared, declared);
        }
    }

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

    /// <summary>Scans <c>src/</c> only: this suite's own host references are build-order edges.</summary>
    [Theory]
    [MemberData(nameof(TheoryRows.Hosts), MemberType = typeof(TheoryRows))]
    public void NoProjectShouldDeclareAProjectReferenceToAHost(string host)
    {
        var offenders = EncoreTree.SourceProjects()
            .Where(project => EncoreTree
                .DeclaredProjectReferences(project.Value)
                .Contains(host, StringComparer.Ordinal))
            .Select(project => project.Key)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"A host composes modules; nothing may depend on one. Found a reference to {host} from: {string.Join(", ", offenders)}");
    }

    /// <summary>Redundant with the test above, kept for a failure message that names the rule.</summary>
    [Fact]
    public void NoHostShouldReferenceAnotherHost()
    {
        var offenders = EncoreTree.Hosts
            .SelectMany(host => EncoreTree
                .Hosts
                .Where(other => other != host && Declared(host).Contains(other, StringComparer.Ordinal))
                .Select(other => $"{host} -> {other}"))
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"Hosts meet over HTTP, not through the project graph. Found: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Telemetry_ShouldDeclareNoProjectReference()
    {
        var references = Declared(EncoreTree.Telemetry);

        Assert.True(
            references.Count == 0,
            $"{EncoreTree.Telemetry} must declare zero ProjectReference items — hosts name it, it names nothing. Found: {string.Join(", ", references)}");
    }

    [Fact]
    public void OnlyAHostShouldReferenceTelemetry()
    {
        var offenders = EncoreTree.SourceProjects()
            .Where(project => !EncoreTree.Hosts.Contains(project.Key, StringComparer.Ordinal))
            .Where(project => EncoreTree
                .DeclaredProjectReferences(project.Value)
                .Contains(EncoreTree.Telemetry, StringComparer.Ordinal))
            .Select(project => project.Key)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"{EncoreTree.Telemetry} is host wiring; a module that references it carries OpenTelemetry. Found: {string.Join(", ", offenders)}");
    }

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
