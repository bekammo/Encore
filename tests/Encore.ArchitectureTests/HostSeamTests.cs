using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.RegularExpressions;

namespace Encore.ArchitectureTests;

/// <summary>
/// A host reaches each module through exactly one seam, <c>Add{Module}Module</c> and
/// <c>Map{Module}Module</c> on the module's <c>{Module}Module</c> class, and knows nothing
/// else. Checked in source, which sees what is written, and in the compiled host, which sees
/// every type and member the compiler resolved, however it was spelled.
/// </summary>
public partial class HostSeamTests
{
    /// <summary>
    /// Seams beyond the Add/Map pair, each a deliberate opt-in: the service API that
    /// <c>Encore.Payments.Api</c> mounts for Orders (008, 018).
    /// </summary>
    private static readonly string[] ExtraSeams = ["MapPaymentsServiceApi"];

    /// <summary>
    /// The only module namespaces a host imports are the modules' root namespaces, where the
    /// seam classes live. Written anywhere else, in code or in a string, <c>Encore.Modules</c>
    /// is a host reaching past a seam: a sub-namespace, a contracts assembly, an alias, a
    /// <c>using static</c> or a fully qualified name.
    /// </summary>
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

    /// <summary>
    /// Every type declared in a module project, and every extension method, is off limits to a
    /// host except the seam: the <c>{Module}Module</c> classes, <c>Add{Module}Module</c>,
    /// <c>Map{Module}Module</c> and <see cref="ExtraSeams"/>. So <c>EfSeatRepository</c>, a
    /// command handler or <c>CheckoutService</c> cannot appear in a host, even in a module's
    /// root namespace.
    /// </summary>
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

    /// <summary>
    /// The compiled half of the rule above, and the precise one: every type the host's
    /// assembly references from a module, contracts or shared persistence assembly is a
    /// <c>{Module}Module</c> seam class, and every member it calls on one is a seam method.
    /// Sees what text cannot: <c>var</c>, <c>typeof</c>, generic arguments.
    /// </summary>
    [Theory]
    [MemberData(nameof(TheoryRows.Hosts), MemberType = typeof(TheoryRows))]
    public void CompiledHost_ShouldReferenceNothingFromAModuleButTheSeam(string host)
    {
        var guarded = EncoreTree.ModuleAssemblies
            .Concat(EncoreTree.ContractsAssemblies)
            .Append(EncoreTree.SharedPersistence)
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

    /// <summary>
    /// Sanity: the derivations find what they must, so the tests above cannot pass vacuously,
    /// and every extra seam still exists, so a rename cannot leave a stale allowance behind.
    /// </summary>
    [Fact]
    public void TheSeamAndWhatItHidesShouldBeFound()
    {
        var moduleFiles = SourceScan.ModuleFiles();
        var types = SourceScan.DeclaredTypes(moduleFiles);
        var methods = SourceScan.DeclaredExtensionMethods(moduleFiles);

        Assert.Contains("EfSeatRepository", types);
        Assert.Contains("HoldSeatCommandHandler", types);
        Assert.Contains("CheckoutService", types);

        foreach (var seam in EncoreTree.ComposedModules.Select(module => $"{module}Module"))
        {
            Assert.Contains(seam, types);
        }

        foreach (var seam in EncoreTree.ComposedModules.Select(module => $"Add{module}Module").Concat(ExtraSeams))
        {
            Assert.Contains(seam, methods);
        }
    }

    /// <summary>Sanity: every host composes at least one module through the seam.</summary>
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

    /// <summary><c>using Encore.Modules.Catalog;</c>, optionally global. No alias, no <c>static</c>.</summary>
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

    /// <summary>The assembly a type reference resolves to, through any enclosing types.</summary>
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

    /// <summary><c>Namespace.Type</c>, or <c>Namespace.Outer+Nested</c>.</summary>
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
