using System.Globalization;

namespace PaperTodo;

// A presentation derived from the current model, never a separately persisted count.
internal static class TodoCapsuleTitle
{
    public static string Suffix(PaperData paper, bool enabled)
    {
        if (!enabled || paper.Type != PaperTypes.Todo) return string.Empty;
        var count = paper.Items.Count(item => !item.Done && !TodoRules.IsPlaceholder(item));
        return count == 0 ? string.Empty : " · " + count.ToString(CultureInfo.InvariantCulture);
    }
}
