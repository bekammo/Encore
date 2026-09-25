using System.Reflection;

namespace Encore.ArchitectureTests;

/// <summary>Reads compiled metadata, which sees transitive reach a csproj does not declare (002).</summary>
public sealed class AssemblyReferenceTests
{
    /// <summary>A typo in a name reads as one clear "not found" rather than many failures.</summary>
    [Fact]
    public void EveryInspectedAssemblyShouldBePresent()
    {
        var missing = EncoreTree.AllAssemblies
            .Where(name => !File.Exists(EncoreTree.AssemblyPath(name)))
            .Select(name => $"{name} (looked in {EncoreTree.AssemblyPath(name)})")
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"Expected compiled output for every project under src/, but could not find: {string.Join("; ", missing)}");
    }

    /// <summary>A backstop to the ENCORE00x build rules (002).</summary>
    [Fact]
    public void InventoryDomain_ShouldReferenceNothingButEncoreSharedAndTheBcl()
    {
        var foreign = EncoreTree
            .ReferencedNames("Encore.Modules.Inventory.Domain")
            .Where(name => name != "Encore.Shared" && !EncoreTree.IsBcl(name))
            .ToList();

        Assert.True(
            foreign.Count == 0,
            $"Encore.Modules.Inventory.Domain may reference Encore.Shared and the BCL only. Found: {string.Join(", ", foreign)}");
    }

    /// <summary>Redundant with the test above, kept for a failure message that names the rule.</summary>
    [Fact]
    public void InventoryDomain_ShouldReferenceNoInfrastructureAssembly()
    {
        string[] forbidden =
            ["Microsoft.EntityFrameworkCore", "Npgsql", "StackExchange.Redis", "Microsoft.AspNetCore"];

        var leaked = EncoreTree
            .ReferencedNames("Encore.Modules.Inventory.Domain")
            .Where(name => forbidden.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        Assert.True(
            leaked.Count == 0,
            $"The domain must reference zero infrastructure — EF Core, Npgsql, Redis and ASP.NET Core live on the far side of the ports. Found: {string.Join(", ", leaked)}");
    }

    [Theory]
    [MemberData(nameof(TheoryRows.ContractsAssemblies), MemberType = typeof(TheoryRows))]
    public void ContractsAssembly_ShouldReferenceNothingButTheBcl(string assembly)
    {
        var foreign = EncoreTree
            .ReferencedNames(assembly)
            .Where(name => !EncoreTree.IsBcl(name))
            .ToList();

        Assert.True(
            foreign.Count == 0,
            $"{assembly} is a public face: a consumer takes a dependency on it and nothing else, so it may reference only the BCL. Found: {string.Join(", ", foreign)}");
    }

    [Theory]
    [MemberData(nameof(TheoryRows.ComposedModuleAssemblies), MemberType = typeof(TheoryRows))]
    public void Module_ShouldReachOtherModulesOnlyThroughContracts(string module)
    {
        var forbidden = EncoreTree.OtherModules(module).ToHashSet(StringComparer.Ordinal);

        var reached = EncoreTree
            .ReferencedNames(module)
            .Where(forbidden.Contains)
            .ToList();

        Assert.True(
            reached.Count == 0,
            $"{module} may name another module only through its .Contracts assembly. Found: {string.Join(", ", reached)}");
    }

    [Theory]
    [MemberData(nameof(TheoryRows.NonHosts), MemberType = typeof(TheoryRows))]
    public void NothingShouldReferenceAHost(string assembly)
    {
        var named = EncoreTree
            .ReferencedNames(assembly)
            .Where(name => EncoreTree.Hosts.Contains(name, StringComparer.Ordinal))
            .ToList();

        Assert.True(
            named.Count == 0,
            $"{assembly} references a host; a host composes modules and nothing may depend on one. Found: {string.Join(", ", named)}");
    }

    [Theory]
    [MemberData(nameof(TheoryRows.Hosts), MemberType = typeof(TheoryRows))]
    public void Host_ShouldNameNoPersistenceOrCacheAssembly(string host)
    {
        string[] forbidden = ["Microsoft.EntityFrameworkCore", "Npgsql", "StackExchange.Redis"];

        var leaked = EncoreTree
            .ReferencedNames(host)
            .Where(name => forbidden.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        Assert.True(
            leaked.Count == 0,
            $"{host} composes modules and runs the web server; persistence and caching belong behind a module's seam. Found: {string.Join(", ", leaked)}");
    }

    [Fact]
    public void TheHostListShouldMatchTheProjectsUsingTheWebSdk()
    {
        var webProjects = EncoreTree.SourceProjects()
            .Where(project => (string?)project.Value.Root?.Attribute("Sdk") == "Microsoft.NET.Sdk.Web")
            .Select(project => project.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(EncoreTree.Hosts.Order(StringComparer.Ordinal), webProjects);
    }

    [Theory]
    [MemberData(nameof(TheoryRows.SharedModuleProjects), MemberType = typeof(TheoryRows))]
    public void SharedModuleProject_ShouldNameNoModuleOrContractsAssembly(string shared)
    {
        var forbidden = EncoreTree.ModuleAssemblies
            .Concat(EncoreTree.ContractsAssemblies)
            .ToHashSet(StringComparer.Ordinal);

        var named = EncoreTree
            .ReferencedNames(shared)
            .Where(forbidden.Contains)
            .ToList();

        Assert.True(
            named.Count == 0,
            $"{shared} is shared by every module and may not know that a module exists. Found: {string.Join(", ", named)}");
    }

    /// <summary>
    /// Instruments are subscribed by wildcard, so the telemetry project needs no module, nor
    /// <c>Encore.Shared</c> (021).
    /// </summary>
    [Fact]
    public void Telemetry_ShouldNameNoModuleContractsOrPersistenceAssembly()
    {
        var forbidden = EncoreTree.ModuleAssemblies
            .Concat(EncoreTree.ContractsAssemblies)
            .Concat(EncoreTree.SharedModuleProjects)
            .Append("Encore.Shared")
            .ToHashSet(StringComparer.Ordinal);

        var named = EncoreTree
            .ReferencedNames(EncoreTree.Telemetry)
            .Where(forbidden.Contains)
            .ToList();

        Assert.True(
            named.Count == 0,
            $"{EncoreTree.Telemetry} knows what an exporter is and may not know that a module exists. Found: {string.Join(", ", named)}");
    }

    [Theory]
    [MemberData(nameof(TheoryRows.Composed), MemberType = typeof(TheoryRows))]
    public void ComposedAssembly_ShouldNameNoTelemetryAssembly(string assembly)
    {
        var leaked = EncoreTree
            .ReferencedNames(assembly)
            .Where(name => name == EncoreTree.Telemetry || name.StartsWith("OpenTelemetry", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            leaked.Count == 0,
            $"{assembly} emits through System.Diagnostics; exporters belong to the host. Found: {string.Join(", ", leaked)}");
    }

    /// <summary>Each module registers its own migrator and filters (017, 029).</summary>
    [Theory]
    [MemberData(nameof(TheoryRows.Hosts), MemberType = typeof(TheoryRows))]
    public void Host_ShouldNotNameASharedModuleProject(string host)
    {
        var referenced = EncoreTree.ReferencedNames(host);

        foreach (var shared in EncoreTree.SharedModuleProjects)
        {
            Assert.DoesNotContain(shared, referenced);
        }
    }

    [Fact]
    public void InventoryDomain_ShouldDeclareEveryTypeInItsOwnNamespace()
    {
        var assembly = EncoreTree.Load("Encore.Modules.Inventory.Domain");

        Type[] types;

        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            Assert.Fail(
                "Could not load every type in the domain assembly: "
                + string.Join(" | ", ex.LoaderExceptions.Select(loaderException => loaderException?.Message)));
            return;
        }

        var strays = types
            .Select(type => type.Namespace)
            .Where(ns => ns is not null && !ns.StartsWith("Encore.Modules.Inventory.Domain", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            strays.Count == 0,
            $"Every type in the domain assembly belongs under Encore.Modules.Inventory.Domain. Found: {string.Join(", ", strays)}");
    }
}
