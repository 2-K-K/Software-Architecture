namespace Ticketing.Architecture.Tests;

internal static class SolutionPaths
{
    public static string Root { get; } = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Ticketing.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Не знайдено Ticketing.sln від " + AppContext.BaseDirectory);
    }

    /// <summary>Усі .cs-файли проєкту, крім згенерованих (bin, obj).</summary>
    public static IEnumerable<string> SourceFiles(string project)
    {
        var sep = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(Path.Combine(Root, "src", project), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}") && !f.Contains($"{sep}bin{sep}"));
    }
}
