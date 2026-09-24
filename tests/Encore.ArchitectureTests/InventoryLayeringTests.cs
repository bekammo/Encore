using System.Text.RegularExpressions;

namespace Encore.ArchitectureTests;

/// <summary>
/// The rules inside the Inventory assembly, where <c>Ports/</c>, <c>Adapters/</c> and
/// <c>Application/</c> are folders of one project and no reference can tell them apart (002, 021).
/// </summary>
/// <remarks>
/// Read from source, since the folders share an assembly. The adapters' type names are derived
/// from <c>Adapters/</c> rather than listed, so a new adapter is covered the day it is written.
/// </remarks>
public partial class InventoryLayeringTests
{
    private const string AdaptersNamespace = "Encore.Modules.Inventory.Adapters";

    private static readonly string Inventory = Path.Combine("src", "Encore.Modules.Inventory");

    /// <summary>
    /// The use cases talk to the ports. Neither the adapters' namespace nor any adapter's type
    /// name may appear in <c>Application/</c>, in code or in a comment.
    /// </summary>
    [Fact]
    public void Application_ShouldNameNothingFromTheAdapters()
    {
        var found = Mentions(Path.Combine(Inventory, "Application"), AdapterTypes());

        Assert.True(
            found.Count == 0,
            $"Application/ reaches the adapters only through Ports/; it may not name {AdaptersNamespace} or an adapter's type. Found: {string.Join("; ", found)}");
    }

    /// <summary>
    /// A port never names an adapter's type, including in XML docs: the port declares what it
    /// throws, and the adapter translates into it (002).
    /// </summary>
    [Fact]
    public void Ports_ShouldNameNoAdapterEvenInDocumentation()
    {
        var found = Mentions(Path.Combine(Inventory, "Ports"), AdapterTypes());

        Assert.True(
            found.Count == 0,
            $"A port may not name an adapter's type or namespace, including in XML docs. Found: {string.Join("; ", found)}");
    }

    /// <summary>
    /// Telemetry is emitted at adapter edges only (021): no <c>ActivitySource</c>, no
    /// <c>Meter</c>, nothing from <c>System.Diagnostics.Metrics</c> or the telemetry adapter,
    /// in the use cases or the Domain. Code only, so a comment may explain the rule.
    /// </summary>
    [Theory]
    [InlineData("src/Encore.Modules.Inventory/Application")]
    [InlineData("src/Encore.Modules.Inventory.Domain")]
    public void NoTelemetryOutsideTheAdapterEdges(string directory)
    {
        var found = new List<string>();

        foreach (var file in SourceScan.Files(directory))
        {
            foreach (var (number, text) in SourceScan.Lines(SourceScan.CodeOnly(File.ReadAllText(file))))
            {
                found.AddRange(TelemetryPattern()
                    .Matches(text)
                    .Select(match => $"{SourceScan.Relative(file)}:{number} uses {match.Value}"));
            }
        }

        Assert.True(
            found.Count == 0,
            $"Modules emit telemetry at adapter edges only, never in the Domain or Application/ (021). Found: {string.Join("; ", found)}");
    }

    /// <summary>
    /// Sanity: a derivation that finds no adapters makes the two tests above pass vacuously, and
    /// an empty folder does the same.
    /// </summary>
    [Fact]
    public void ThereShouldBeAdaptersPortsAndUseCasesToInspect()
    {
        var adapters = AdapterTypes();

        Assert.Contains("EfSeatRepository", adapters);
        Assert.Contains("RedisDistributedLock", adapters);
        Assert.Contains("InventoryTelemetry", adapters);

        Assert.NotEmpty(SourceScan.Files(Path.Combine(Inventory, "Ports")));
        Assert.NotEmpty(SourceScan.Files(Path.Combine(Inventory, "Application")));
    }

    /// <summary>The telemetry vocabulary, as whole words or qualified names.</summary>
    [GeneratedRegex(@"\bActivitySource\b|\bMeter\b|\bSystem\.Diagnostics\.Metrics\b|\bAdapters\.Telemetry\b")]
    private static partial Regex TelemetryPattern();

    /// <summary>Every type declared under <c>Adapters/</c>, nested ones and migrations included.</summary>
    private static IReadOnlySet<string> AdapterTypes() =>
        SourceScan.DeclaredTypes(SourceScan.Files(Path.Combine(Inventory, "Adapters")));

    /// <summary>
    /// Each line under <paramref name="directory"/> that names the adapters' namespace or one of
    /// <paramref name="adapterTypes"/> as a whole word. The raw text, comments and all.
    /// </summary>
    private static List<string> Mentions(string directory, IReadOnlySet<string> adapterTypes)
    {
        var found = new List<string>();

        foreach (var file in SourceScan.Files(directory))
        {
            foreach (var (number, text) in SourceScan.Lines(File.ReadAllText(file)))
            {
                var at = $"{SourceScan.Relative(file)}:{number}";

                if (text.Contains(AdaptersNamespace, StringComparison.Ordinal))
                {
                    found.Add($"{at} names {AdaptersNamespace}");
                }

                found.AddRange(SourceScan
                    .Identifiers(text)
                    .Where(adapterTypes.Contains)
                    .Distinct(StringComparer.Ordinal)
                    .Select(name => $"{at} names {name}"));
            }
        }

        return found;
    }
}
