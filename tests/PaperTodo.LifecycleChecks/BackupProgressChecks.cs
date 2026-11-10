using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PaperTodo;

internal static class BackupProgressChecks
{
    internal static async Task Run()
    {
        var root = AppPaths.DataDirectory;
        var store = new StateStore(root, DurableAtomicFileWriter.Shared);
        using var images = new NoteImageStore(Path.Combine(root, "note-assets.lmdb"));
        images.Load();
        var references = new List<string>();
        var random = new Random(2901);
        for (var i = 0; i < 4; i++)
        {
            var pixels = new byte[1024 * 1024 * 4];
            random.NextBytes(pixels);
            var bitmap = BitmapSource.Create(1024, 1024, 96, 96, PixelFormats.Bgr32, null, pixels, 1024 * 4);
            bitmap.Freeze();
            var asset = images.ImportBitmapSource("backup-note", bitmap);
            references.Add(MarkdownImageReferences.CreateReference(asset.Id));
        }
        var state = new AppState { Theme = "light", Papers = [new PaperData { Id = "backup-note", Type = PaperTypes.Note, Content = string.Join("\n", references) }] };
        store.SaveJsonSync(store.SerializeState(state), 1);
        using var plugins = new PaperBodyPluginDataStore(Path.Combine(root, "plugins"));
        using var controller = new AppController(store);
        var service = new BackupService(root);
        var watch = Stopwatch.StartNew();
        using var snapshot = service.Capture(store, images, plugins);
        watch.Stop();
        var bytes = new FileInfo(Path.Combine(snapshot.Directory, "note-assets.lmdb")).Length;
        Console.WriteLine($"BACKUP_SNAPSHOT bytes={bytes} elapsed_ms={watch.ElapsedMilliseconds}");
        using var cancellation = new CancellationTokenSource();
        var window = new BackupProgressWindow(null, cancellation, "Backup progress test", "Cancel", "Cancelling");
        window.SetStage("Preparing snapshot");
        window.Show();
        await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
        var stages = new List<BackupStage>();
        var beats = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => beats++;
        timer.Start();
        var progress = new Progress<BackupStage>(stage => { stages.Add(stage); window.SetStage(stage.ToString()); });
        var archive = Path.Combine(Path.GetTempPath(), "Dodo-backup-ui-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await Task.Run(() => service.CreateArchive(snapshot, archive, cancellation.Token, progress));
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            if (beats < 2 || !stages.SequenceEqual([BackupStage.Validate, BackupStage.Archive]))
                throw new InvalidOperationException($"Progress was not responsive: ticks={beats}, stages={string.Join(',', stages)}");
            using var prepared = await Task.Run(() => service.Prepare(archive));
            if (prepared.ImageCount != 4) throw new InvalidOperationException("Large image backup did not validate");
            var original = File.ReadAllBytes(archive);
            using var cancelSecond = new CancellationTokenSource();
            var cancelWindow = new BackupProgressWindow(null, cancelSecond, "Cancel backup test", "Cancel", "Cancelling");
            cancelWindow.Show();
            try
            {
                var uiCancel = new UiCancelProgress(cancelWindow, BackupStage.Archive);
                try
                {
                    await Task.Run(() => service.CreateArchive(snapshot, archive, cancelSecond.Token, uiCancel));
                    throw new InvalidOperationException("UI cancellation did not stop backup");
                }
                catch (OperationCanceledException) when (cancelSecond.IsCancellationRequested) { }
                if (!original.AsSpan().SequenceEqual(File.ReadAllBytes(archive)))
                    throw new InvalidOperationException("UI cancellation replaced the existing backup");
            }
            finally { cancelWindow.Finish(); }
            Console.WriteLine($"BACKUP_RESPONSIVENESS ticks={beats} images={prepared.ImageCount} ui_cancel=passed");
        }
        finally
        {
            timer.Stop();
            window.Finish();
            if (File.Exists(archive)) File.Delete(archive);
        }
    }

    private sealed class UiCancelProgress(BackupProgressWindow window, BackupStage target) : IProgress<BackupStage>
    {
        public void Report(BackupStage stage)
        {
            if (stage != target) return;
            window.Dispatcher.Invoke(() =>
            {
                var panel = (StackPanel)window.Content;
                var button = panel.Children.OfType<Button>().Single();
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
        }
    }
}
