using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace PaperTodo;

public sealed partial class AppController
{
    private CancellationTokenSource? _backupCancellation;

    private UIElement BuildBackupSettingsSection()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        panel.Children.Add(SettingsSectionLabel(BackupText("备份与恢复", "Backup and restore", "バックアップと復元", "백업 및 복원")));
        panel.Children.Add(new TextBlock
        {
            Text = BackupText(
                "完整备份纸片、图片、设置、宿主管理的插件状态和自定义资源。插件程序及其自行保存的文件、外部链接文件不包含在内。恢复会替换当前数据并退出；原数据保留在 restore-history。",
                "Back up papers, images, settings, host-managed plugin state and custom resources. Plugin programs, plugin-owned storage and externally linked files are excluded. Restore replaces current data and exits; originals remain in restore-history.",
                "紙片・画像・設定・ホスト管理のプラグイン状態・カスタム素材を保存します。プラグイン本体・独自保存データ・外部リンク先は含みません。復元は現在のデータを置換して終了します。元データは restore-history に残ります。",
                "페이퍼, 이미지, 설정, 호스트 관리 플러그인 상태 및 사용자 리소스를 백업합니다. 플러그인 프로그램·자체 저장 데이터·외부 연결 파일은 제외됩니다. 복원하면 현재 데이터를 교체하고 종료합니다. 원본은 restore-history에 보존됩니다."),
            TextWrapping = TextWrapping.Wrap,
            Foreground = Theme.WeakTextBrush,
            FontSize = AppTypography.Scale(11),
            LineHeight = AppTypography.Scale(17),
            Margin = new Thickness(0, 4, 0, 8)
        });
        var buttons = new WrapPanel();
        var backup = SettingsTextButton(BackupText("导出完整备份…", "Export full backup…", "完全バックアップ…", "전체 백업 내보내기…"));
        backup.Margin = new Thickness(0, 0, 8, 6);
        backup.Padding = new Thickness(12, 0, 12, 0);
        backup.MinHeight = AppTypography.FitChrome(32);
        backup.Click += (_, _) => CreateFullBackup();
        var restore = SettingsTextButton(BackupText("恢复备份…", "Restore backup…", "復元…", "백업 복원…"));
        restore.Margin = new Thickness(0, 0, 0, 6);
        restore.Padding = new Thickness(12, 0, 12, 0);
        restore.MinHeight = AppTypography.FitChrome(32);
        restore.Click += (_, _) => RestoreFullBackup();
        buttons.Children.Add(backup);
        buttons.Children.Add(restore);
        panel.Children.Add(buttons);
        return panel;
    }

    private string BackupText(string zh, string en, string ja, string ko) => SettingsSidebarLocalized(zh, en, ja, ko);

    private bool FlushForFullBackup()
    {
        CommitSettingsExternalMarkdownEditor(saveImmediately: false);
        foreach (var window in _windows.Values) window.CommitPendingMarkdownContentForSave();
        return TrySaveNow(sync: true);
    }

    private async void CreateFullBackup()
    {
        if (_backupCancellation != null || IsExiting || _exitRequested) return;
        var dialog = new SaveFileDialog
        {
            Filter = BackupFileFilter(), DefaultExt = ".zip", AddExtension = true,
            FileName = "Dodo-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip"
        };
        if (dialog.ShowDialog(_settingsWindow) != true) return;
        using var cancellation = new CancellationTokenSource();
        _backupCancellation = cancellation;
        BackupProgressWindow? progress = null;
        try
        {
            progress = CreateBackupProgress(cancellation);
            progress.SetStage(BackupStageText(BackupStage.Snapshot));
            progress.Show();
            await Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            cancellation.Token.ThrowIfCancellationRequested();
            if (IsExiting || _exitRequested) return;
            var service = new BackupService(AppPaths.DataDirectory);
            service.ValidateArchiveDestination(dialog.FileName);
            if (!FlushForFullBackup()) return;
            using var snapshot = service.Capture(_store, _imageStore, _paperBodyPlugins.DataStore);
            cancellation.Token.ThrowIfCancellationRequested();
            var reporter = new Progress<BackupStage>(stage => progress.SetStage(BackupStageText(stage)));
            await Task.Run(() => service.CreateArchive(snapshot, dialog.FileName, cancellation.Token, reporter));
            progress.Finish();
            if (IsExiting || _exitRequested) return;
            var open = MessageBox.Show(_settingsWindow,
                BackupText("备份已保存。是否打开所在文件夹？", "Backup saved. Open its folder?", "保存しました。保存先フォルダーを開きますか？", "백업을 저장했습니다. 폴더를 열까요?") + "\n" + dialog.FileName,
                "Dodo", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No);
            if (open == MessageBoxResult.Yes)
            {
                try { Process.Start(new ProcessStartInfo(Path.GetDirectoryName(dialog.FileName)!) { UseShellExecute = true }); }
                catch (Exception ex)
                {
                    MessageBox.Show(_settingsWindow,
                        BackupText("备份已保存，但无法打开文件夹。", "The backup was saved, but its folder could not be opened.", "保存しましたが、フォルダーを開けませんでした。", "백업은 저장했지만 폴더를 열 수 없습니다.") + "\n\n" + ex.Message,
                        "Dodo", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { if (!IsExiting && !_exitRequested) ShowBackupFailure(ex); }
        finally
        {
            progress?.Finish();
            _backupCancellation = null;
        }
    }

    private async void RestoreFullBackup()
    {
        if (_backupCancellation != null || IsExiting || _exitRequested) return;
        var dialog = new OpenFileDialog { Filter = BackupFileFilter(), CheckFileExists = true };
        if (dialog.ShowDialog(_settingsWindow) != true) return;
        using var cancellation = new CancellationTokenSource();
        _backupCancellation = cancellation;
        BackupProgressWindow? progress = null;
        var stopped = false;
        try
        {
            progress = CreateBackupProgress(cancellation);
            var service = new BackupService(AppPaths.DataDirectory);
            progress.SetStage(BackupStageText(BackupStage.Extract));
            progress.Show();
            var reporter = new Progress<BackupStage>(stage => progress.SetStage(BackupStageText(stage)));
            using var prepared = await Task.Run(() => service.Prepare(dialog.FileName, cancellation.Token, reporter));
            cancellation.Token.ThrowIfCancellationRequested();
            progress.Finish();
            if (IsExiting || _exitRequested) return;
            var manifest = prepared.Manifest;
            var summary = BackupText(
                "已验证备份。恢复将替换当前纸片、图片、设置和插件状态，不会合并。应用随后退出，请重新打开 Dodo。原数据将保留在数据目录的 restore-history。",
                "Backup verified. Restore replaces papers, images, settings and plugin state; it does not merge. Dodo will exit; reopen it afterwards. Originals are preserved in restore-history in the data directory.",
                "検証済みです。紙片・画像・設定・プラグイン状態を置換します（結合しません）。終了後に Dodo を開き直してください。元データはデータフォルダーの restore-history に保存されます。",
                "백업 검증 완료. 페이퍼·이미지·설정·플러그인 상태를 병합하지 않고 교체합니다. 종료 후 Dodo를 다시 여세요. 원본은 데이터 폴더의 restore-history에 보존됩니다.") +
                $"\n\n{dialog.FileName}\n{manifest.CreatedUtc.ToLocalTime():g}\n" +
                BackupText("纸片 / 图片 / 文件", "Papers / images / files", "紙片 / 画像 / ファイル", "페이퍼 / 이미지 / 파일") +
                $": {prepared.PaperCount} / {prepared.ImageCount} / {manifest.Files.Count}\n\n" +
                string.Join("\n", manifest.Files.Take(25).Select(f => f.Path)) +
                (manifest.Files.Count > 25 ? "\n…" : "") + "\n\n" +
                BackupText("需要安装的插件：", "Plugins required:", "必要なプラグイン：", "필요한 플러그인:") + "\n" +
                (manifest.RequiredProviders.Length == 0 ? "—" : string.Join(", ", manifest.RequiredProviders));
            if (MessageBox.Show(_settingsWindow, summary,
                BackupText("确认替换并退出", "Replace data and exit?", "置換して終了しますか", "데이터를 교체하고 종료할까요?"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            if (IsExiting || _exitRequested || !FlushForFullBackup()) return;
            _paperBodyPlugins.DataStore.WithBackupSnapshot(() => { });
            _lifecycleState = AppLifecycleState.Exiting;
            stopped = true;
            _store.SuppressFurtherWrites();
            _paperBodyPlugins.DataStore.SuppressFinalFlushOnDispose();
            DisposeRuntimeResources();
            _lifecycleState = AppLifecycleState.Disposed;
            var originalPath = service.Restore(prepared);
            MessageBox.Show(BackupText("恢复完成。请重新打开 Dodo。原数据：", "Restore complete. Reopen Dodo. Original data:", "復元完了。Dodo を開き直してください。元データ：", "복원이 완료되었습니다. Dodo를 다시 여세요. 원본 데이터:") + "\n" + originalPath,
                "Dodo", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested && !stopped) { }
        catch (Exception ex)
        {
            if (stopped)
                MessageBox.Show(BackupText("恢复失败。原数据副本保留在 restore-history；重启时会先完成必要的回滚。", "Restore failed. Originals remain in restore-history; startup completes any required rollback.", "復元に失敗しました。元データは restore-history に残り、次回起動時に必要なロールバックを完了します。", "복원 실패. 원본은 restore-history에 보존되며 다음 시작 시 필요한 롤백을 완료합니다.") + "\n\n" + ex.Message, "Dodo", MessageBoxButton.OK, MessageBoxImage.Error);
            else if (!IsExiting && !_exitRequested) ShowBackupFailure(ex);
        }
        finally
        {
            progress?.Finish();
            _backupCancellation = null;
            if (stopped) Application.Current.Shutdown();
        }
    }

    private BackupProgressWindow CreateBackupProgress(CancellationTokenSource cancellation) => new(
        _settingsWindow, cancellation,
        BackupText("备份与恢复", "Backup and restore", "バックアップと復元", "백업 및 복원"),
        BackupText("取消", "Cancel", "キャンセル", "취소"),
        BackupText("正在取消…", "Cancelling…", "キャンセル中…", "취소 중…"));

    private string BackupStageText(BackupStage stage) => stage switch
    {
        BackupStage.Snapshot => BackupText("正在准备一致快照…", "Preparing snapshot…", "スナップショットを準備中…", "스냅샷 준비 중…"),
        BackupStage.Extract => BackupText("正在解压备份…", "Extracting backup…", "バックアップを展開中…", "백업 압축 해제 중…"),
        BackupStage.Validate => BackupText("正在校验数据…", "Verifying data…", "データを検証中…", "데이터 확인 중…"),
        BackupStage.Archive => BackupText("正在生成备份…", "Creating backup…", "バックアップを作成中…", "백업 생성 중…"),
        _ => "…"
    };

    private string BackupFileFilter() => BackupText("Dodo 备份", "Dodo backup", "Dodo バックアップ", "Dodo 백업") + " (*.zip)|*.zip";

    private void ShowBackupFailure(Exception ex) => MessageBox.Show(_settingsWindow,
        BackupText("操作未完成，当前数据未替换。", "The operation did not complete. Current data was not replaced.", "操作は完了していません。現在のデータは置換されていません。", "작업이 완료되지 않았습니다. 현재 데이터는 교체되지 않았습니다.") + "\n\n" + ex.Message,
        "Dodo", MessageBoxButton.OK, MessageBoxImage.Error);
}
