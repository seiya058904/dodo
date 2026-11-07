using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace PaperTodo;

internal enum SaveFailureChoice { Dismiss, Retry, ExitWithoutSaving }

internal sealed class SaveFailureDialog : Window
{
    internal SaveFailureChoice Choice { get; private set; } = SaveFailureChoice.Dismiss;

    internal SaveFailureDialog(Exception error, bool exiting, Style buttonStyle)
    {
        Title = Strings.Get("SaveFailureTitle");
        Width = Math.Min(480, SystemParameters.WorkArea.Width - 32);
        MaxHeight = Math.Max(200, SystemParameters.WorkArea.Height - 32);
        SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        FontFamily = AppTypography.UiFontFamily;
        Language = AppTypography.Language;
        AppTypography.ApplyTextRendering(this);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(new TextBlock
        {
            Text = Title, FontSize = AppTypography.Scale(16), FontWeight = FontWeights.SemiBold,
            Foreground = Theme.TextBrush, Margin = new Thickness(0, 0, 0, 10)
        });
        var message = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = Math.Max(80, MaxHeight - 160),
            Content = new TextBlock
            {
                Text = Strings.Format(exiting ? "ExitSaveFailureMessage" : "SaveFailureMessage", error.Message),
                Foreground = Theme.TextBrush, TextWrapping = TextWrapping.Wrap,
                FontSize = AppTypography.Scale(13)
            }
        };
        Grid.SetRow(message, 1);
        layout.Children.Add(message);
        var buttons = new WrapPanel
        {
            HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0)
        };
        if (exiting) AddButton("ExitWithoutSaving", SaveFailureChoice.ExitWithoutSaving);
        AddButton("SaveFailureRetry", SaveFailureChoice.Retry);
        var dismiss = AddButton(exiting ? "ExitSaveFailureCancel" : "SaveFailureIgnore", SaveFailureChoice.Dismiss);
        dismiss.IsCancel = true;
        dismiss.IsDefault = true;
        Loaded += (_, _) => dismiss.Focus();
        Grid.SetRow(buttons, 2);
        layout.Children.Add(buttons);
        Content = new Border
        {
            Background = Theme.PaperBrush, BorderBrush = Theme.PaperBorderBrush,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
            Padding = new Thickness(20), Child = layout
        };

        Button AddButton(string key, SaveFailureChoice choice)
        {
            var button = new Button
            {
                Content = Strings.Get(key), Style = buttonStyle,
                MinHeight = AppTypography.FitChrome(32), Margin = new Thickness(4, 2, 0, 2)
            };
            AutomationProperties.SetAutomationId(button, key);
            button.Click += (_, _) => { Choice = choice; Close(); };
            buttons.Children.Add(button);
            return button;
        }
    }
}
