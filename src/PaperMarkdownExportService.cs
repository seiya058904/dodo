using System.IO;
using System.Text;

namespace PaperTodo;

/// <summary>One-way portable Markdown snapshots; exported files never become application state.</summary>
internal static class PaperMarkdownExportService
{
    internal static bool CanExport(PaperData paper) =>
        paper.Type == PaperTypes.Todo ||
        (paper.Type == PaperTypes.Note &&
         (string.IsNullOrWhiteSpace(paper.BodyProviderId) ||
          paper.BodyProviderId.Trim() == PaperBodyProviderIds.Markdown));

    internal static string SuggestedFileName(string? title)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var stem = new string((title ?? "").Select(c => invalid.Contains(c) ? '_' : c).ToArray())
            .Trim().TrimEnd('.', ' ');
        if (stem.Length > 100)
        {
            stem = stem[..100];
            if (char.IsHighSurrogate(stem[^1])) stem = stem[..^1];
            stem = stem.TrimEnd('.', ' ');
        }
        if (stem.Length == 0) stem = "Dodo";
        var deviceName = stem.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
        if (deviceName is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            (deviceName.Length == 4 &&
             (deviceName.StartsWith("COM", StringComparison.Ordinal) ||
              deviceName.StartsWith("LPT", StringComparison.Ordinal)) &&
             "123456789¹²³".Contains(deviceName[3])))
            stem = "_" + stem;
        return stem + ".md";
    }

    internal static void Export(PaperData paper, NoteImageStore images, string destination)
    {
        ArgumentNullException.ThrowIfNull(paper);
        ArgumentNullException.ThrowIfNull(images);
        if (!CanExport(paper)) throw new InvalidOperationException("This paper has no Markdown export.");
        var path = Path.GetFullPath(destination);
        if (!string.Equals(Path.GetExtension(path), ".md", StringComparison.OrdinalIgnoreCase))
            throw new IOException(Strings.Get("ExportMarkdownExtensionRequired"));
        var directory = Path.GetDirectoryName(path)!;
        // The selected directory must exist. Do not silently create a mistyped user path.
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);

        // Never reuse title-derived image directories or fixed .tmp names in a user's folder.
        // Separate generations also keep the old document's images valid if replacement fails.
        var token = Guid.NewGuid().ToString("N");
        var temporaryPath = Path.Combine(directory, ".dodo-export-" + token + ".tmp");
        var imageDirectory = Path.Combine(directory, "dodo-assets-" + token);
        var completed = false;
        var createdTemporaryFile = false;
        var createdImageDirectory = false;
        var exportedImagePaths = new List<string>();
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew,
                       FileAccess.Write, FileShare.None))
            {
                createdTemporaryFile = true;
                var markdown = paper.Type == PaperTypes.Todo
                    ? TodoMarkdown(paper)
                    : images.ConvertMarkdownForExport(paper.Id, paper.Content ?? "", imageDirectory, directory,
                        out createdImageDirectory, exportedImagePaths);
                var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(markdown);
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
            completed = true;
        }
        finally
        {
            if (createdTemporaryFile && !completed)
            {
                TryDeleteFile(temporaryPath);
                // Only this operation's new flat asset directory is eligible for cleanup.
                // Existing export generations and any unexpected subdirectories remain intact.
                if (createdImageDirectory) TryRemoveFailedAssets(imageDirectory, exportedImagePaths);
            }
        }
    }

    private static string TodoMarkdown(PaperData paper)
    {
        var builder = new StringBuilder();
        foreach (var item in paper.Items.OrderBy(item => item.Order))
        {
            var lines = (item.Text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            builder.Append(item.Done ? "- [x] " : "- [ ] ");
            for (var index = 0; index < lines.Length; index++)
            {
                if (index > 0) builder.Append("\n      ");
                foreach (var character in lines[index])
                {
                    // Todo text is plain text, so Markdown punctuation must remain literal.
                    if ("\\`*_{}[]<>()#+-.!|~>&".Contains(character)) builder.Append('\\');
                    builder.Append(character);
                }
            }
            builder.Append('\n');
        }
        return builder.ToString();
    }

    private static void TryDeleteFile(string path)
    {
        try { File.Delete(path); }
        catch { /* Best-effort cleanup must not hide the original export failure. */ }
    }

    private static void TryRemoveFailedAssets(string directory, IReadOnlyList<string> exportedImagePaths)
    {
        try
        {
            if (!Directory.Exists(directory) ||
                (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;
            foreach (var file in exportedImagePaths) TryDeleteFile(file);
            Directory.Delete(directory, recursive: false);
        }
        catch { /* Keep unexpected contents rather than recursively deleting user files. */ }
    }
}
