using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PaperTodo;

internal static partial class Program
{
    private static void VerifyBackgroundCompatibility(AppController controller, PaperWindow todoWindow,
        PaperWindow noteWindow, PaperData todo, PaperData note, Window settings)
    {
        var checks = 0;
        void Require(bool value, string message)
        { if (!value) throw new InvalidOperationException(message); checks++; }
        // The visual-only capsule examples share the Todo items; exclude those duplicates
        // from the two-paper search fixture after their PNGs have been captured.
        var visualExamples = controller.State.Papers.Where(paper => paper != todo && paper != note).ToArray();
        controller.State.Papers.RemoveAll(paper => paper != todo && paper != note);
        var windows = (Dictionary<string, PaperWindow>)typeof(AppController).GetField("_windows", Private)!.GetValue(controller)!;
        windows[todo.Id] = todoWindow;
        windows[note.Id] = noteWindow;
        var noteBox = (MarkdownTextBox)typeof(PaperWindow).GetProperty("_noteBox", Private)!.GetValue(noteWindow)!;
        var editors = (Dictionary<string, TodoTextBox>)typeof(PaperWindow).GetField("_todoEditors", Private)!.GetValue(todoWindow)!;
        const string draft = "未保存的草稿 unsavedDraft42";
        var priorNote = noteBox.Text;
        var priorTodo = editors[todo.Items[0].Id].Text;
        var storedNote = note.Content;
        noteBox.Text = draft;
        editors[todo.Items[0].Id].Text = draft;
        Require(note.Content == storedNote, "Fixture must retain an uncommitted Markdown model");
        Require(noteWindow.TryGetBuiltInFindText(null, out var liveNote) && liveNote == draft,
            "Note find reads the pending Chinese/English editor text");
        Require(todoWindow.TryGetBuiltInFindText(todo.Items[0].Id, out var liveTodo) && liveTodo == draft,
            "Todo find reads the live Chinese/English editor text");
        var sources = controller.GetBuiltInFindSources();
        Require(sources.Single(source => source.PaperId == note.Id).Text == draft &&
            sources.Single(source => source.PaperId == todo.Id && source.TodoItemId == todo.Items[0].Id).Text == draft,
            "Controller search overrides persisted models with live editors");
        foreach (var query in new[] { "未保存", "UNSAVEDdraft42" })
        {
            var matches = (ICollection)Call(todoWindow, "ScanGlobalFindMatches", query)!;
            Require(matches.Count == 2, "Global matching finds both papers for " + query);
        }
        windows.Remove(note.Id);
        Require(controller.GetBuiltInFindSources().Single(source => source.PaperId == note.Id).Text == storedNote,
            "Nonmaterialized paper search falls back to the model");
        windows[note.Id] = noteWindow;
        var plugin = new PaperData { Type = PaperTypes.Note, BodyProviderId = "fixture.plugin", Content = draft };
        controller.State.Papers.Add(plugin);
        Require(controller.GetBuiltInFindSources().All(source => source.PaperId != plugin.Id),
            "Global built-in search does not consume plugin-owned content");
        controller.State.Papers.Remove(plugin);
        noteBox.Text = priorNote;
        editors[todo.Items[0].Id].Text = priorTodo;

        var definitions = ((IEnumerable<PaperSettingDefinition>)Call(controller, "CreatePublicSettingsCatalog")!)
            .Where(definition => definition.Metadata.Id == "capsule.todo_count").ToArray();
        var commit = true;
        var service = new PaperSettingsService(definitions, () => true, () => commit, _ =>
        {
            // The native/tray publication is intentionally excluded from this offscreen check.
            // Production's region scheduler intentionally skips invisible settings windows.
            // Execute its registered region builder directly without bypassing that UI guard.
            var regions = (Dictionary<string, Action>)typeof(AppController)
                .GetField("_settingsRegionRefreshers", Private)!.GetValue(controller)!;
            regions["general.todoCount"]();
            ((FrameworkElement)settings.Content).UpdateLayout();
            todoWindow.RefreshPaperTitle();
        });
        CheckBox CountToggle() => Descendants(settings.Content as DependencyObject)
            .OfType<CheckBox>().Single(box => Equals(box.Content, Strings.Get("SettingsShowCapsuleTodoCount")));
        Require(CountToggle().IsChecked == true, "Count toggle initially reflects the enabled default");
        service.Set("capsule.todo_count", JsonSerializer.SerializeToElement(false));
        Require(!controller.State.ShowCapsuleTodoCount && CountToggle().IsChecked == false &&
            controller.PaperCapsuleTitle(todo) == controller.PaperTitleText(todo),
            "Settings API updates the actual live region and derived capsule text");
        service.Set("capsule.todo_count", JsonSerializer.SerializeToElement(true));
        Require(controller.State.ShowCapsuleTodoCount && CountToggle().IsChecked == true,
            "Re-enabling count updates the live region");
        commit = false;
        try { service.Set("capsule.todo_count", JsonSerializer.SerializeToElement(false)); throw new Exception("Expected rejected save"); }
        catch (PaperSettingsException ex) when (ex.Code == "save_failed") { }
        Require(controller.State.ShowCapsuleTodoCount && CountToggle().IsChecked == true,
            "Rejected count-setting persistence rolls back without publishing stale UI");
        windows.Clear();
        controller.State.Papers.AddRange(visualExamples);
        Console.WriteLine($"Background compatibility: {checks} search and settings assertions passed without native presentation.");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject? root)
    {
        if (root == null) yield break;
        yield return root;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
