using System.Windows;

namespace PaperTodo;

public sealed partial class AppController
{
    private bool _exitRequested;
    private Exception? _lastSaveFailure;
    private long _lastSuccessfulSaveVersion;
    private bool _saveFailurePromptPending;
    private SaveFailureDialog? _saveFailureDialog;
    private long? _dismissedSaveFailureRevision;
    private string? _dismissedSaveFailureSignature;
    private string? _lastSaveFailureJson;
    private string? _dismissedSaveFailureJson;

    private static string SaveFailureSignature(Exception error) =>
        error.GetType().FullName + ":" + error.Message;

    private void RecordSaveSuccess(long version, long revision, bool hasPendingMarkdown = false)
    {
        _lastSuccessfulSaveVersion = Math.Max(_lastSuccessfulSaveVersion, version);
        if (_lastSaveFailure == null) return;
        // An old completion must not clear a newer failure or unsaved editor content.
        if (version != _saveVersion || revision != _stateRevision || hasPendingMarkdown ||
            _windows.Values.Any(window => window.HasPendingMarkdownContentForSave)) return;

        _lastSaveFailure = null;
        _lastSaveFailureJson = null;
        _dismissedSaveFailureRevision = null;
        _dismissedSaveFailureSignature = null;
        _dismissedSaveFailureJson = null;
        RefreshSaveFailureStatus();
        if (!_exitRequested) _saveFailureDialog?.Close();
    }

    private void HandleSaveFailure(Exception error, long? failedVersion = null, string? json = null)
    {
        if (IsExiting || failedVersion is long version &&
            (version != _saveVersion || version <= _lastSuccessfulSaveVersion)) return;

        _lastSaveFailure = error;
        _lastSaveFailureJson = json;
        if (!_exitRequested) ArmSaveRetryIfNeeded(failedVersion);
        RefreshSaveFailureStatus();
        QueueSaveFailurePrompt();
    }

    private bool ShouldPromptSaveFailure => !IsExiting && !_exitRequested &&
        _lastSaveFailure != null &&
        (_dismissedSaveFailureRevision != _stateRevision ||
         _dismissedSaveFailureJson != _lastSaveFailureJson ||
         _dismissedSaveFailureSignature != SaveFailureSignature(_lastSaveFailure));

    private void QueueSaveFailurePrompt()
    {
        if (_saveFailurePromptPending || !ShouldPromptSaveFailure) return;
        _saveFailurePromptPending = true;
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                while (ShouldPromptSaveFailure)
                {
                    var error = _lastSaveFailure!;
                    var json = _lastSaveFailureJson;
                    var revision = _stateRevision;
                    var choice = ShowSaveFailureDialog(exiting: false);
                    if (IsExiting || _exitRequested || _lastSaveFailure == null) return;
                    if (choice == SaveFailureChoice.Retry)
                    {
                        if (TrySaveNow(sync: true)) return;
                    }
                    else
                    {
                        // Dismiss only this failed edit/error, not the whole process. Automatic
                        // retries continue quietly; new edits or a different error prompt again.
                        _dismissedSaveFailureRevision = revision;
                        _dismissedSaveFailureSignature = SaveFailureSignature(error);
                        _dismissedSaveFailureJson = json;
                    }
                }
            }
            finally { _saveFailurePromptPending = false; }
        });
    }

    private SaveFailureChoice ShowSaveFailureDialog(bool exiting)
    {
        var dialog = new SaveFailureDialog(_lastSaveFailure!, exiting, BuildDialogButtonStyle());
        _saveFailureDialog = dialog;
        try
        {
            dialog.ShowDialog();
            return dialog.Choice;
        }
        finally
        {
            if (ReferenceEquals(_saveFailureDialog, dialog)) _saveFailureDialog = null;
        }
    }

    private void RefreshSaveFailureStatus()
    {
        if (IsExiting) return;
        if (_trayIcon != null)
            _trayIcon.ToolTipText = _lastSaveFailure == null
                ? "Dodo" : "Dodo · " + Strings.Get("SaveFailurePending");
        RefreshTrayMenu();
    }

    private void RetryFailedSave()
    {
        // A deliberate retry deserves fresh failure feedback even for a dismissed snapshot.
        _dismissedSaveFailureRevision = null;
        SaveNow(sync: true);
    }
}
