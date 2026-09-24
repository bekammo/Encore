namespace Encore.ArchitectureTests;

/// <summary>
/// <see cref="EncoreTree"/>'s lists as theory rows, so a rule that holds for every host or every
/// assembly is written once and a new entry inherits it.
/// </summary>
public static class TheoryRows
{
    /// <summary>Every host.</summary>
    public static TheoryData<string> Hosts => Of(EncoreTree.Hosts);

    /// <summary>Every inspected assembly that is not a host.</summary>
    public static TheoryData<string> NonHosts => Of(EncoreTree.NonHosts);

    /// <summary>Every inspected assembly but the hosts and their telemetry wiring: what the hosts compose.</summary>
    public static TheoryData<string> Composed =>
        Of(EncoreTree.NonHosts.Where(assembly => assembly != EncoreTree.Telemetry));

    /// <summary>Every module implementation a host composes, by assembly name.</summary>
    public static TheoryData<string> ComposedModuleAssemblies =>
        Of(EncoreTree.ComposedModules.Select(module => $"Encore.Modules.{module}"));

    private static TheoryData<string> Of(IEnumerable<string> values)
    {
        var rows = new TheoryData<string>();

        foreach (var value in values)
        {
            rows.Add(value);
        }

        return rows;
    }
}
