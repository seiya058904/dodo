using System.IO;

namespace PaperTodo;

/// <summary>Dodo's portable data boundary; never reads an adjacent PaperTodo profile.</summary>
internal static class AppPaths
{
    internal static string DataDirectory { get; } = Path.Combine(AppContext.BaseDirectory, "dodo-data");
}
