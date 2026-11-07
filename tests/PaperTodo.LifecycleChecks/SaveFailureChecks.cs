using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using PaperTodo;

internal static class SaveFailureChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    internal static async Task Run(string name)
    {
        var state = new AppState
        {
            EnableAnimations = false, UseCapsuleMode = false, UseDeepCapsuleMode = false,
            ExperimentalEdgeCapsuleHoverPreview = false, UsePersistentPowerShellProcess = false,
            McpEnabled = false, FullscreenTopmostMode = FullscreenTopmostModes.StayOnTop,
            Theme = "light", PaperSkin = PaperSkins.Paper
        };
        var area = SystemParameters.WorkArea;
        state.Papers.Add(new PaperData
        {
            Id = "save-note", Type = PaperTypes.Note, Content = "saved before failure",
            IsVisible = true, X = area.Left + 60, Y = area.Top + 60, Width = 330, Height = 240
        });
        state.Papers.Add(new PaperData
        {
            Id = "save-todo", Type = PaperTypes.Todo, IsVisible = true,
            X = area.Left + 410, Y = area.Top + 60, Width = 330, Height = 240,
            Items = [new PaperItem { Id = "todo-1", Text = "saved todo" }]
        });
        var writer = new SwitchableWriter();
        var store = new StateStore(AppPaths.DataDirectory, writer);
        store.SaveJsonSync(store.SerializeState(state), 1);
        using var controller = new AppController(store);
        await controller.StartAsync(createDefaultPaper: false);
        await Idle();
        var windows = (Dictionary<string, PaperWindow>)Field(controller, "_windows");
        var noteWindow = windows["save-note"];
        var note = (MarkdownTextBox)Field(noteWindow, "_noteBox");
        var todo = ((Dictionary<string, TodoTextBox>)Field(windows["save-todo"], "_todoEditors"))["todo-1"];
        controller.SaveNow(sync: true);
        var oldVersion = controller.PluginEventSaveVersion;
        var oldRevision = controller.PluginEventStateRevision;
        writer.Fail = true;
        note.Document.Replace(0, note.Document.TextLength, "pending editor text at exit");
        todo.Text = "未保存的待办内容";

        try
        {
            if (name == "save-failure-cancel")
            {
                // Advance the existing retry timer quickly; the assertion observes real disk output.
                ((DispatcherTimer)Field(controller, "_forceSaveTimer")).Interval = TimeSpan.FromMilliseconds(80);
                var prompt = ChoosePrompt(dialog =>
                {
                    Require(Button(dialog, "ExitSaveFailureCancel").IsDefault, "exit was the default choice");
                    Require(Button(dialog, "ExitSaveFailureCancel").IsCancel, "Escape cannot cancel exit");
                    controller.Exit(); // Reentry must not dispose or open a second exit flow.
                    Require(windows.Values.All(w => !w.IsClosed), "reentrant exit closed editors");
                    Click(dialog, "ExitSaveFailureCancel");
                });
                controller.Exit();
                await prompt;
                Require(controller.IsRunning && windows.Values.All(w => !w.IsClosed && w.HasVisibleSurface),
                    "cancel exit did not retain running windows");
                Require(note.Text == "pending editor text at exit" && todo.Text == "未保存的待办内容",
                    "cancel exit lost unsaved editor content");
                Require(store.Load().Papers[0].Content == "saved before failure", "failed save replaced old data");
                Require(note.Document.UndoStack.CanUndo, "cancel exit discarded note undo history");
                note.Document.UndoStack.Undo();
                Require(note.Text == "saved before failure", "note undo stopped working after cancel");
                note.Document.UndoStack.Redo();
                Require(note.Text == "pending editor text at exit", "note redo lost pending content");
                Require(HasUnsavedReminder(controller), "cancel exit lost the unsaved reminder");
                writer.Fail = false;
                await Until(() => store.Load().Papers[0].Content == note.Text, "automatic retry after cancel");
                await Until(() => !HasUnsavedReminder(controller), "saved status after retry");
                Require(store.Load().Papers[1].Items[0].Text == todo.Text, "automatic retry lost Todo text");
                note.Document.Insert(note.Document.TextLength, "，取消后继续编辑");
                await Until(() => store.Load().Papers[0].Content == note.Text, "autosave after further editing");
            }
            else if (name == "real-exit-retry")
            {
                Task? secondPrompt = null;
                var prompt = ChoosePrompt(dialog =>
                {
                    // The first retry also fails; the next prompt must still offer cancellation.
                    secondPrompt = ChoosePrompt(next =>
                    {
                        Require(!noteWindow.IsClosed && note.Text == "pending editor text at exit",
                            "retry failure destroyed pending edits");
                        writer.Fail = false;
                        Click(next, "SaveFailureRetry");
                    });
                    Click(dialog, "SaveFailureRetry");
                });
                controller.Exit();
                await prompt;
                Require(secondPrompt != null, "the failed retry did not show a second prompt");
                await secondPrompt!;
            }
            else if (name == "real-exit-discard")
            {
                var prompt = ChoosePrompt(dialog => Click(dialog, "ExitWithoutSaving"));
                controller.Exit();
                await prompt;
            }
            else if (name == "save-failure-system-exit")
            {
                var prompt = ChoosePrompt(_ => controller.ExitForSystemShutdown());
                controller.Exit();
                await prompt;
            }
            else
            {
                // Ignore only the current error/content, while keeping an actionable reminder.
                await FailAndDismiss(controller);
                Require(HasUnsavedReminder(controller), "ignore removed the unsaved reminder");
                var extraPrompts = 0;
                EventHandler guard = (_, _) =>
                {
                    foreach (var dialog in Application.Current.Windows.OfType<SaveFailureDialog>().ToArray())
                    { extraPrompts++; dialog.Close(); }
                };
                var guardTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
                guardTimer.Tick += guard;
                guardTimer.Start();
                try
                {
                    controller.SaveNow(sync: true); // unchanged automatic retry-equivalent failure
                    await Idle();
                    Require(extraPrompts == 0, "unchanged failed snapshot repeatedly interrupted editing");
                    // Deliver delayed completions through the same production handlers. A stale
                    // success/failure must not clear or resurrect the current tray warning.
                    Call(controller, "RecordSaveSuccess", oldVersion, oldRevision, false);
                    Require(HasUnsavedReminder(controller), "old success cleared a newer failure");
                }
                finally { guardTimer.Stop(); }

                var failedManualRetry = ChoosePrompt(dialog => Click(dialog, "SaveFailureIgnore"));
                RetryFromTray(controller);
                await failedManualRetry;
                await Idle();
                Require(HasUnsavedReminder(controller), "failed manual retry removed the warning");

                // Immediate-save paths need not call MarkDirty: changed serialized content also
                // makes this a new failure rather than inheriting the dismissed warning forever.
                controller.State.Papers[0].Title = "new unsaved title";
                await FailAndDismiss(controller);
                note.Document.Insert(note.Document.TextLength, " new edit");
                await FailAndDismiss(controller);
                writer.Message = "A different storage failure";
                await FailAndDismiss(controller);

                // Manual retry from the tray uses the same production save and clears the warning.
                writer.Fail = false;
                RetryFromTray(controller);
                await Until(() => !HasUnsavedReminder(controller), "manual tray retry");
                Require(store.Load().Papers[0].Content == note.Text, "manual retry did not save latest editor text");
                Call(controller, "HandleSaveFailure", new IOException("late old failure"), oldVersion, "old snapshot");
                await Idle();
                Require(!HasUnsavedReminder(controller), "old failure resurrected a resolved warning");

                writer.Fail = true;
                var prompt = ChoosePrompt(dialog =>
                {
                    writer.Fail = false;
                    Click(dialog, "SaveFailureRetry");
                });
                note.Document.Insert(note.Document.TextLength, " recovered then failed again");
                controller.SaveNow(sync: true);
                await prompt;
                await Idle();
                Require(!HasUnsavedReminder(controller), "retry button did not clear a resolved failure");
            }
        }
        finally { writer.Fail = false; }
    }

    private static async Task FailAndDismiss(AppController controller)
    {
        var prompt = ChoosePrompt(dialog => Click(dialog, "SaveFailureIgnore"));
        controller.SaveNow(sync: true);
        await prompt;
        await Idle();
    }

    private static Task ChoosePrompt(Action<SaveFailureDialog> choose) =>
        Dispatcher.CurrentDispatcher.InvokeAsync(() =>
        {
            var dialog = Application.Current.Windows.OfType<SaveFailureDialog>().Single();
            try { choose(dialog); }
            finally { if (dialog.IsVisible) dialog.Close(); }
        }, DispatcherPriority.ApplicationIdle).Task;

    private static Button Button(SaveFailureDialog dialog, string id) =>
        Descendants(dialog).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == id);
    private static void Click(SaveFailureDialog dialog, string id) =>
        Button(dialog, id).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            foreach (var descendant in Descendants(child)) yield return descendant;
    }

    private static bool HasUnsavedReminder(AppController controller)
    {
        var menu = new ContextMenu();
        controller.RebuildTrayMenu(menu);
        return menu.Items.OfType<MenuItem>().Any(i => Equals(i.Header, Strings.Get("SaveFailurePending")));
    }

    private static void RetryFromTray(AppController controller)
    {
        var menu = new ContextMenu();
        controller.RebuildTrayMenu(menu);
        menu.Items.OfType<MenuItem>().Single(i => Equals(i.Header, Strings.Get("SaveFailurePending")))
            .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
    }

    private static object Field(object target, string name) =>
        target.GetType().GetField(name, Private)?.GetValue(target) ??
        target.GetType().GetProperty(name, Private)?.GetValue(target) ??
        throw new MissingMemberException(target.GetType().FullName, name);
    private static void Call(object target, string name, params object?[] args) => target.GetType().GetMethod(name, Private)!.Invoke(target, args);
    private static Task Idle() => Dispatcher.CurrentDispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle).Task;
    private static async Task Until(Func<bool> predicate, string label)
    {
        var started = Stopwatch.GetTimestamp();
        while (!predicate())
        {
            if (Stopwatch.GetElapsedTime(started).TotalSeconds > 5) throw new TimeoutException(label);
            await Task.Delay(20);
        }
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    private sealed class SwitchableWriter : IDurableAtomicFileWriter
    {
        public volatile bool Fail;
        public string Message = "Injected storage failure";
        public void Write(string path, byte[] bytes, Func<string, bool>? validateTemp = null)
        {
            if (Fail) throw new IOException(Message);
            DurableAtomicFileWriter.Shared.Write(path, bytes, validateTemp);
        }
    }
}
