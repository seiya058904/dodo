using System.Text.Json;

namespace PaperTodo;

internal sealed partial class WebPaperBodySession
{
    private object? ExecutePaperPresentationHostRequest(
        string method,
        JsonElement parameters)
    {
        var activate = OptionalPayloadBoolean(parameters, "activate") ?? true;
        switch (method)
        {
            case "paper.show":
                _context.Presentation.Show(activate);
                break;
            case "paper.hide":
                _context.Presentation.Hide();
                break;
            case "paper.toggle":
                _context.Presentation.ToggleVisibility(activate);
                break;
            case "paper.expand":
                _context.Presentation.Expand(activate);
                break;
            case "paper.collapse":
                _context.Presentation.Collapse();
                break;
            case "paper.toggleCollapsed":
                _context.Presentation.ToggleCollapsed(activate);
                break;
            case "paper.activate":
                _context.Presentation.Activate();
                break;
            default:
                throw new PaperTodo.Plugin.PaperTodoPluginException(
                    "method_not_found",
                    $"Unknown Dodo paper presentation method: {method}");
        }

        return null;
    }
}
