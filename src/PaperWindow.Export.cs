using System.Windows;
using Microsoft.Win32;

namespace PaperTodo;

public sealed partial class PaperWindow
{
    private void ExportMarkdown()
    {
        if (!PaperMarkdownExportService.CanExport(_paper)) return;
        try
        {
            var dialog = new SaveFileDialog
            {
                Title = Strings.Get("ExportMarkdownTitle"),
                Filter = Strings.Get("ExportMarkdownFilter"),
                DefaultExt = ".md",
                AddExtension = true,
                OverwritePrompt = true,
                CheckPathExists = true,
                FileName = PaperMarkdownExportService.SuggestedFileName(_controller.PaperDisplayTitle(_paper))
            };
            if (dialog.ShowDialog(this) != true) return;
            // A modal dialog can pump external commands. Recheck the body before committing.
            if (!PaperMarkdownExportService.CanExport(_paper)) return;
            CommitPendingMarkdownContentForSave();
            PaperMarkdownExportService.Export(_paper, _controller.ImageStore, dialog.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Strings.Format("ExportMarkdownFailureMessage", ex.Message),
                Strings.Get("ExportMarkdownFailureTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
