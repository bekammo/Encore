using System.Text.RegularExpressions;

namespace Encore.ArchitectureTests;

/// <summary>
/// <c>Ports/</c>, <c>Adapters/</c> and <c>Application/</c> are folders of one project, so their
/// rules are read from source (002, 021).
/// </summary>
public sealed partial class InventoryLayeringTests
{
    private const string AdaptersNamespace = "Encore.Modules.Inventory.Adapters";

    private static readonly string Inventory = Path.Combine("src", "Encore.Modules.Inventory");

    [Fact]
    public void Application_ShouldNameNothingFromTheAdapters()
    {
        var found = Mentions(Path.Combine(Inventory, "Application"), AdapterTypes());

        Assert.True(
            found.Count == 0,
            $"Application/ reaches the adapters only through Ports/; it may not name {AdaptersNamespace} or an adapter's type. Found: {string.Join("; ", found)}");
    }

    /// <summary>The port declares what it throws; the adapter translates into it (002).</summary>
    [Fact]
    public void Ports_ShouldNameNoAdapterEvenInDocumentation()
    {
        var found = Mentions(Path.Combine(Inventory, "Ports"), AdapterTypes());

        Assert.True(
            found.Count == 0,
            $"A port may not name an adapter's type or namespace, including in XML docs. Found: {string.Join("; ", found)}");
    }

    /// <summary>Code only, so a comment may explain the rule (021).</summary>
    [Theory]
    [InlineData("src/Encore.Modules.Inventory/Application")]
    [InlineData("src/Encore.Modules.Inventory.Domain")]
    public void NoTelemetryShouldBeEmittedOutsideTheAdapterEdges(string directory)
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

    [GeneratedRegex(@"\bActivitySource\b|\bMeter\b|\bSystem\.Diagnostics\.Metrics\b|\bAdapters\.Telemetry\b")]
    private static partial Regex TelemetryPattern();

    // Migrations included, so a migration's class name is forbidden too.
    private static IReadOnlySet<string> AdapterTypes() =>
        SourceScan.DeclaredTypes(SourceScan.Files(Path.Combine(Inventory, "Adapters")));

    // Raw text, not CodeOnly: a comment naming an adapter counts (002).
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
