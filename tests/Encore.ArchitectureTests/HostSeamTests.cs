using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace Encore.ArchitectureTests;

/// <summary>
/// The seam is checked in source and in the compiled host, which also sees <c>var</c>,
/// <c>typeof</c> and generic arguments.
/// </summary>
public sealed partial class HostSeamTests
{
    // Opt-ins beyond Add/Map: the service API Encore.Payments.Api mounts for Orders (008, 018).
    private static readonly string[] ExtraSeams = ["MapPaymentsServiceApi"];

    /// <summary>Outside a root-namespace <c>using</c>, any <c>Encore.Modules</c> fails, in code or in a string.</summary>
    [Theory]
    [MemberData(nameof(TheoryRows.Hosts), MemberType = typeof(TheoryRows))]
    public void Host_ShouldImportOnlyTheModulesRootNamespaces(string host)
    {
        var roots = EncoreTree.ComposedModules
            .Select(module => $"Encore.Modules.{module}")
            .ToHashSet(StringComparer.Ordinal);

        var found = new List<string>();

        foreach (var file in HostFiles(host))
        {
            foreach (var (number, text) in SourceScan.Lines(File.ReadAllText(file)))
            {
                if (text.TrimStart().StartsWith("//", StringComparison.Ordinal)
                    || !text.Contains("Encore.Modules", StringComparison.Ordinal))
                {
                    continue;
                }

                var directive = UsingDirectivePattern().Match(text);

                if (!directive.Success || !roots.Contains(directive.Groups["namespace"].Value))
                {
                    found.Add($"{SourceScan.Relative(file)}:{number}: {text.Trim()}");
                }
            }
        }

        // A <Using> item in the csproj is a global using the source never shows.
        found.AddRange(EncoreTree.SourceProjects()[host]
            .Descendants("Using")
            .Select(item => (string?)item.Attribute("Include") ?? string.Empty)
            .Where(include => include.StartsWith("Encore.Modules", StringComparison.Ordinal))
            .Select(include => $"{host}.csproj: <Using Include=\"{include}\" />"));

        Assert.True(
            found.Count == 0,
            $"{host} may import a module's root namespace, where its seam class lives, and nothing else from Encore.Modules. Found: {string.Join("; ", found)}");
    }

    [Theory]
    [MemberData(nameof(TheoryRows.Hosts), MemberType = typeof(TheoryRows))]
    public void Host_ShouldNameNothingFromAModuleButTheSeam(string host)
    {
        var moduleFiles = SourceScan.ModuleFiles();
        var types = SourceScan.DeclaredTypes(moduleFiles);
        var methods = SourceScan.DeclaredExtensionMethods(moduleFiles);
        var seam = SeamTypes().Concat(SeamMethods()).ToHashSet(StringComparer.Ordinal);

        var found = new List<string>();

        foreach (var file in HostFiles(host))
        {
            foreach (var (number, text) in SourceScan.Lines(SourceScan.CodeOnly(File.ReadAllText(file))))
            {
                found.AddRange(SourceScan
                    .Identifiers(text)
                    .Where(name => (types.Contains(name) || methods.Contains(name)) && !seam.Contains(name))
                    .Distinct(StringComparer.Ordinal)
                    .Select(name => $"{SourceScan.Relative(file)}:{number} names {name}"));
            }
        }

        Assert.True(
            found.Count == 0,
            $"{host} knows a module only through its Add/Map seam; this names something the module declares behind it. Found: {string.Join("; ", found)}");
    }

    [Theory]
    [MemberData(nameof(TheoryRows.Hosts), MemberType = typeof(TheoryRows))]
    public void CompiledHost_ShouldReferenceNothingFromAModuleButTheSeam(string host)
    {
        var guarded = EncoreTree.ModuleAssemblies
            .Concat(EncoreTree.ContractsAssemblies)
            .Concat(EncoreTree.SharedModuleProjects)
            .ToHashSet(StringComparer.Ordinal);

        var seamTypes = EncoreTree.ComposedModules
            .Select(module => $"Encore.Modules.{module}.{module}Module")
            .ToHashSet(StringComparer.Ordinal);

        var seamMethods = SeamMethods();
        var found = new List<string>();

        using var stream = File.OpenRead(EncoreTree.AssemblyPath(host));
        using var image = new PEReader(stream);
        var reader = image.GetMetadataReader();

        foreach (var handle in reader.TypeReferences)
        {
            var assembly = AssemblyOf(reader, handle);

            if (assembly is not null && guarded.Contains(assembly) && !seamTypes.Contains(FullName(reader, handle)))
            {
                found.Add($"type {FullName(reader, handle)} from {assembly}");
            }
        }

        foreach (var handle in reader.MemberReferences)
        {
            var member = reader.GetMemberReference(handle);

            if (member.Parent.Kind != HandleKind.TypeReference)
            {
                continue;
            }

            var parent = (TypeReferenceHandle)member.Parent;
            var assembly = AssemblyOf(reader, parent);
            var name = reader.GetString(member.Name);

            if (assembly is not null && guarded.Contains(assembly) && !seamMethods.Contains(name))
            {
                found.Add($"member {FullName(reader, parent)}.{name} from {assembly}");
            }
        }

        Assert.True(
            found.Count == 0,
            $"{host} was compiled against something a module keeps behind its seam. Found: {string.Join("; ", found)}");
    }

    [Fact]
    public void TheSeamAndWhatItHidesShouldBeFound()
    {
        var moduleFiles = SourceScan.ModuleFiles();
        var types = SourceScan.DeclaredTypes(moduleFiles);
        var methods = SourceScan.DeclaredExtensionMethods(moduleFiles);

        Assert.Contains("EfSeatRepository", types);
        Assert.Contains("HoldSeatCommandHandler", types);
        Assert.Contains("CheckoutService", types);

        foreach (var seam in SeamTypes())
        {
            Assert.Contains(seam, types);
        }

        foreach (var seam in EncoreTree.ComposedModules.Select(module => $"Add{module}Module").Concat(ExtraSeams))
        {
            Assert.Contains(seam, methods);
        }
    }

    [Theory]
    [MemberData(nameof(TheoryRows.Hosts), MemberType = typeof(TheoryRows))]
    public void Host_ShouldComposeAtLeastOneModule(string host)
    {
        var adds = EncoreTree.ComposedModules.Select(module => $"Add{module}Module").ToHashSet(StringComparer.Ordinal);

        var called = HostFiles(host)
            .SelectMany(file => SourceScan.Lines(SourceScan.CodeOnly(File.ReadAllText(file))))
            .SelectMany(line => SourceScan.Identifiers(line.Text))
            .Where(adds.Contains)
            .ToList();

        Assert.NotEmpty(called);
    }

    /// <summary>
    /// A module attaches rate-limit policies to its routes; a host without the middleware skips
    /// every one of them silently (030).
    /// </summary>
    [Theory]
    [MemberData(nameof(TheoryRows.Hosts), MemberType = typeof(TheoryRows))]
    public void Host_MappingARateLimitedModule_ShouldUseTheRateLimiter(string host)
    {
        var limited = SourceScan.ModuleFiles()
            .Where(file => SourceScan.CodeOnly(File.ReadAllText(file)).Contains(".RequireRateLimiting(", StringComparison.Ordinal))
            .Select(file => SourceScan.Relative(file).Split('/')[1]["Encore.Modules.".Length..])
            .Where(module => EncoreTree.ComposedModules.Contains(module, StringComparer.Ordinal))
            .Select(module => $"Map{module}Module")
            .ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(limited);

        var named = HostFiles(host)
            .SelectMany(file => SourceScan.Lines(SourceScan.CodeOnly(File.ReadAllText(file))))
            .SelectMany(line => SourceScan.Identifiers(line.Text))
            .ToHashSet(StringComparer.Ordinal);

        if (!limited.Any(named.Contains))
        {
            return;
        }

        Assert.True(
            named.Contains("UseRateLimiter"),
            $"{host} maps a module whose routes carry rate-limit policies ({string.Join(", ", limited.Where(named.Contains))}) but never calls UseRateLimiter, so every policy is skipped.");
    }

    // Deliberately misses an alias and a using static, so both are reported.
    [GeneratedRegex(@"^\s*(?:global\s+)?using\s+(?<namespace>[\w.]+)\s*;\s*$")]
    private static partial Regex UsingDirectivePattern();

    private static IReadOnlyList<string> HostFiles(string host) =>
        SourceScan.Files(Path.Combine("src", host));

    private static IEnumerable<string> SeamTypes() =>
        EncoreTree.ComposedModules.Select(module => $"{module}Module");

    private static HashSet<string> SeamMethods() =>
        [
            .. EncoreTree.ComposedModules.SelectMany(module => new[] { $"Add{module}Module", $"Map{module}Module" }),
            .. ExtraSeams
        ];

    private static string? AssemblyOf(MetadataReader reader, TypeReferenceHandle handle)
    {
        var scope = reader.GetTypeReference(handle).ResolutionScope;

        while (scope.Kind == HandleKind.TypeReference)
        {
            scope = reader.GetTypeReference((TypeReferenceHandle)scope).ResolutionScope;
        }

        return scope.Kind == HandleKind.AssemblyReference
            ? reader.GetString(reader.GetAssemblyReference((AssemblyReferenceHandle)scope).Name)
            : null;
    }

    private static string FullName(MetadataReader reader, TypeReferenceHandle handle)
    {
        var type = reader.GetTypeReference(handle);
        var name = reader.GetString(type.Name);

        if (type.ResolutionScope.Kind == HandleKind.TypeReference)
        {
            return $"{FullName(reader, (TypeReferenceHandle)type.ResolutionScope)}+{name}";
        }

        var ns = reader.GetString(type.Namespace);

        return ns.Length == 0 ? name : $"{ns}.{name}";
    }
}
