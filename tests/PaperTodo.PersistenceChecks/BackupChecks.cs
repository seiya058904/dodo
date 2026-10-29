using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PaperTodo;

internal static class BackupChecks
{
    internal static void WriteIntegrationFixture(string root)
    {
        if (Directory.Exists(root)) throw new InvalidOperationException("Fixture destination must not exist.");
        Directory.CreateDirectory(root);
        using var images = new NoteImageStore(Path.Combine(root, "note-assets.lmdb"));
        images.Load();
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 0, 0, 0, 255 }, 8);
        bitmap.Freeze();
        var asset = images.ImportBitmapSource("fixture-image-note", bitmap);
        var state = new AppState
        {
            Theme = "light", McpEnabled = true, McpAllowBlankWrites = true,
            McpAllowFullWrites = true, McpAllowDeletes = true, UseCapsuleMode = false,
            UseDeepCapsuleMode = false, EnableAnimations = false,
            Papers =
            [
                new PaperData { Id = "fixture-image-note", Type = PaperTypes.Note, Title = "图片夹具", IsVisible = false, Content = "# 中文图片验收\n\n" + MarkdownImageReferences.CreateReference(asset.Id) },
                new PaperData { Id = "fixture-clock", Type = PaperTypes.Note, Title = "时钟夹具", IsVisible = false, BodyProviderId = "sample.clock.native" },
                new PaperData { Id = "fixture-focus", Type = PaperTypes.Note, Title = "专注夹具", IsVisible = false, BodyProviderId = "sample.focus-timer.native" }
            ]
        };
        var store = new StateStore(root, DurableAtomicFileWriter.Shared);
        store.SaveJsonSync(store.SerializeState(state), 1);
        Check(store.TryRefreshBackupFromPrimary(), "fixture backup failed");
        using var plugins = new PaperBodyPluginDataStore(Path.Combine(root, "plugins"));
        plugins.SavePaperState("sample.clock.native", "fixture-clock", 1, "{}");
        plugins.SavePaperState("sample.focus-timer.native", "fixture-focus", 3,
            "{\"initialized\":true,\"focusMinutes\":17,\"breakMinutes\":3,\"remainingSeconds\":1020,\"isRunning\":false,\"completedFocusSessions\":7}");
        plugins.WithBackupSnapshot(() => { });
    }

    internal static void WriteRestoredIntegrationFixture(string root)
    {
        root = Path.GetFullPath(root);
        var evidencePath = root + ".restore-chain.json";
        if (Directory.Exists(root) || File.Exists(root) || File.Exists(evidencePath))
            throw new InvalidOperationException("Restored fixture destination and evidence must not exist.");
        using var temporary = new TempDirectory();
        var source = Path.Combine(temporary.Path, "source");
        var archive = Path.Combine(temporary.Path, "fixture.zip");
        WriteIntegrationFixture(source);
        var sourceStore = new StateStore(source, DurableAtomicFileWriter.Shared);
        using (var sourceImages = new NoteImageStore(Path.Combine(source, "note-assets.lmdb")))
        using (var sourcePlugins = new PaperBodyPluginDataStore(Path.Combine(source, "plugins")))
        {
            sourceImages.Load();
            new BackupService(source).Create(archive, sourceStore, sourceImages, sourcePlugins);
        }
        var service = new BackupService(root);
        using var prepared = service.Prepare(archive);
        Check(prepared.PaperCount == 3 && prepared.ImageCount == 1, "restored fixture archive summary changed");
        Check(prepared.Manifest.RequiredProviders.Order().SequenceEqual(
            new[] { "sample.clock.native", "sample.focus-timer.native" }), "native plugin requirements missing");
        var originals = service.Restore(prepared);
        foreach (var file in prepared.Manifest.Files)
        {
            var sourceHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(source, file.Path))));
            var restoredHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, file.Path))));
            Check(sourceHash == file.Sha256 && restoredHash == file.Sha256, "restored fixture hash mismatch: " + file.Path);
        }
        var restored = new StateStore(root, DurableAtomicFileWriter.Shared).Load();
        Check(restored.Theme == "light" && restored.McpEnabled && restored.McpAllowFullWrites &&
            !restored.UseCapsuleMode && !restored.EnableAnimations, "restored application settings changed");
        Check(restored.Papers.Count == 3 && restored.Papers.All(paper => !paper.IsVisible), "restored fixture must remain hidden");
        Check(NoteImageStore.ValidateBackupSnapshot(Path.Combine(root, "note-assets.lmdb"), [restored]) == 1,
            "restored image bytes or references invalid");
        using var plugins = new PaperBodyPluginDataStore(Path.Combine(root, "plugins"));
        var clock = plugins.ReadPaperState("sample.clock.native", "fixture-clock");
        var focus = plugins.ReadPaperState("sample.focus-timer.native", "fixture-focus");
        Check(clock.Version == 1 && clock.Json == "{}", "restored clock state changed");
        using var focusState = JsonDocument.Parse(focus.Json);
        Check(focus.Version == 3 && focusState.RootElement.GetProperty("completedFocusSessions").GetInt32() == 7 &&
            focusState.RootElement.GetProperty("focusMinutes").GetInt32() == 17, "restored focus state changed");
        File.WriteAllText(evidencePath, JsonSerializer.Serialize(new
        {
            status = "passed",
            chain = new[] { "WriteIntegrationFixture", "BackupService.Create", "BackupService.Prepare", "BackupService.Restore", "ReopenAndVerify" },
            restoredProfile = root,
            originalData = originals,
            archiveSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(archive))),
            sourceAndArchive = "Private temporary siblings; removed after verification",
            papers = prepared.PaperCount,
            images = prepared.ImageCount,
            requiredProviders = prepared.Manifest.RequiredProviders,
            checks = new[] { "all restored files match source and manifest SHA256", "core settings and hidden state", "LMDB index, encoded image hashes and references", "native clock and focus per-paper state" },
            files = prepared.Manifest.Files
        }, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }));
    }

    internal static void RoundTrip()
    {
        using var directory = new TempDirectory();
        var source = Path.Combine(directory.Path, "source");
        var zip = Path.Combine(directory.Path, "backup.zip");
        CreateBackup(source, zip);
        var destination = Path.Combine(directory.Path, "restored");
        Directory.CreateDirectory(Path.Combine(destination, "plugins", "data"));
        File.WriteAllText(Path.Combine(destination, "plugins", "data", "stale.json"), "{}");
        var service = new BackupService(destination);
        using var prepared = service.Prepare(zip);
        Check(prepared.PaperCount == 1 && prepared.ImageCount == 1, "summary lost paper or image");
        Check(prepared.Manifest.RequiredProviders.Contains("example.plugin"), "provider requirement missing");
        var originals = service.Restore(prepared);
        Check(File.Exists(Path.Combine(originals, "plugins", "data", "stale.json")), "old state not retained");
        Check(!File.Exists(Path.Combine(destination, "plugins", "data", "stale.json")), "restore merged stale state");
        var reopened = new StateStore(destination, DurableAtomicFileWriter.Shared).Load();
        Check(reopened.Theme == "dark" && reopened.Papers.Single().Content.Contains("i:"), "core state did not reopen");
        using var images = new NoteImageStore(Path.Combine(destination, "note-assets.lmdb"));
        images.Load();
        var imageId = MarkdownImageReferences.CollectImageIds(reopened.Papers.Single().Content).Single();
        Check(images.TryGetEncodedImageBytes(imageId, out _, out var bytes) && bytes.Length > 0, "image did not reopen");
        using var plugins = new PaperBodyPluginDataStore(Path.Combine(destination, "plugins"));
        Check(plugins.ReadRuntimeState("example.plugin").Json.Contains("42"), "runtime state did not reopen");
        Check(plugins.ReadPaperState("example.plugin", "note").Json.Contains("hello"), "paper state did not reopen");
        Check(File.ReadAllText(Path.Combine(destination, "paper-background.json")) == "{\"BlendWithTheme\":false}", "background settings lost");
        Check(File.ReadAllText(Path.Combine(destination, "dodo.wav")) == "resource fixture", "custom resource lost");
        Check(File.ReadAllText(Path.Combine(destination, "dodo-bold.ttf")) == "font fixture", "hyphenated custom bold font lost");
        var secondZip = Path.Combine(directory.Path, "second.zip");
        var store = new StateStore(destination, DurableAtomicFileWriter.Shared);
        new BackupService(destination).Create(secondZip, store, images, plugins);
        using var archive = ZipFile.OpenRead(secondZip);
        Check(!archive.Entries.Any(e => e.FullName.Contains("restore-history")), "backup recursively included restore history");
    }

    internal static void RejectUnsafeArchives()
    {
        using var directory = new TempDirectory();
        var zip = Path.Combine(directory.Path, "backup.zip");
        CreateBackup(Path.Combine(directory.Path, "source"), zip);
        var service = new BackupService(Path.Combine(directory.Path, "destination"));
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Update))
        {
            var entry = archive.GetEntry("data.json")!;
            entry.Delete();
            using var writer = new StreamWriter(archive.CreateEntry("data.json").Open());
            writer.Write("{\"papers\":[]}");
        }
        Throws(() => service.Prepare(zip), "tampered archive accepted");
        var unsafeZip = Path.Combine(directory.Path, "unsafe.zip");
        using (var archive = ZipFile.Open(unsafeZip, ZipArchiveMode.Create))
        {
            archive.CreateEntry("../outside.txt");
            archive.CreateEntry("dodo-backup.json");
        }
        Throws(() => service.Prepare(unsafeZip), "path traversal accepted");
        Check(!File.Exists(Path.Combine(directory.Path, "outside.txt")), "unsafe path extracted");
    }

    internal static void FailedRestoreRollsBack()
    {
        using var directory = new TempDirectory();
        var zip = Path.Combine(directory.Path, "backup.zip");
        CreateBackup(Path.Combine(directory.Path, "source"), zip);
        var root = Path.Combine(directory.Path, "existing");
        var store = new StateStore(root, DurableAtomicFileWriter.Shared);
        store.SaveJsonSync(store.SerializeState(new AppState { Theme = "light", Papers = [] }), 1);
        var original = File.ReadAllBytes(store.FilePath);
        var service = new BackupService(root, index => { if (index == 2) throw new IOException("injected restore failure"); });
        using var prepared = service.Prepare(zip);
        Throws(() => service.Restore(prepared), "fault injection did not fail");
        Check(original.AsSpan().SequenceEqual(File.ReadAllBytes(store.FilePath)), "rollback changed original bytes");
        Check(!File.Exists(Path.Combine(root, "note-assets.lmdb")), "new database survived rollback");
        BackupService.RecoverInterruptedRestore(root);
        Check(new StateStore(root, DurableAtomicFileWriter.Shared).Load().Theme == "light", "rollback did not reopen");
    }

    internal static void InterruptedRestoreRollsBack()
    {
        using var directory = new TempDirectory();
        var root = Path.Combine(directory.Path, "existing");
        var originalRoot = Path.Combine(root, "restore-history", "interrupted-fixture", "original");
        var originalStore = new StateStore(originalRoot, DurableAtomicFileWriter.Shared);
        originalStore.SaveJsonSync(originalStore.SerializeState(new AppState { Theme = "light", Papers = [] }), 1);
        var original = File.ReadAllBytes(originalStore.FilePath);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "data.json"), "{\"papers\":[],\"theme\":\"dark\"}");
        File.WriteAllText(Path.Combine(root, "dodo.wav"), "partially restored resource");
        var journal = new
        {
            status = "applying", targets = new[] { "data.json", "dodo.wav" },
            originals = new[] { new { path = "data.json", length = original.Length, sha256 = Convert.ToHexString(SHA256.HashData(original)) } }
        };
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(originalRoot)!, "transaction.json"), JsonSerializer.Serialize(journal));
        BackupService.RecoverInterruptedRestore(root);
        Check(original.AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(root, "data.json"))), "startup rollback changed original");
        Check(!File.Exists(Path.Combine(root, "dodo.wav")), "startup rollback kept new file");
        BackupService.RecoverInterruptedRestore(root);
        Check(new StateStore(root, DurableAtomicFileWriter.Shared).Load().Theme == "light", "startup rollback not idempotent");
    }

    internal static void RejectIncompatiblePayload()
    {
        using var directory = new TempDirectory();
        var zip = Path.Combine(directory.Path, "backup.zip");
        CreateBackup(Path.Combine(directory.Path, "source"), zip);
        var service = new BackupService(Path.Combine(directory.Path, "destination"));
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        foreach (var variant in new[] { "version", "image", "plugin" })
        {
            var copy = Path.Combine(directory.Path, variant + ".zip");
            File.Copy(zip, copy);
            using (var archive = ZipFile.Open(copy, ZipArchiveMode.Update))
            {
                var manifestEntry = archive.GetEntry("dodo-backup.json")!;
                BackupService.Manifest manifest;
                using (var reader = new StreamReader(manifestEntry.Open()))
                    manifest = JsonSerializer.Deserialize<BackupService.Manifest>(reader.ReadToEnd(), options)!;
                manifestEntry.Delete();
                if (variant == "version") manifest.FormatVersion = 999;
                else if (variant == "image")
                {
                    archive.GetEntry("note-assets.lmdb")!.Delete();
                    manifest.Files.RemoveAll(f => f.Path == "note-assets.lmdb");
                }
                else
                {
                    const string path = "plugins/data/example.plugin.json";
                    var bytes = System.Text.Encoding.UTF8.GetBytes("{\"storageVersion\":999}");
                    archive.GetEntry(path)!.Delete();
                    using (var output = archive.CreateEntry(path).Open()) output.Write(bytes);
                    var record = manifest.Files.Single(f => f.Path == path);
                    record.Length = bytes.Length;
                    record.Sha256 = Convert.ToHexString(SHA256.HashData(bytes));
                }
                using var writer = new StreamWriter(archive.CreateEntry("dodo-backup.json").Open());
                writer.Write(JsonSerializer.Serialize(manifest, options));
            }
            Throws(() => service.Prepare(copy), variant + " invalid payload accepted");
        }
    }

    internal static void SuppressionBlocksLateWrites()
    {
        using var directory = new TempDirectory();
        var store = new StateStore(directory.Path, DurableAtomicFileWriter.Shared);
        var oldJson = store.SerializeState(new AppState { Theme = "dark", Papers = [] });
        store.SaveJsonSync(oldJson, 1);
        store.SuppressFurtherWrites();
        const string restored = "{\"papers\":[],\"theme\":\"light\"}";
        File.WriteAllText(store.FilePath, restored);
        store.SaveJsonSync(oldJson, 99);
        store.SaveJsonAsync(oldJson, 100).GetAwaiter().GetResult();
        Check(!store.TryRefreshBackupFromPrimary(), "suppressed store refreshed backup");
        Check(File.ReadAllText(store.FilePath) == restored, "late save overwrote restored data");
    }

    private static void CreateBackup(string root, string zip)
    {
        Directory.CreateDirectory(root);
        var store = new StateStore(root, DurableAtomicFileWriter.Shared);
        using var images = new NoteImageStore(Path.Combine(root, "note-assets.lmdb"));
        images.Load();
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 0, 0, 255, 255, 0, 255, 0, 255, 255, 0, 0, 255, 0, 0, 0, 255 }, 8);
        bitmap.Freeze();
        var asset = images.ImportBitmapSource("note", bitmap);
        var state = new AppState { Theme = "dark", Papers = [new PaperData { Id = "note", Type = PaperTypes.Note, Content = MarkdownImageReferences.CreateReference(asset.Id) }] };
        store.SaveJsonSync(store.SerializeState(state), 1);
        Check(store.TryRefreshBackupFromPrimary(), "backup source failed");
        using var plugins = new PaperBodyPluginDataStore(Path.Combine(root, "plugins"));
        plugins.SaveRuntimeState("example.plugin", 2, "{\"answer\":42}");
        plugins.SavePaperState("example.plugin", "note", 3, "{\"text\":\"hello\"}");
        File.WriteAllText(Path.Combine(root, "paper-background.json"), "{\"BlendWithTheme\":false}");
        File.WriteAllText(Path.Combine(root, "dodo.wav"), "resource fixture");
        File.WriteAllText(Path.Combine(root, "dodo-bold.ttf"), "font fixture");
        new BackupService(root).Create(zip, store, images, plugins);
    }

    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private static void Throws(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        catch (IOException) { return; }
        throw new InvalidOperationException(message);
    }
}
