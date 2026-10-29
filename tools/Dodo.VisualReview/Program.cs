using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using PaperTodo;

internal static partial class Program
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 3 || args[0] is not ("zh-CN" or "en-US") || args[1] is not ("light" or "dark"))
        {
            Console.Error.WriteLine("Usage: Dodo.VisualReview <zh-CN|en-US> <light|dark> <output-directory>");
            return 2;
        }
        // UiLanguages is intentionally restart-scoped. Each invocation has its own language.
        // Only this tool's output directory contains the generated fixture profile.
        Directory.CreateDirectory(AppPaths.DataDirectory);
        File.WriteAllText(Path.Combine(AppPaths.DataDirectory, "data.json"),
            JsonSerializer.Serialize(new { uiLanguage = args[0], papers = Array.Empty<object>() }));
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        try { Render(args[0], args[1], Path.GetFullPath(args[2])); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { app.Shutdown(); }
    }

    private static void Render(string language, string theme, string output)
    {
        Directory.CreateDirectory(output);
        var zh = language == "zh-CN";
        var state = new AppState
        {
            UiLanguage = language, Theme = theme, ColorScheme = ColorSchemes.Warm,
            EnableAnimations = false, UseCapsuleMode = true, UseDeepCapsuleMode = false,
            ExperimentalTodoReminders = false, ShowCapsuleTodoCount = true
        };
        var controller = (AppController)RuntimeHelpers.GetUninitializedObject(typeof(AppController));
        foreach (var field in typeof(AppController).GetFields(Private))
            if (field.FieldType.Namespace == "System.Collections.Generic" && field.FieldType.GetConstructor(Type.EmptyTypes) != null)
                field.SetValue(controller, Activator.CreateInstance(field.FieldType));
        Set(controller, "<State>k__BackingField", state);
        Set(controller, "_suppressDirty", true);
        using var plugins = new PaperBodyPluginRegistry();
        using var images = new NoteImageStore();
        Set(controller, "_paperBodyPlugins", plugins);
        Set(controller, "_imageStore", images);
        typeof(AppController).GetProperty("Current")!.SetValue(null, controller);
        Theme.Invalidate();
        AppTypography.Configure(state.UiFontPreset, state.Zoom, state.CustomFontEnhancedBold, state.TextRenderingProfile);
        NoteTypography.Configure(state.NoteTextSize, state.NoteTextBold);

        using (var source = typeof(Program).Assembly.GetManifestResourceStream("Dodo.App.xaml")!)
        {
            var xaml = XDocument.Load(source);
            XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
            var resources = new XElement(ns + "ResourceDictionary", xaml.Root!.Attributes().Where(a => a.IsNamespaceDeclaration),
                xaml.Root.Element(ns + "Application.Resources")!.Nodes());
            Application.Current.Resources = (ResourceDictionary)XamlReader.Parse(resources.ToString());
        }
        Call(controller, "RefreshApplicationThemeResources");

        var todo = new PaperData
        {
            Type = PaperTypes.Todo, Title = zh ? "今天" : "Today", Width = 330, Height = 345,
            Items = [new() { Text = zh ? "梳理今天最重要的三件事" : "Choose today's three priorities" },
                new() { Text = zh ? "整理项目笔记" : "Organize project notes" },
                new() { Text = zh ? "留一点时间给自己" : "Make time for yourself" },
                new() { Text = zh ? "完成晨间计划" : "Plan the morning", Done = true }]
        };
        var note = new PaperData
        {
            Type = PaperTypes.Note, Title = zh ? "随手记" : "Notes", Width = 360, Height = 345,
            Content = zh ? "# 留在桌面上的想法\n\n把任务放在眼前，也给想法留一点空间。\n\n## 今天的灵感\n\n- 简单一点，专注一点\n- 用 Markdown 记录\n- 完成后，收起成胶囊\n\n**慢慢来，也是在前进。**" :
                "# A little space to think\n\nKeep tasks in sight and leave room for ideas.\n\n## Today's inspiration\n\n- Keep things simple\n- Write with Markdown\n- Collapse into a capsule\n\n**Small steps count.**"
        };
        state.Papers.AddRange([todo, note]);
        var todoWindow = new PaperWindow(todo, controller);
        var noteWindow = new PaperWindow(note, controller);
        var prefix = language + "-" + theme;
        SaveWindow(todoWindow, 330, 345, Path.Combine(output, prefix + "-todo.png"));
        SaveWindow(noteWindow, 360, 345, Path.Combine(output, prefix + "-note.png"));

        var collapsed = new PaperData { Type = PaperTypes.Todo, Title = zh ? "工作" : "Work", IsCollapsed = true, Items = todo.Items };
        state.Papers.Add(collapsed);
        var capsuleWindow = new PaperWindow(collapsed, controller);
        SaveWindow(capsuleWindow, capsuleWindow.Width, capsuleWindow.Height, Path.Combine(output, prefix + "-capsule.png"));

        var longPaper = new PaperData { Type = PaperTypes.Todo, Title = zh ? "本周项目计划与灵感收集" : "Project planning and ideas", Items = todo.Items };
        state.Papers.Add(longPaper);
        var longWindow = new PaperWindow(longPaper, controller);
        var edge = (EdgeCapsuleHost)Call(longWindow, "EnsureDeepCapsuleSlotHost")!;
        Call(edge, "ApplyDefaultContentVisibility", true);
        var content = (FrameworkElement)typeof(EdgeCapsuleHost).GetProperty("ContentGrid", Private)!.GetValue(edge)!;
        SaveVisual(content, 116, 30, Path.Combine(output, prefix + "-narrow-count.png"));

        var settings = new Window { Width = 1040, Height = 760, FontFamily = AppTypography.UiFontFamily,
            FontSize = AppTypography.Scale(12), Language = AppTypography.Language };
        Set(controller, "_settingsWindow", settings);
        settings.Content = Call(controller, "BuildSettingsSidebarWindowContent", settings);
        SaveWindow(settings, 1040, 760, Path.Combine(output, prefix + "-settings.png"));
        VerifyBackgroundCompatibility(controller, todoWindow, noteWindow, todo, note, settings);
        foreach (Window window in Application.Current.Windows)
            if (window.IsVisible || new WindowInteropHelper(window).Handle != IntPtr.Zero)
                throw new InvalidOperationException("Visual review unexpectedly created a native window.");
        File.WriteAllText(Path.Combine(output, prefix + "-evidence.txt"),
            "Actual WPF PaperWindow, EdgeCapsuleHost content and settings controls rendered with RenderTargetBitmap.\n" +
            "No Show, Activate, native HWND, input, clipboard, application startup or production data access.\n" +
            "The narrow-count crop is the real docked content grid, not the full docked HWND.\n" +
            "Offscreen rendering does not verify native material, real DPI transitions, dragging, hover or window handoff.\n");
        Console.WriteLine(prefix + ": 5 actual WPF PNGs rendered without native windows in " + output);
    }

    private static void SaveWindow(Window window, double width, double height, string path)
    {
        if (window.Content is not FrameworkElement root) throw new InvalidOperationException("Missing real window content");
        SaveVisual(root, width, height, path);
    }

    private static void SaveVisual(FrameworkElement element, double width, double height, string path)
    {
        element.Measure(new Size(width, height));
        element.Arrange(new Rect(0, 0, width, height));
        element.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(width * 2), (int)Math.Ceiling(height * 2), 192, 192, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
    private static void Set(object target, string field, object value) => target.GetType().GetField(field, Private)!.SetValue(target, value);
    private static object? Call(object target, string method, params object?[] args) => target.GetType().GetMethod(method, Private)!.Invoke(target, args);
}
