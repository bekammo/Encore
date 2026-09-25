using System.Reflection;
using System.Xml.Linq;

namespace Encore.ArchitectureTests;

internal static class EncoreTree
{
    internal static string Root { get; } = Metadata("EncoreRepositoryRoot");

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
        "Encore.Modules.Shared.Http",
        "Encore.Telemetry"
    ];

    internal const string SharedPersistence = "Encore.Modules.Shared.Persistence";

    internal const string SharedHttp = "Encore.Modules.Shared.Http";

    /// <summary>Not modules: inert code every module may share, which may not know a module exists (017, 029).</summary>
    internal static readonly string[] SharedModuleProjects = [SharedPersistence, SharedHttp];

    internal const string Telemetry = "Encore.Telemetry";

    internal static readonly string[] Hosts =
    [
        "Encore.Api",
        "Encore.Payments.Api"
    ];

    internal static readonly string[] ZeroDependencyProjects =
    [
        "Encore.Shared",
        "Encore.Modules.Catalog.Contracts",
        "Encore.Modules.Inventory.Contracts",
        "Encore.Modules.Payments.Contracts",
        "Encore.Modules.Inventory.Domain"
    ];

    internal static readonly string[] ContractsAssemblies =
    [
        "Encore.Modules.Catalog.Contracts",
        "Encore.Modules.Inventory.Contracts",
        "Encore.Modules.Payments.Contracts"
    ];

    /// <summary>Compared by exact name, never by prefix: <c>Inventory.Contracts</c> starts with <c>Inventory</c> (002).</summary>
    internal static readonly string[] ModuleAssemblies =
    [
        "Encore.Modules.Catalog",
        "Encore.Modules.Orders",
        "Encore.Modules.Notifications",
        "Encore.Modules.Payments",
        "Encore.Modules.Inventory",
        "Encore.Modules.Inventory.Domain"
    ];

    /// <summary>Must stay below <see cref="ModuleAssemblies"/>: static initialisers run in declaration order.</summary>
    internal static readonly string[] ComposedModules =
    [
        .. ModuleAssemblies
            .Where(module => module.Count(character => character == '.') == 2)
            .Select(module => module["Encore.Modules.".Length..])
    ];

    internal static IEnumerable<string> NonHosts =>
        AllAssemblies.Where(assembly => !Hosts.Contains(assembly, StringComparer.Ordinal));

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
    /// <c>LoadFrom</c> resolves references lazily, so <c>GetReferencedAssemblies</c> works when
    /// they are absent.
    /// </summary>
    internal static Assembly Load(string name) => Assembly.LoadFrom(AssemblyPath(name));

    internal static IReadOnlyList<string> ReferencedNames(string name) =>
        [.. Load(name).GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty)];

    internal static IEnumerable<string> OtherModules(string self) =>
        ModuleAssemblies.Where(module =>
            module != self
            && !(self == "Encore.Modules.Inventory" && module == "Encore.Modules.Inventory.Domain"));

    /// <summary><c>Microsoft.*</c> is not assumed BCL: <c>Microsoft.Extensions.*</c> are ordinary packages.</summary>
    internal static bool IsBcl(string name) =>
        name is "mscorlib" or "netstandard" or "System"
        || name.StartsWith("System.", StringComparison.Ordinal);

    internal static IReadOnlyDictionary<string, XDocument> SourceProjects() =>
        Directory
            .EnumerateFiles(Path.Combine(Root, "src"), "*.csproj", SearchOption.AllDirectories)
            .ToDictionary(file => Path.GetFileNameWithoutExtension(file), XDocument.Load, StringComparer.Ordinal);

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
