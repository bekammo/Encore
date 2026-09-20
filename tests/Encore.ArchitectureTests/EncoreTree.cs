using System.Reflection;
using System.Xml.Linq;

namespace Encore.ArchitectureTests;

/// <summary>
/// Locates the repository on disk and reads it the two ways this suite needs:
/// as compiled assemblies, and as csproj source.
/// </summary>
/// <remarks>
/// <para>
/// The root arrives as an assembly attribute written by the csproj rather than
/// being discovered at run time. Walking up from <c>AppContext.BaseDirectory</c>
/// looking for a <c>.sln</c> works until it does not — a different output layout,
/// a published test bundle, someone running from a temp directory — and fails as
/// "file not found" rather than as "I looked in the wrong place".
/// </para>
/// <para>
/// Nothing here takes a compile-time dependency on anything it inspects. See the
/// <c>ReferenceOutputAssembly="false"</c> comment in the csproj, and DECISIONS 037.
/// </para>
/// </remarks>
internal static class EncoreTree
{
    private static readonly Dictionary<string, Assembly> Loaded = [];
    private static readonly Lock Gate = new();

    /// <summary>Absolute path to the repository root, with a trailing separator.</summary>
    internal static string Root { get; } = Metadata("EncoreRepositoryRoot");

    /// <summary>The assemblies this suite asserts about, by simple name.</summary>
    /// <remarks>
    /// Every name a test mentions appears here, so a typo is caught once by
    /// <c>AssemblyReferenceTests.EveryInspectedAssemblyShouldBePresent</c> rather
    /// than showing up as several confusing unrelated failures.
    /// </remarks>
    internal static readonly string[] AllAssemblies =
    [
        "Encore.Api",
        "Encore.Shared",
        "Encore.Modules.Catalog",
        "Encore.Modules.Catalog.Contracts",
        "Encore.Modules.Orders",
        "Encore.Modules.Notifications",
        "Encore.Modules.Payments",
        "Encore.Modules.Payments.Contracts",
        "Encore.Modules.Inventory",
        "Encore.Modules.Inventory.Contracts",
        "Encore.Modules.Inventory.Domain"
    ];

    /// <summary>The three public faces. Nothing else may be named across a module boundary.</summary>
    internal static readonly string[] ContractsAssemblies =
    [
        "Encore.Modules.Catalog.Contracts",
        "Encore.Modules.Inventory.Contracts",
        "Encore.Modules.Payments.Contracts"
    ];

    /// <summary>
    /// The module implementations. Exact names, never prefixes — see
    /// <see cref="OtherModules"/>.
    /// </summary>
    internal static readonly string[] ModuleAssemblies =
    [
        "Encore.Modules.Catalog",
        "Encore.Modules.Orders",
        "Encore.Modules.Notifications",
        "Encore.Modules.Payments",
        "Encore.Modules.Inventory",
        "Encore.Modules.Inventory.Domain"
    ];

    /// <summary>The path a project's compiled output is expected at.</summary>
    internal static string AssemblyPath(string name) =>
        Path.Combine(
            Root,
            "src",
            name,
            "bin",
            Metadata("EncoreConfiguration"),
            Metadata("EncoreTargetFramework"),
            name + ".dll");

    /// <summary>
    /// Loads a compiled assembly by simple name, without resolving its closure.
    /// </summary>
    /// <remarks>
    /// <see cref="Assembly.LoadFrom(string)"/> reads the metadata tables, so
    /// <see cref="Assembly.GetReferencedAssemblies"/> answers without any of the
    /// referenced assemblies needing to be present. That matters: this process
    /// has no ASP.NET Core loaded, and <c>Encore.Api</c> still loads fine.
    /// </remarks>
    internal static Assembly Load(string name)
    {
        lock (Gate)
        {
            if (Loaded.TryGetValue(name, out var cached))
            {
                return cached;
            }

            var assembly = Assembly.LoadFrom(AssemblyPath(name));
            Loaded[name] = assembly;
            return assembly;
        }
    }

    /// <summary>The names this assembly actually emitted a reference to.</summary>
    internal static IReadOnlyList<string> ReferencedNames(string name) =>
        [.. Load(name).GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty)];

    /// <summary>
    /// Every module implementation except this one, plus the Domain. What a module
    /// is forbidden to name.
    /// </summary>
    /// <remarks>
    /// <b>Exact-name comparison, never a prefix test.</b>
    /// <c>Encore.Modules.Inventory.Contracts</c> starts with
    /// <c>Encore.Modules.Inventory</c>, so a <c>StartsWith</c> here would ban the
    /// very seam these rules exist to permit.
    /// </remarks>
    internal static IEnumerable<string> OtherModules(string self) =>
        ModuleAssemblies.Where(module => !string.Equals(module, self, StringComparison.Ordinal));

    /// <summary>Whether a referenced assembly name is part of the base class library.</summary>
    /// <remarks>
    /// <c>Microsoft.*</c> is deliberately absent. <c>Microsoft.Extensions.*</c> is a
    /// package like any other, and treating the prefix as BCL would wave through
    /// exactly the dependency the domain-purity rule exists to refuse.
    /// </remarks>
    internal static bool IsBcl(string name) =>
        name is "mscorlib" or "netstandard" or "System"
        || name.StartsWith("System.", StringComparison.Ordinal);

    /// <summary>Every csproj under <c>src/</c>, as parsed XML, keyed by project name.</summary>
    internal static IReadOnlyDictionary<string, XDocument> SourceProjects() =>
        Directory
            .EnumerateFiles(Path.Combine(Root, "src"), "*.csproj", SearchOption.AllDirectories)
            .ToDictionary(file => Path.GetFileNameWithoutExtension(file), XDocument.Load, StringComparer.Ordinal);

    /// <summary>
    /// The project names a csproj declares a <c>ProjectReference</c> to, whether or
    /// not the compiler ended up emitting one.
    /// </summary>
    internal static IReadOnlyList<string> DeclaredProjectReferences(XDocument project) =>
        [.. project
            .Descendants("ProjectReference")
            .Select(reference => (string?)reference.Attribute("Include"))
            .Where(include => !string.IsNullOrWhiteSpace(include))
            .Select(include => Path.GetFileNameWithoutExtension(include!.Replace('\\', '/')))];

    private static string Metadata(string key) =>
        typeof(EncoreTree).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(attribute => attribute.Key == key)
            .Value!;
}
