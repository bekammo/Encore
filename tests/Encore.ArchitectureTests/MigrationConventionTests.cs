using System.Text.RegularExpressions;

namespace Encore.ArchitectureTests;

public sealed class MigrationConventionTests
{
    /// <summary>
    /// A <c>CreateTable</c> column for xmin fails outside the Npgsql provider, and regenerating a
    /// migration brings it back (004).
    /// </summary>
    [Fact]
    public void NoMigrationShouldCreateTheXminSystemColumn()
    {
        var offenders = new List<string>();

        foreach (var file in MigrationSources())
        {
            var lines = File.ReadAllLines(file);

            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("xmin = table.Column", StringComparison.Ordinal))
                {
                    offenders.Add($"{SourceScan.Relative(file)}:{i + 1}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"xmin is a system column and must never appear in a migration's CreateTable — map a concurrency token onto it in the EF configuration instead. Found at: {string.Join("; ", offenders)}");
    }

    /// <summary>
    /// The sweep's partial index filters on the stored value of <c>SeatStatus.Held</c>, so
    /// renumbering the enum cannot silently change which rows it covers.
    /// </summary>
    [Fact]
    public void TheExpiringHoldsIndexShouldBeFilteredToTheHeldStatus()
    {
        // Read from source: this project cannot reference the types it inspects.
        var statuses = File.ReadAllText(
            Path.Combine(EncoreTree.Root, "src", "Encore.Modules.Inventory.Domain", "SeatStatus.cs"));

        var held = Regex.Match(statuses, @"Held\s*=\s*(?<value>\d+)");

        Assert.True(held.Success, "Could not read SeatStatus.Held's value out of SeatStatus.cs.");

        var expected = $"filter: \"\\\"Status\\\" = {held.Groups["value"].Value}\"";

        var migration = MigrationSources()
            .Where(file => Path.GetFileName(file).Contains("AddExpiringHoldsIndex", StringComparison.Ordinal))
            .ToList();

        var file = Assert.Single(migration);
        var source = File.ReadAllText(file);

        Assert.Contains("ix_seats_expiring_holds", source, StringComparison.Ordinal);
        Assert.Contains(expected, source, StringComparison.Ordinal);
    }

    [Fact]
    public void ThereShouldBeMigrationsToInspect() =>
        Assert.NotEmpty(MigrationSources());

    private static List<string> MigrationSources() =>
        [.. Directory
            .EnumerateFiles(Path.Combine(EncoreTree.Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !file.EndsWith(".Designer.cs", StringComparison.Ordinal))
            .Where(file => !file.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal))];
}
