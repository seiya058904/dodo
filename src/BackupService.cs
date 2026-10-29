using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PaperTodo;

/// <summary>Portable, versioned snapshots of host-owned data. Never copies plugin code or
/// plugin-owned databases. All live capture is coordinated through the existing store owners.</summary>
internal sealed class BackupService
{
    private const string ManifestName = "dodo-backup.json";
    private const string HistoryName = "restore-history";
    private const long MaximumArchiveBytes = 1024L * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private static readonly HashSet<string> RootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "data.json", "data.backup.json", "note-assets.lmdb", "paper-background.json",
        "dodo.png", "dodo.jpg", "dodo.jpeg", "dodo.ttf", "dodo.otf", "dodo.wav", "dodo.ico",
        "dodo_bold.ttf", "dodo_bold.otf", "dodo-bold.ttf", "dodo-bold.otf"
    };
    private readonly string _root;
    private readonly Action<int>? _restoreFault;

    internal BackupService(string root, Action<int>? restoreFault = null)
    {
        _root = Path.GetFullPath(root);
        _restoreFault = restoreFault;
    }

    internal sealed class FileRecord
    {
        public string Path { get; set; } = "";
        public long Length { get; set; }
        public string Sha256 { get; set; } = "";
    }

    internal sealed class Manifest
    {
        public string Format { get; set; } = "dodo-backup";
        public int FormatVersion { get; set; } = 1;
        public string ApplicationVersion { get; set; } = "0.1.0";
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public List<FileRecord> Files { get; set; } = [];
        public string[] RequiredProviders { get; set; } = [];
    }

    internal sealed class PreparedBackup : IDisposable
    {
        internal PreparedBackup(string directory, Manifest manifest, int papers, int images)
        { Directory = directory; Manifest = manifest; PaperCount = papers; ImageCount = images; }
        internal string Directory { get; }
        internal Manifest Manifest { get; }
        internal int PaperCount { get; }
        internal int ImageCount { get; }
        public void Dispose() => DeleteOwnedStaging(Directory);
    }

    private sealed class RestoreJournal
    {
        public string Status { get; set; } = "applying";
        public List<string> Targets { get; set; } = [];
        public List<FileRecord> Originals { get; set; } = [];
    }

    internal void Create(string archivePath, StateStore stateStore, NoteImageStore images,
        PaperBodyPluginDataStore plugins)
    {
        if (!string.Equals(Path.GetDirectoryName(stateStore.FilePath), _root, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(images.FilePath), _root, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(plugins.DataRoot, Path.Combine(_root, "plugins", "data"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Backup stores must share the Dodo data directory.");
        archivePath = Path.GetFullPath(archivePath);
        if (archivePath.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Save the ZIP outside the Dodo data directory.");
        var stage = CreateStaging();
        var temporaryZip = archivePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            stateStore.WithBackupSnapshot(() => plugins.WithBackupSnapshot(() =>
                images.WithBackupSnapshot(() =>
                {
                    var files = EnumerateManagedFiles().ToList();
                    if (files.Count > 9999 || files.Sum(p => new FileInfo(Resolve(_root, p)).Length) > MaximumArchiveBytes)
                        throw new InvalidDataException("The backup exceeds the supported size or entry count.");
                    foreach (var relative in files)
                        CopyDurably(Resolve(_root, relative), Resolve(stage, relative));
                })));
            var (state, _) = ValidatePayload(stage);
            var manifest = new Manifest
            {
                Files = EnumerateManagedFiles(stage).Select(p => Describe(stage, p)).ToList(),
                RequiredProviders = RequiredProviders(state, stage)
            };
            var manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
            if (manifest.Files.Sum(f => f.Length) + manifestBytes.Length > MaximumArchiveBytes || manifestBytes.Length > 4 * 1024 * 1024)
                throw new InvalidDataException("The backup exceeds the supported archive size.");
            File.WriteAllBytes(Path.Combine(stage, ManifestName), manifestBytes);
            using (var stream = new FileStream(temporaryZip, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (var item in manifest.Files)
                        zip.CreateEntryFromFile(Resolve(stage, item.Path), item.Path, CompressionLevel.Optimal);
                    zip.CreateEntryFromFile(Path.Combine(stage, ManifestName), ManifestName);
                }
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryZip, archivePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryZip)) File.Delete(temporaryZip);
            DeleteOwnedStaging(stage);
        }
    }

    internal PreparedBackup Prepare(string archivePath)
    {
        var stage = CreateStaging();
        try
        {
            using var zip = ZipFile.OpenRead(archivePath);
            if (zip.Entries.Count is < 2 or > 10000)
                throw new InvalidDataException("Invalid backup entry count.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                if (!names.Add(entry.FullName) || (entry.FullName != ManifestName && !IsManagedPath(entry.FullName)) ||
                    ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000)
                    throw new InvalidDataException($"Unsafe or unsupported backup path: {entry.FullName}");
                total = checked(total + entry.Length);
                if (entry.Length < 0 || total > MaximumArchiveBytes)
                    throw new InvalidDataException("The backup exceeds the 1 GiB safety limit.");
                if (entry.FullName == ManifestName && entry.Length > 4 * 1024 * 1024)
                    throw new InvalidDataException("The backup manifest is too large.");
                var target = Resolve(stage, entry.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var input = entry.Open();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                CopyBounded(input, output, entry.Length);
            }
            var manifest = JsonSerializer.Deserialize<Manifest>(
                File.ReadAllText(Path.Combine(stage, ManifestName)), JsonOptions)
                ?? throw new InvalidDataException("Missing backup manifest.");
            if (manifest.Format != "dodo-backup" || manifest.FormatVersion != 1 || manifest.Files == null ||
                !Version.TryParse(manifest.ApplicationVersion, out var version) || version > new Version(0, 1, 0))
                throw new InvalidDataException("This backup requires a newer or different application version.");
            ValidateManifest(stage, manifest);
            if (manifest.Files.Count + 1 != zip.Entries.Count)
                throw new InvalidDataException("Archive contents differ from the manifest.");
            var (state, count) = ValidatePayload(stage);
            manifest.RequiredProviders = RequiredProviders(state, stage);
            return new PreparedBackup(stage, manifest, state.Papers.Count, count);
        }
        catch { DeleteOwnedStaging(stage); throw; }
    }

    // The caller must stop all runtime writers and close LMDB before applying. An interrupted
    // journal is rolled back before StateStore is constructed on the next application startup.
    internal string Restore(PreparedBackup backup)
    {
        RecoverInterruptedRestore(_root);
        ValidateManifest(backup.Directory, backup.Manifest);
        _ = ValidatePayload(backup.Directory);
        Directory.CreateDirectory(_root);
        var history = Path.Combine(_root, HistoryName, DateTime.UtcNow.ToString("yyyyMMddTHHmmss") + "-" + Guid.NewGuid().ToString("N"));
        EnsureNoReparsePoints(history);
        var originalRoot = Path.Combine(history, "original");
        Directory.CreateDirectory(originalRoot);
        var originals = EnumerateManagedFiles().ToList();
        foreach (var relative in originals)
            CopyDurably(Resolve(_root, relative), Resolve(originalRoot, relative));
        var journal = new RestoreJournal
        {
            Originals = originals.Select(p => Describe(originalRoot, p)).ToList(),
            Targets = originals.Concat(backup.Manifest.Files.Select(f => f.Path)).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };
        var journalPath = Path.Combine(history, "transaction.json");
        WriteJournal(journalPath, journal);
        try
        {
            var incoming = backup.Manifest.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
            var index = 0;
            foreach (var relative in journal.Targets)
            {
                var target = Resolve(_root, relative);
                if (incoming.ContainsKey(relative)) CopyDurably(Resolve(backup.Directory, relative), target, overwrite: true);
                else if (File.Exists(target))
                {
                    var retired = Resolve(Path.Combine(history, "removed"), relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(retired)!);
                    File.Move(target, retired);
                }
                _restoreFault?.Invoke(++index);
            }
            _ = ValidatePayload(_root);
            journal.Status = "completed";
            WriteJournal(journalPath, journal);
            return originalRoot;
        }
        catch
        {
            Rollback(_root, history, journal);
            throw;
        }
    }

    internal static void RecoverInterruptedRestore() => RecoverInterruptedRestore(AppPaths.DataDirectory);

    internal static void RecoverInterruptedRestore(string root)
    {
        var historyRoot = Path.Combine(root, HistoryName);
        EnsureNoReparsePoints(historyRoot);
        if (!Directory.Exists(historyRoot)) return;
        foreach (var history in Directory.EnumerateDirectories(historyRoot))
        {
            EnsureNoReparsePoints(history);
            var path = Path.Combine(history, "transaction.json");
            if (!File.Exists(path)) continue;
            EnsureNoReparsePoints(path);
            var journal = JsonSerializer.Deserialize<RestoreJournal>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidDataException("The restore journal cannot be read.");
            if (journal.Status == "applying") Rollback(root, history, journal);
            else if (journal.Status is not ("completed" or "rolled-back"))
                throw new InvalidDataException("Unknown restore journal status.");
        }
    }

    private static void Rollback(string root, string history, RestoreJournal journal)
    {
        var originalRoot = Path.Combine(history, "original");
        var originals = journal.Originals.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var original in journal.Originals) Verify(originalRoot, original);
        foreach (var relative in journal.Targets)
        {
            if (!IsManagedPath(relative)) throw new InvalidDataException("Unsafe restore journal path.");
            var target = Resolve(root, relative);
            if (originals.ContainsKey(relative)) CopyDurably(Resolve(originalRoot, relative), target, overwrite: true);
            else if (File.Exists(target))
            {
                var failed = Resolve(Path.Combine(history, "interrupted-" + Guid.NewGuid().ToString("N")), relative);
                Directory.CreateDirectory(Path.GetDirectoryName(failed)!);
                File.Move(target, failed);
            }
        }
        journal.Status = "rolled-back";
        WriteJournal(Path.Combine(history, "transaction.json"), journal);
    }

    private static void WriteJournal(string path, RestoreJournal journal) =>
        DurableAtomicFileWriter.Shared.Write(path, JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions));

    private static (AppState State, int Images) ValidatePayload(string directory)
    {
        var state = StateStore.ValidateBackupState(Resolve(directory, "data.json"));
        var states = new List<AppState> { state };
        var backup = Resolve(directory, "data.backup.json");
        if (File.Exists(backup)) states.Add(StateStore.ValidateBackupState(backup));
        var plugins = Path.Combine(directory, "plugins", "data");
        EnsureNoReparsePoints(plugins);
        if (Directory.Exists(plugins))
            foreach (var file in Directory.EnumerateFiles(plugins, "*.json"))
            {
                EnsureNoReparsePoints(file);
                PaperBodyPluginDataStore.ValidateBackupDocument(file);
            }
        var background = Resolve(directory, "paper-background.json");
        if (File.Exists(background))
        {
            using var parsed = JsonDocument.Parse(File.ReadAllText(background));
            if (parsed.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Invalid background preferences.");
        }
        return (state, NoteImageStore.ValidateBackupSnapshot(Resolve(directory, "note-assets.lmdb"), states));
    }

    private static string[] RequiredProviders(AppState state, string directory) =>
        state.Papers.Select(p => p.BodyProviderId).Concat(
            EnumerateManagedFiles(directory).Where(p => p.StartsWith("plugins/data/", StringComparison.Ordinal))
                .Select(p => Path.GetFileNameWithoutExtension(p)))
        .Where(p => !string.IsNullOrWhiteSpace(p) && p != PaperBodyProviderIds.Markdown)
        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private IEnumerable<string> EnumerateManagedFiles() => EnumerateManagedFiles(_root);

    private static IEnumerable<string> EnumerateManagedFiles(string root)
    {
        EnsureNoReparsePoints(root);
        foreach (var name in RootFiles)
        {
            var path = Resolve(root, name);
            if (File.Exists(path)) yield return name;
        }
        var data = Path.Combine(root, "plugins", "data");
        EnsureNoReparsePoints(data);
        if (!Directory.Exists(data)) yield break;
        foreach (var file in Directory.EnumerateFiles(data, "*.json", SearchOption.TopDirectoryOnly))
        {
            var relative = "plugins/data/" + Path.GetFileName(file);
            if (!IsManagedPath(relative)) throw new InvalidDataException("Invalid plugin data filename.");
            EnsureNoReparsePoints(file);
            yield return relative;
        }
    }

    private static bool IsManagedPath(string path) => RootFiles.Contains(path) ||
        Regex.IsMatch(path, @"\Aplugins/data/[A-Za-z0-9][A-Za-z0-9._-]{0,199}\.json\z", RegexOptions.CultureInvariant) &&
        !Regex.IsMatch(Path.GetFileName(path), @"\A(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string Resolve(string root, string relative)
    {
        if (relative != ManifestName && !IsManagedPath(relative)) throw new InvalidDataException("Unsafe backup path.");
        var path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Backup path escapes its directory.");
        EnsureNoReparsePoints(path);
        return path;
    }

    private static void EnsureNoReparsePoints(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Backup paths cannot contain symbolic links or junctions.");
    }

    private static FileRecord Describe(string root, string relative)
    {
        var path = Resolve(root, relative);
        using var stream = File.OpenRead(path);
        return new FileRecord { Path = relative, Length = stream.Length, Sha256 = Convert.ToHexString(SHA256.HashData(stream)) };
    }

    private static void Verify(string root, FileRecord expected)
    {
        var actual = Describe(root, expected.Path);
        if (actual.Length != expected.Length || !string.Equals(actual.Sha256, expected.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Backup integrity check failed: {expected.Path}");
    }

    private static void ValidateManifest(string root, Manifest manifest)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Files)
        {
            if (entry == null || !IsManagedPath(entry.Path) || !paths.Add(entry.Path))
                throw new InvalidDataException("Invalid or duplicate manifest path.");
            Verify(root, entry);
        }
        if (!paths.Contains("data.json")) throw new InvalidDataException("The backup has no application state.");
    }

    private static void CopyDurably(string source, string target, bool overwrite = false)
    {
        EnsureNoReparsePoints(source);
        EnsureNoReparsePoints(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var temp = target + ".backup-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            // Live LMDB holds a write-capable Windows handle even between transactions. The
            // store owner's snapshot gate guarantees stability while permitting that handle.
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { input.CopyTo(output); output.Flush(flushToDisk: true); }
            File.Move(temp, target, overwrite);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private static void CopyBounded(Stream input, Stream output, long expected)
    {
        var buffer = new byte[81920];
        long copied = 0;
        int count;
        while ((count = input.Read(buffer)) > 0)
        {
            copied += count;
            if (copied > expected) throw new InvalidDataException("Invalid ZIP entry length.");
            output.Write(buffer, 0, count);
        }
        if (copied != expected) throw new InvalidDataException("Truncated ZIP entry.");
    }

    private static string CreateStaging()
    {
        var path = Path.Combine(Path.GetTempPath(), "DodoBackup-" + Guid.NewGuid().ToString("N"));
        EnsureNoReparsePoints(path);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteOwnedStaging(string directory)
    {
        // Only private, randomly created staging directories are passed here; never a user path.
        if (!Path.GetFileName(directory).StartsWith("DodoBackup-", StringComparison.Ordinal)) return;
        try
        {
            EnsureNoReparsePoints(directory);
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            { EnsureNoReparsePoints(file); File.Delete(file); }
            foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories).OrderByDescending(p => p.Length))
                Directory.Delete(child);
            Directory.Delete(directory);
        }
        catch { /* A locked temporary file must not hide the original operation result. */ }
    }
}
