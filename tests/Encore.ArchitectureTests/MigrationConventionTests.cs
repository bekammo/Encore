using System.Text.RegularExpressions;

namespace Encore.ArchitectureTests;

/// <summary>
/// Conventions the scaffolded migration sources have to keep.
/// </summary>
public class MigrationConventionTests
{
    /// <summary>
    /// <c>xmin</c> is a Postgres system column. A table may map a concurrency
    /// token onto it — <c>SeatConfiguration</c> does, and that mapping is the
    /// whole mechanism — but no migration may try to create it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Three migrations carried <c>xmin = table.Column&lt;uint&gt;(...)</c> in
    /// their <c>CreateTable</c> until DECISIONS 042. It was inert, because the
    /// Npgsql provider strips the column before emitting SQL — but only because
    /// of that provider. In hand-written DDL, or pasted into psql, it is
    /// <c>ERROR: column name "xmin" conflicts with a system column name</c>.
    /// </para>
    /// <para>
    /// This test exists because regenerating any of those migrations would put
    /// the line straight back and nothing else would notice.
    /// </para>
    /// <para>
    /// Deliberately narrow. It matches the <c>CreateTable</c> form only, so the
    /// legitimate <c>HasColumnName("xmin")</c> in the EF configurations and the
    /// <c>xmin</c> property entries in the model snapshots are untouched — those
    /// are the mapping, which is correct.
    /// </para>
    /// </remarks>
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
                    offenders.Add($"{Path.GetRelativePath(EncoreTree.Root, file)}:{i + 1}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            $"xmin is a system column and must never appear in a migration's CreateTable — map a concurrency token onto it in the EF configuration instead. Found at: {string.Join("; ", offenders)}");
    }

    /// <summary>
    /// The expired-hold sweep's index is filtered to held seats, and the filter says
    /// the number the enum says.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>ix_seats_expiring_holds</c> is partial — <c>"Status" = 1</c> — because the
    /// interesting set is the handful of seats currently held and the table is every
    /// seat in the system (<c>DECISIONS.md</c> 068). The number is the stored form of
    /// <c>SeatStatus.Held</c>, written as a SQL literal because an index filter
    /// cannot be an expression over the enum.
    /// </para>
    /// <para>
    /// So it is pinned here, in <c>PaymentConfiguration</c>'s spirit: renumbering the
    /// enum would change which rows the index covers without changing which rows the
    /// sweep asks for, and nothing else in the build would notice. The failure that
    /// produces is a sweep that quietly scans instead of seeking — slow, correct, and
    /// invisible.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheExpiringHoldsIndexShouldBeFilteredToTheHeldStatus()
    {
        // The enum is read out of its source rather than referenced: this project
        // takes every ProjectReference with ReferenceOutputAssembly="false", so it
        // can read the tree but cannot name a type in it (037).
        var statuses = File.ReadAllText(
            Path.Combine(EncoreTree.Root, "src", "Encore.Modules.Inventory.Domain", "SeatStatus.cs"));

        var held = Regex.Match(statuses, @"Held\s*=\s*(?<value>\d+)");

        Assert.True(held.Success, "Could not read SeatStatus.Held's value out of SeatStatus.cs.");

        var expected = $"filter: \"\\\"Status\\\" = {held.Groups["value"].Value}\"";

        var migration = MigrationSources()
            .Where(file => Path.GetFileName(file).Contains("AddExpiringHoldsIndex", StringComparison.Ordinal))
            .Where(file => !file.EndsWith(".Designer.cs", StringComparison.Ordinal))
            .ToList();

        var file = Assert.Single(migration);
        var source = File.ReadAllText(file);

        Assert.Contains("ix_seats_expiring_holds", source, StringComparison.Ordinal);
        Assert.Contains(expected, source, StringComparison.Ordinal);
    }

    /// <summary>
    /// Sanity: if the glob ever stops finding migrations, every test in this class
    /// would pass by finding nothing.
    /// </summary>
    [Fact]
    public void ThereShouldBeMigrationsToInspect()
    {
        Assert.NotEmpty(MigrationSources());
    }

    /// <summary>
    /// Hand-written migration bodies only — the generated Designer and snapshot
    /// files describe the model rather than the DDL.
    /// </summary>
    private static List<string> MigrationSources() =>
        [.. Directory
            .EnumerateFiles(Path.Combine(EncoreTree.Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(file => file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => !file.EndsWith(".Designer.cs", StringComparison.Ordinal))
            .Where(file => !file.EndsWith("ModelSnapshot.cs", StringComparison.Ordinal))];
}
