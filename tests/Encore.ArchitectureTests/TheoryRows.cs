namespace Encore.ArchitectureTests;

public static class TheoryRows
{
    public static TheoryData<string> Hosts => new(EncoreTree.Hosts);

    public static TheoryData<string> NonHosts => new(EncoreTree.NonHosts);

    public static TheoryData<string> Composed =>
        new(EncoreTree.NonHosts.Where(assembly => assembly != EncoreTree.Telemetry));

    public static TheoryData<string> ComposedModuleAssemblies =>
        new(EncoreTree.ComposedModules.Select(module => $"Encore.Modules.{module}"));

    public static TheoryData<string> ContractsAssemblies => new(EncoreTree.ContractsAssemblies);

    public static TheoryData<string> ZeroDependencyProjects => new(EncoreTree.ZeroDependencyProjects);
}
