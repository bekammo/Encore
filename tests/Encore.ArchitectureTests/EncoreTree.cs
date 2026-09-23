using System.Reflection;
using System.Xml.Linq;

namespace Encore.ArchitectureTests;

/// <summary>
/// Locates the repository and reads it as compiled assemblies and as csproj source. The root
/// comes from an assembly attribute written by the csproj, not from walking up directories.
/// Nothing here takes a compile-time dependency on what it inspects.
/// </summary>
internal static class EncoreTree
{
    private static readonly Dictionary<string, Assembly> Loaded = [];
    private static readonly Lock Gate = new();

    /// <summary>Absolute path to the repository root, with a trailing separator.</summary>
    internal static string Root { get; } = Metadata("EncoreRepositoryRoot");

    /// <summary>Every assembly this suite asserts about, so a typo fails in one place.</summary>
    internal static readonly string[] AllAssemblies =
    [
        "Encore.Api",
        "Encore.Payments.Api",
        "Encore.Shared",
        "Encore.Modules.Catalog",
        "Encore.Modules.Catalog.Contracts",
        "Encore.Modules.Orders",
        "Encore.Modules.Notifications",
        "Encore.Modules.Payments",
        "Encore.Modules.Payments.Contracts",
        "Encore.Modules.Inventory",
        "Encore.Modules.Inventory.Contracts",
        "Encore.Modules.Inventory.Domain",
        "Encore.Modules.Shared.Persistence",
        "Encore.Telemetry"
    ];

    /// <summary>The shared persistence project.</summary>
    internal const string SharedPersistence = "Encore.Modules.Shared.Persistence";

    /// <summary>The hosts' OpenTelemetry wiring.</summary>
    internal const string Telemetry = "Encore.Telemetry";

    /// <summary>Every host. A list, so a new host inherits the rules instead of silently escaping them.</summary>
    internal static readonly string[] Hosts =
    [
        "Encore.Api",
        "Encore.Payments.Api"
    ];

    /// <summary>
    /// The projects that declare <c>EncoreZeroDependency</c>. None may reach
    /// <see cref="SharedPersistence"/>, which carries EF Core.
    /// </summary>
    internal static readonly string[] ZeroDependencyProjects =
    [
        "Encore.Shared",
        "Encore.Modules.Catalog.Contracts",
        "Encore.Modules.Inventory.Contracts",
        "Encore.Modules.Payments.Contracts",
        "Encore.Modules.Inventory.Domain"
    ];

    /// <summary>The three public faces. Nothing else may be named across a module boundary.</summary>
    internal static readonly string[] ContractsAssemblies =
    [
        "Encore.Modules.Catalog.Contracts",
        "Encore.Modules.Inventory.Contracts",
        "Encore.Modules.Payments.Contracts"
    ];

    /// <summary>The module implementations. Exact names, never prefixes.</summary>
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
    /// Loads an assembly's metadata without resolving its references, so
    /// <see cref="Assembly.GetReferencedAssemblies"/> works even when they are absent.
    /// </summary>
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
    /// Every other module implementation, plus the Domain: what a module may not name. Exact
    /// names, since <c>Inventory.Contracts</c> starts with <c>Inventory</c>.
    /// </summary>
    internal static IEnumerable<string> OtherModules(string self) =>
        ModuleAssemblies.Where(module => !string.Equals(module, self, StringComparison.Ordinal));

    /// <summary>
    /// Whether a referenced assembly is part of the BCL. <c>Microsoft.*</c> is not assumed to be:
    /// <c>Microsoft.Extensions.*</c> are ordinary packages.
    /// </summary>
    internal static bool IsBcl(string name) =>
        name is "mscorlib" or "netstandard" or "System"
        || name.StartsWith("System.", StringComparison.Ordinal);

    /// <summary>Every csproj under <c>src/</c>, as parsed XML, keyed by project name.</summary>
    internal static IReadOnlyDictionary<string, XDocument> SourceProjects() =>
        Directory
            .EnumerateFiles(Path.Combine(Root, "src"), "*.csproj", SearchOption.AllDirectories)
            .ToDictionary(file => Path.GetFileNameWithoutExtension(file), XDocument.Load, StringComparer.Ordinal);

    /// <summary>The project names a csproj declares a reference to, whether or not the compiler emitted one.</summary>
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
