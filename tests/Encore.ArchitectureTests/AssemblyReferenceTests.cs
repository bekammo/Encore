using System.Reflection;

namespace Encore.ArchitectureTests;

/// <summary>
/// What the compiled assemblies actually reference, read from their metadata. This half sees
/// transitive reach; <see cref="ProjectGraphTests"/> sees declared references.
/// </summary>
public class AssemblyReferenceTests
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

    /// <summary>
    /// The Domain references nothing outside the BCL and <c>Encore.Shared</c>, checked on the
    /// compiled output after the build rules.
    /// </summary>
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

    /// <summary>
    /// Redundant with the test above, kept for a failure message that names the rule.
    /// </summary>
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

    /// <summary>The contracts assemblies reference nothing outside the BCL.</summary>
    [Theory]
    [InlineData("Encore.Modules.Catalog.Contracts")]
    [InlineData("Encore.Modules.Inventory.Contracts")]
    [InlineData("Encore.Modules.Payments.Contracts")]
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

    /// <summary>A module may reach another module only through its contracts assembly.</summary>
    [Theory]
    [MemberData(nameof(TheoryRows.ComposedModuleAssemblies), MemberType = typeof(TheoryRows))]
    public void Module_ShouldReachOtherModulesOnlyThroughContracts(string module)
    {
        // Inventory is allowed its own domain assembly; every other module
        // implementation, and everyone else's domain, is off limits.
        var forbidden = EncoreTree
            .OtherModules(module)
            .Where(other => !(module == "Encore.Modules.Inventory" && other == "Encore.Modules.Inventory.Domain"))
            .ToHashSet(StringComparer.Ordinal);

        var reached = EncoreTree
            .ReferencedNames(module)
            .Where(forbidden.Contains)
            .ToList();

        Assert.True(
            reached.Count == 0,
            $"{module} may name another module only through its .Contracts assembly. Found: {string.Join(", ", reached)}");
    }

    /// <summary>
    /// The hosts compose the modules; nothing composes a host. Every host, checked from every
    /// assembly that is not one.
    /// </summary>
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

    /// <summary>A host owns no business logic and talks to no database directly.</summary>
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

    /// <summary>
    /// The host list matches the projects that use the Web SDK, so a new host cannot silently
    /// escape the rules above.
    /// </summary>
    [Fact]
    public void TheHostListShouldMatchTheProjectsUsingTheWebSdk()
    {
        var webProjects = EncoreTree.SourceProjects()
            .Where(project => string.Equals(
                (string?)project.Value.Root?.Attribute("Sdk"),
                "Microsoft.NET.Sdk.Web",
                StringComparison.Ordinal))
            .Select(project => project.Key)
            .Order(StringComparer.Ordinal)
            .ToList();

        Assert.Equal(EncoreTree.Hosts.Order(StringComparer.Ordinal), webProjects);
    }

    /// <summary>The shared persistence project may not name a module or a contracts assembly.</summary>
    [Fact]
    public void SharedPersistence_ShouldNameNoModuleOrContractsAssembly()
    {
        var forbidden = EncoreTree.ModuleAssemblies
            .Concat(EncoreTree.ContractsAssemblies)
            .ToHashSet(StringComparer.Ordinal);

        var named = EncoreTree
            .ReferencedNames(EncoreTree.SharedPersistence)
            .Where(forbidden.Contains)
            .ToList();

        Assert.True(
            named.Count == 0,
            $"{EncoreTree.SharedPersistence} knows what a DbContext and a schema are and may not know that a module exists. Found: {string.Join(", ", named)}");
    }

    /// <summary>
    /// The telemetry project may not name a module, a contracts assembly or the shared persistence
    /// project. Module instruments are subscribed by wildcard.
    /// </summary>
    [Fact]
    public void Telemetry_ShouldNameNoModuleContractsOrPersistenceAssembly()
    {
        var forbidden = EncoreTree.ModuleAssemblies
            .Concat(EncoreTree.ContractsAssemblies)
            .Append(EncoreTree.SharedPersistence)
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

    /// <summary>
    /// Nothing but a host emits a reference to the telemetry project or to OpenTelemetry: no
    /// module, contracts assembly, the Domain, <c>Encore.Shared</c> or the shared persistence project.
    /// </summary>
    [Theory]
    [MemberData(nameof(TheoryRows.Composed), MemberType = typeof(TheoryRows))]
    public void Module_ShouldNameNoTelemetryAssembly(string assembly)
    {
        var leaked = EncoreTree
            .ReferencedNames(assembly)
            .Where(name => name == EncoreTree.Telemetry || name.StartsWith("OpenTelemetry", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            leaked.Count == 0,
            $"{assembly} emits through System.Diagnostics; exporters belong to the host. Found: {string.Join(", ", leaked)}");
    }

    /// <summary>No host names the shared persistence project: each module registers its own migrator.</summary>
    [Theory]
    [MemberData(nameof(TheoryRows.Hosts), MemberType = typeof(TheoryRows))]
    public void Host_ShouldNotNameTheSharedPersistenceAssembly(string host) =>
        Assert.DoesNotContain(EncoreTree.SharedPersistence, EncoreTree.ReferencedNames(host));

    /// <summary>Every type in the Domain assembly lives in the Domain namespace.</summary>
    [Fact]
    public void InventoryDomain_ShouldDeclareEveryTypeInItsOwnNamespace()
    {
        var assembly = EncoreTree.Load("Encore.Modules.Inventory.Domain");

        Type?[] types;

        try
        {
            types = assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException exception)
        {
            // Report a load problem as such, then carry on.
            Assert.Fail(
                "Could not load every type in the domain assembly: "
                + string.Join(" | ", exception.LoaderExceptions.Select(e => e?.Message)));
            return;
        }

        var strays = types
            .Where(type => type is not null)
            .Select(type => type!.Namespace)
            .Where(ns => ns is not null && !ns.StartsWith("Encore.Modules.Inventory.Domain", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        Assert.True(
            strays.Count == 0,
            $"Every type in the domain assembly belongs under Encore.Modules.Inventory.Domain. Found: {string.Join(", ", strays)}");
    }
}
