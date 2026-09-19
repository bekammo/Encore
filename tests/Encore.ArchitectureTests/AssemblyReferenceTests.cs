using System.Reflection;

namespace Encore.ArchitectureTests;

/// <summary>
/// What the compiled assemblies actually reach, read from their metadata tables.
/// </summary>
/// <remarks>
/// <para>
/// This is the half of the suite that sees transitively: an assembly's reference
/// list names everything the compiler emitted a reference to, whatever route it
/// arrived by. <see cref="ProjectGraphTests"/> is the other half, and neither
/// subsumes the other — see DECISIONS 037.
/// </para>
/// <para>
/// Nothing here has a compile-time dependency on anything it inspects; the
/// assemblies are named as strings and loaded from disk.
/// </para>
/// </remarks>
public class AssemblyReferenceTests
{
    /// <summary>
    /// Runs first in spirit: a typo in a name should read as one clear "not found
    /// at this path" rather than as nine confusing unrelated failures.
    /// </summary>
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
    /// The rule the whole hexagon exists to make true, asserted a step later than
    /// the build guards assert it.
    /// </summary>
    /// <remarks>
    /// ENCORE001/002/003 stop a forbidden dependency getting into the Domain's
    /// compile surface. This reads what the compiler actually emitted afterwards,
    /// which is a different question and the one a reader of CLAUDE.md is really
    /// asking.
    /// </remarks>
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
    /// The sign on the fence that the previous test is.
    /// </summary>
    /// <remarks>
    /// Redundant by construction — anything here would already have failed above —
    /// and kept anyway, because this is the one whose failure message names the
    /// rule in CLAUDE.md rather than leaving the reader to work out why
    /// <c>Npgsql</c> is not on an allow-list. The same belt-and-braces the
    /// aggregate itself uses.
    /// </remarks>
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

    /// <summary>
    /// The three contracts assemblies claim "zero packages, zero refs" in prose.
    /// Until this test, nothing checked any of them.
    /// </summary>
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

    /// <summary>
    /// A module may reach another module through its contracts assembly and by no
    /// other route. This is the rule that makes extracting a module later a matter
    /// of changing one registration.
    /// </summary>
    [Theory]
    [InlineData("Encore.Modules.Catalog")]
    [InlineData("Encore.Modules.Orders")]
    [InlineData("Encore.Modules.Payments")]
    [InlineData("Encore.Modules.Inventory")]
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
    /// The host composes the modules; nothing composes the host.
    /// </summary>
    [Theory]
    [InlineData("Encore.Shared")]
    [InlineData("Encore.Modules.Catalog")]
    [InlineData("Encore.Modules.Catalog.Contracts")]
    [InlineData("Encore.Modules.Orders")]
    [InlineData("Encore.Modules.Payments")]
    [InlineData("Encore.Modules.Payments.Contracts")]
    [InlineData("Encore.Modules.Inventory")]
    [InlineData("Encore.Modules.Inventory.Contracts")]
    [InlineData("Encore.Modules.Inventory.Domain")]
    public void NothingShouldReferenceTheHost(string assembly)
    {
        Assert.DoesNotContain("Encore.Api", EncoreTree.ReferencedNames(assembly));
    }

    /// <summary>
    /// The host owns no business logic and talks to no database. DECISIONS 014 and
    /// 017 both partly rest on this, and <c>Program.cs</c> states it in a comment.
    /// </summary>
    [Fact]
    public void Host_ShouldNameNoPersistenceOrCacheAssembly()
    {
        string[] forbidden = ["Microsoft.EntityFrameworkCore", "Npgsql", "StackExchange.Redis"];

        var leaked = EncoreTree
            .ReferencedNames("Encore.Api")
            .Where(name => forbidden.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        Assert.True(
            leaked.Count == 0,
            $"Encore.Api composes modules and runs the web server; persistence and caching belong behind a module's seam. Found: {string.Join(", ", leaked)}");
    }

    /// <summary>
    /// The namespace-level assertion DECISIONS 002 anticipated.
    /// </summary>
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
            // Report a load problem as a load problem rather than as a namespace
            // violation, then carry on with whatever did load.
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
