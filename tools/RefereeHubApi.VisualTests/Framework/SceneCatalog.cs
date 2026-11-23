using System.Reflection;

namespace RefereeHubApi.VisualTests.Framework;

public sealed record SceneInfo(Type Type, string Category, string Name, string Description)
{
    public VisualScene Create() => (VisualScene)Activator.CreateInstance(Type)!;

    public override string ToString() => $"{Category}/{Name}";
}

// every concrete VisualScene in this assembly, sorted by category then name.
public static class SceneCatalog
{
    public static IReadOnlyList<SceneInfo> All { get; } = discover();

    private static List<SceneInfo> discover() => typeof(SceneCatalog).Assembly.GetTypes()
        .Where(t => t.IsSubclassOf(typeof(VisualScene)) && !t.IsAbstract && t.GetConstructor(Type.EmptyTypes) != null)
        .Select(t =>
        {
            var probe = (VisualScene)Activator.CreateInstance(t)!;
            return new SceneInfo(t, probe.Category, probe.Name, probe.Description);
        })
        .OrderBy(s => s.Category, StringComparer.OrdinalIgnoreCase)
        .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public static SceneInfo? Find(string name) => All.FirstOrDefault(s =>
        string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)
        || string.Equals(s.ToString(), name, StringComparison.OrdinalIgnoreCase)
        || string.Equals(s.Type.Name, name, StringComparison.OrdinalIgnoreCase));
}
