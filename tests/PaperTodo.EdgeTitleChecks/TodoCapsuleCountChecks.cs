using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using PaperTodo;

internal static partial class Program
{
    private static void TodoCapsuleCountChecks()
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var controller = (AppController)RuntimeHelpers.GetUninitializedObject(typeof(AppController));
        // Initialize empty controller collections without its disk/tray/startup lifecycle.
        foreach (var field in typeof(AppController).GetFields(flags))
            if (field.FieldType.Namespace == "System.Collections.Generic" &&
                field.FieldType.GetConstructor(Type.EmptyTypes) != null)
                field.SetValue(controller, Activator.CreateInstance(field.FieldType));
        void Field(string name, object value) => typeof(AppController).GetField(name, flags)!.SetValue(controller, value);
        var state = new AppState { EnableAnimations = false, ExperimentalTodoReminders = false };
        Field("<State>k__BackingField", state);
        Field("_windows", new Dictionary<string, PaperWindow>());
        Field("_masterCapsules", new Dictionary<string, MasterCapsuleWindow>());
        Field("_deepCapsuleContextMenuOwners", new HashSet<string>());
        Field("_suppressDirty", true);
        var paper = new PaperData { Type = PaperTypes.Todo, Title = "工作", Items =
        [new() { Text = "one" }, new() { Text = "two" }, new() { Text = "done", Done = true },
         new(), new() { Text = " \t\r\n" }] };
        state.Papers.Add(paper);
        Check(controller.PaperCapsuleTitle(paper) == "工作 · 2", "Only meaningful unfinished todos count");
        Check(controller.PaperTitleText(paper) == "工作", "Count never changes the stored/display title");
        var quickLaunch = new PaperItem();
        quickLaunch.RestoreQuickLaunch(null, "C:\\fixture.txt", false);
        paper.Items.Add(quickLaunch);
        Check(controller.PaperCapsuleTitle(paper) == "工作 · 3", "A quick launch without text is a real item");
        paper.Items.RemoveAt(paper.Items.Count - 1);
        state.ShowCapsuleTodoCount = false;
        Check(controller.PaperCapsuleTitle(paper) == "工作", "Setting disables the count");
        state.ShowCapsuleTodoCount = true;
        var note = new PaperData { Type = PaperTypes.Note, Title = "笔记", Items = paper.Items };
        Check(TodoCapsuleTitle.Suffix(note, true) == "", "Notes cannot gain todo counts");

        // The normal WPF shell and its real handlers run without Show, input injection or a HWND.
        var window = new PaperWindow(paper, controller);
        object Part(string name) => typeof(PaperWindow).GetField(name, flags)!.GetValue(window)!;
        void Invoke(string name, params object?[] args) => typeof(PaperWindow).GetMethod(name, flags)!.Invoke(window, args);
        string Label() => ((TextBlock)Part("_capsuleLabelText")).Text;
        Check(!window.IsVisible && Label() == "工作 · 2", "Unshown shell starts with the derived count");
        var editors = (Dictionary<string, TodoTextBox>)Part("_todoEditors");
        editors[paper.Items[0].Id].Text = " ";
        Check(Label() == "工作 · 1", "Real editor text handler removes an empty item from the count");
        editors[paper.Items[0].Id].Text = "one";
        Check(Label() == "工作 · 2", "Real editor text handler restores the count");
        var firstRow = ((List<Border>)Part("_todoRows")).Single(row => Equals(row.Tag, paper.Items[0].Id));
        var checkbox = ((Grid)firstRow.Child).Children.OfType<CheckBox>().Single();
        checkbox.IsChecked = true;
        Check(Label() == "工作 · 1", "Real checkbox completion updates the count");
        checkbox.IsChecked = false;
        Check(Label() == "工作 · 2", "Real checkbox unchecking restores the count");
        Invoke("SetTodoDoneFromEdgeCapsulePreview", paper.Items[0].Id, true);
        Check(Label() == "工作 · 1", "Preview completion refreshes the count");
        Invoke("Undo");
        Check(Label() == "工作 · 2", "Actual undo restores the count");
        Invoke("Redo");
        Check(Label() == "工作 · 1", "Actual redo reapplies the count");
        Invoke("RemoveItem", paper.Items.Single(item => item.Text == "two"), true, null, true);
        Check(Label() == "工作", "Deleting the last unfinished item hides zero");
        Invoke("Undo");
        Check(Label() == "工作 · 1", "Deletion undo restores the count");
        paper.Items = Enumerable.Range(0, 12).Select(index => new PaperItem { Text = "task " + index }).ToList();
        window.RefreshTodoRowsForExternalChange();
        Check(Label() == "工作 · 12", "External/bulk row replacement refreshes the real shell");
        var selected = (HashSet<string>)Part("_selectedTodoItemIds");
        selected.UnionWith(paper.Items.Take(3).Select(item => item.Id));
        Invoke("ApplyDoneToSelectedTodos", true);
        Check(Label() == "工作 · 9", "Actual batch completion updates the count");
        Invoke("Undo");
        Check(Label() == "工作 · 12", "Batch completion has one undo boundary");
        selected.Clear();
        selected.UnionWith(paper.Items.Take(2).Select(item => item.Id));
        Invoke("DeleteSelectedTodoItems");
        Check(Label() == "工作 · 10", "Actual batch deletion updates the count");
        Invoke("Undo");
        Check(Label() == "工作 · 12", "Batch deletion has one undo boundary");
        state.DeepCapsuleTitleMeasureCharacterLimit = 1;
        var measure = typeof(PaperWindow).GetMethod("MeasureCapsuleTitleWidth", flags)!;
        var countedWidth = (double)measure.Invoke(window, [true, 1d])!;
        state.ShowCapsuleTodoCount = false;
        var plainWidth = (double)measure.Invoke(window, [true, 1d])!;
        Check(countedWidth > plainWidth + 10, "Title character limit still reserves the count width");
        state.ShowCapsuleTodoCount = true;
        state.DeepCapsuleTitleMeasureCharacterLimit = EdgeCapsuleTitleLimit.Hidden;
        Check((double)measure.Invoke(window, [true, 1d])! == 0, "Hidden title requests no count width");
        state.DeepCapsuleTitleMeasureCharacterLimit = EdgeCapsuleTitleLimit.Unlimited;
        foreach (var item in paper.Items) item.Done = true;
        window.RefreshTodoRowsForExternalChange();
        Check(Label() == "工作", "All completed hides the count");
        state.ShowCapsuleTodoCount = false;
        window.RefreshPaperTitle();
        Check(Label() == "工作", "Runtime setting refresh retains title");
        Check(!window.IsVisible, "Checks never show the shell");

        var directory = Path.Combine(Path.GetTempPath(), "Dodo.CapsuleChecks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var store = new StateStore(directory, DurableAtomicFileWriter.Shared);
        store.SaveJsonSync(store.SerializeState(state), 1);
        var restored = store.Load();
        Check(!restored.ShowCapsuleTodoCount && TodoCapsuleTitle.Suffix(restored.Papers[0], true) == "",
            "Restart retains setting and derives zero from completed data");
        restored.ShowCapsuleTodoCount = true;
        restored.Papers[0].Items[0].Done = false;
        store.SaveJsonSync(store.SerializeState(restored), 2);
        restored = store.Load();
        Check(restored.ShowCapsuleTodoCount && TodoCapsuleTitle.Suffix(restored.Papers[0], true) == " · 1",
            "Restart derives the count from persisted tasks");
        Console.WriteLine("Capsule persistence fixture retained at " + directory);

        using var host = EdgeCapsuleHost.Create(new EdgeCapsuleHostOptions(
            WindowChromeMargin: 4, ChromeCornerRadius: 16, InnerCornerRadius: 15,
            OutlineThickness: 2, OutlineOverlap: 1, BodyHeight: 32,
            LeftPadding: 6, IconGap: 4, IconText: "✓", IconFontSize: 13,
            LabelFontSize: 12, LabelFontWeight: FontWeights.Normal, CloseToolTip: "Close",
            PaperBrush: Brushes.White, PaperBorderBrush: Brushes.Gray, OutlineBrush: Brushes.Blue,
            HoverBrush: Brushes.LightGray, IconBrush: Brushes.Gray,
            StrongTextBrush: Brushes.Black, TextBrush: Brushes.Gray,
            UiFontFamily: new FontFamily("Segoe UI"), SymbolFontFamily: new FontFamily("Segoe UI Symbol"),
            Language: XmlLanguage.GetLanguage("en-US"), Topmost: false, DiagnosticId: "todo-count-checks"));
        T HostPart<T>(string name) => (T)typeof(EdgeCapsuleHost).GetProperty(name, flags)!.GetValue(host)!;
        var labelGroup = HostPart<Grid>("LabelGroup");
        var count = HostPart<TextBlock>("TodoCount");
        var title = HostPart<TextBlock>("Label");
        foreach (var scale in new[] { 1d, 1.25d, 1.5d, 2d })
        foreach (var brush in new[] { Brushes.Black, Brushes.White })
        {
            host.SetLabel("很长的标题 👨‍👩‍👧‍👦 Project プロジェクト 작업 · 123", "full title", " · 123");
            typeof(EdgeCapsuleHost).GetMethod("ApplyDefaultContentVisibility", flags)!.Invoke(host, [true]);
            title.FontSize = 12 * scale;
            title.Foreground = brush;
            labelGroup.Measure(new Size(105 * scale, 40 * scale));
            labelGroup.Arrange(new Rect(0, 0, 105 * scale, 40 * scale));
            labelGroup.UpdateLayout();
            Check(count.Text == " · 123" && count.ActualWidth >= count.DesiredSize.Width - .1 &&
                count.ActualWidth > 10 && title.ActualWidth > 0,
                "Narrow long-title layout reserves all count digits at scale " + scale);
            Check(Equals(count.Foreground, brush) && count.FontSize == title.FontSize && !count.IsHitTestVisible,
                "Count follows theme/typography and owns no hit target");
        }
        host.SetPluginContent(new TextBlock { Text = "plugin" }, null);
        Check(count.Visibility == Visibility.Collapsed, "Plugin content replaces the count too");
        host.SetPluginContent(null, null);
        typeof(EdgeCapsuleHost).GetMethod("ApplyDefaultContentVisibility", flags)!.Invoke(host, [false]);
        Check(count.Visibility == Visibility.Collapsed, "Hidden-title mode also hides the count");
        Check(!host.IsVisible, "Layout checks never show the docked host");
    }
}
