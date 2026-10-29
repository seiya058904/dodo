using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo;

internal static class MarkdownExportChecks
{
    internal static int RunStandalone()
    {
        var failures = 0;
        Run((name, check) =>
        {
            try { check(); Console.WriteLine("PASS " + name); }
            catch (Exception ex) { failures++; Console.Error.WriteLine("FAIL " + name + ": " + ex); }
        });
        return failures == 0 ? 0 : 1;
    }

    internal static void Run(Action<string, Action> check)
    {
        check("Markdown export preserves text, empty bodies and safe filenames", TextAndNames);
        check("Markdown export preserves Todo order, completion and literal text", Todos);
        check("Markdown export bundles portable images and isolates repeated exports", PortableImages);
        check("Markdown export failures preserve existing files and unrelated assets", FailureSafety);
        check("Markdown export rejects unwritable directories", UnwritableDirectory);
    }

    private static void TextAndNames()
    {
        using var fixture = new Fixture();
        var paper = new PaperData { Type = PaperTypes.Note, Content = "# 中文标题\n\n正文 **加粗**\n" };
        var path = Path.Combine(fixture.Root, "中文 (记录) #1.md");
        PaperMarkdownExportService.Export(paper, fixture.Images, path);
        Require(File.ReadAllText(path) == paper.Content, "Markdown text changed.");
        Require(!File.ReadAllBytes(path).Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }), "Unexpected BOM.");
        Require(Directory.GetDirectories(fixture.Root).Length == 0, "Text-only export created an asset directory.");
        paper.Content = "";
        PaperMarkdownExportService.Export(paper, fixture.Images, path);
        Require(new FileInfo(path).Length == 0, "Empty note must export an empty file.");
        Require(PaperMarkdownExportService.SuggestedFileName("中文:题目/问号?") == "中文_题目_问号_.md", "Invalid filename characters.");
        Require(PaperMarkdownExportService.SuggestedFileName("CON.txt") == "_CON.txt.md", "Reserved device filename.");
        Require(PaperMarkdownExportService.SuggestedFileName(" . ") == "Dodo.md", "Empty filename fallback.");
        Require(PaperMarkdownExportService.SuggestedFileName(new string('中', 99) + "😀") == new string('中', 99) + ".md", "Truncated Unicode surrogate.");
        Throws<IOException>(() => PaperMarkdownExportService.Export(paper, fixture.Images, Path.Combine(fixture.Root, "wrong.txt")));
        paper.BodyProviderId = "example.plugin";
        Require(!PaperMarkdownExportService.CanExport(paper), "Plugin body exposed as built-in Markdown.");
    }

    private static void Todos()
    {
        using var fixture = new Fixture();
        var paper = new PaperData
        {
            Type = PaperTypes.Todo,
            Items =
            [
                new() { Text = "已完成", Done = true, Order = 2 },
                new() { Text = "[链接](x) *原样*\n第二行", Order = 1 },
                new() { Text = "", Order = 3 }
            ]
        };
        var path = Path.Combine(fixture.Root, "待办.md");
        PaperMarkdownExportService.Export(paper, fixture.Images, path);
        Require(File.ReadAllText(path) == "- [ ] \\[链接\\]\\(x\\) \\*原样\\*\n      第二行\n- [x] 已完成\n- [ ] \n", "Task state, ordering or text changed.");
        Require(paper.Items[0].Done && paper.Items[0].Order == 2, "Export mutated source tasks.");
        paper.Items.Clear();
        PaperMarkdownExportService.Export(paper, fixture.Images, path);
        Require(new FileInfo(path).Length == 0, "Empty todo list must export an empty file.");
    }

    private static void PortableImages()
    {
        using var fixture = new Fixture();
        var asset = fixture.Images.ImportBitmapSource("note", BitmapSource.Create(1, 1, 96, 96,
            PixelFormats.Bgra32, null, new byte[] { 20, 40, 60, 255 }, 4));
        Require(fixture.Images.TryGetEncodedImageBytes(asset.Id, out _, out var expected), "Missing fixture image.");
        var reference = MarkdownImageReferences.CreateReference(asset.Id);
        var paper = new PaperData { Id = "note", Type = PaperTypes.Note, Content = reference + "\n\n" + reference + "\n\n```\n" + reference + "\n```\n" };
        var directory = Path.Combine(fixture.Root, "可移动 目录 (#)");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "同名 # (笔记).md");
        PaperMarkdownExportService.Export(paper, fixture.Images, path);
        var firstText = File.ReadAllText(path);
        var references = Regex.Matches(firstText, @"\./dodo-assets-[a-f0-9]+/[^)\s]+").Select(match => match.Value).ToArray();
        Require(references.Length == 2 && references[0] == references[1], "Duplicate images must share one exported asset.");
        Require(firstText.Contains("```\n" + reference + "\n```"), "Code image literal changed.");
        Require(File.ReadAllBytes(Path.Combine(directory, references[0])).SequenceEqual(expected), "Exported image bytes changed.");
        File.Copy(path, Path.Combine(directory, "first.md"));
        PaperMarkdownExportService.Export(paper, fixture.Images, path);
        Require(File.ReadAllText(path) != firstText, "Repeated export reused its previous image generation.");
        Require(File.ReadAllBytes(Path.Combine(directory, references[0])).SequenceEqual(expected), "Repeated export damaged earlier assets.");
        var moved = Path.Combine(fixture.Root, "moved");
        Directory.Move(directory, moved);
        Require(File.ReadAllBytes(Path.Combine(moved, references[0])).SequenceEqual(expected), "Relative image failed after moving directory.");
        Require(File.ReadAllText(Path.Combine(moved, "first.md")) == firstText, "Moving export changed its document.");
        Require(paper.Content.Contains("i:"), "Export rewrote source image references.");
    }

    private static void FailureSafety()
    {
        using var fixture = new Fixture();
        var destination = Path.Combine(fixture.Root, "existing.md");
        File.WriteAllText(destination, "original");
        File.WriteAllText(destination + ".tmp", "user temporary file");
        var asset = fixture.Images.ImportBitmapSource("note", BitmapSource.Create(1, 1, 96, 96,
            PixelFormats.Bgra32, null, new byte[] { 10, 20, 30, 255 }, 4));
        var paper = new PaperData { Id = "note", Type = PaperTypes.Note,
            Content = MarkdownImageReferences.CreateReference(asset.Id) + "\n\n" + MarkdownImageReferences.CreateReference("999999") };
        Throws<InvalidDataException>(() => PaperMarkdownExportService.Export(paper, fixture.Images, destination));
        Require(File.ReadAllText(destination) == "original", "Missing image replaced the old document.");
        Require(File.ReadAllText(destination + ".tmp") == "user temporary file", "A user .tmp file was overwritten.");
        Require(!Directory.EnumerateFileSystemEntries(fixture.Root).Any(path => Path.GetFileName(path).StartsWith("dodo-assets-")), "Failed export left partial image assets.");
        paper.Content = "replacement";
        File.SetAttributes(destination, FileAttributes.ReadOnly);
        try { Throws<UnauthorizedAccessException>(() => PaperMarkdownExportService.Export(paper, fixture.Images, destination)); }
        finally { File.SetAttributes(destination, FileAttributes.Normal); }
        Require(File.ReadAllText(destination) == "original", "Read-only target was modified.");
        Require(!Directory.EnumerateFiles(fixture.Root, ".dodo-export-*").Any(), "Failed export left its temp file.");
        var existingAssets = Path.Combine(fixture.Root, "existing-assets");
        Directory.CreateDirectory(existingAssets);
        var sentinel = Path.Combine(existingAssets, "user.txt");
        File.WriteAllText(sentinel, "preserve");
        Throws<IOException>(() => fixture.Images.ConvertMarkdownForExport("note", MarkdownImageReferences.CreateReference(asset.Id),
            existingAssets, fixture.Root, out _));
        Require(File.ReadAllText(sentinel) == "preserve", "Shared image conversion deleted a pre-existing directory.");
    }

    private static void UnwritableDirectory()
    {
        using var fixture = new Fixture();
        var directory = Directory.CreateDirectory(Path.Combine(fixture.Root, "denied"));
        var original = directory.GetAccessControl();
        var denied = directory.GetAccessControl();
        var identity = WindowsIdentity.GetCurrent().User!;
        denied.AddAccessRule(new FileSystemAccessRule(identity,
            FileSystemRights.CreateFiles | FileSystemRights.CreateDirectories, AccessControlType.Deny));
        directory.SetAccessControl(denied);
        try
        {
            Throws<UnauthorizedAccessException>(() => PaperMarkdownExportService.Export(
                new PaperData { Type = PaperTypes.Note, Content = "test" }, fixture.Images, Path.Combine(directory.FullName, "note.md")));
            Require(!Directory.EnumerateFileSystemEntries(directory.FullName).Any(), "Unwritable export produced files.");
        }
        finally { directory.SetAccessControl(original); }
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Dodo.MarkdownExportChecks", Guid.NewGuid().ToString("N")));
        internal NoteImageStore Images { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            Images = new NoteImageStore(Path.Combine(Root, "images.lmdb"));
            Images.Load();
        }
        public void Dispose()
        {
            Images.Dispose();
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Dodo.MarkdownExportChecks")) + Path.DirectorySeparatorChar;
            Require(Root.StartsWith(allowed, StringComparison.OrdinalIgnoreCase), "Unsafe test cleanup path.");
            // Only the isolated fixture created above is removed; no application data or UI is used.
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories)) File.Delete(file);
            foreach (var directory in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length)) Directory.Delete(directory);
            Directory.Delete(Root);
        }
    }
}
