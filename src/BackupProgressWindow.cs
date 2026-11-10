using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace PaperTodo;

internal sealed class BackupProgressWindow : Window
{
    private readonly CancellationTokenSource _cancellation;
    private readonly TextBlock _stage;
    private readonly Button _cancel;
    private readonly string _cancelling;
    private bool _finished;
    private readonly Window? _owner;

    internal BackupProgressWindow(Window? owner, CancellationTokenSource cancellation,
        string title, string cancel, string cancelling)
    {
        _owner = owner;
        _cancellation = cancellation;
        _cancelling = cancelling;
        Title = title;
        Owner = owner;
        if (_owner != null) _owner.Closed += OwnerClosed;
        Width = 400;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = Theme.PaperBrush;
        Foreground = Theme.TextBrush;
        FontFamily = AppTypography.UiFontFamily;
        Language = AppTypography.Language;
        AppTypography.ApplyTextRendering(this);
        var panel = new StackPanel { Margin = new Thickness(20) };
        _stage = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = AppTypography.Scale(14), Margin = new Thickness(0, 0, 0, 12) };
        panel.Children.Add(_stage);
        panel.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 6, Margin = new Thickness(0, 0, 0, 16) });
        _cancel = new Button { Content = cancel, HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(16, 4, 16, 4), MinHeight = AppTypography.FitChrome(32) };
        _cancel.Click += (_, _) => RequestCancel();
        panel.Children.Add(_cancel);
        Content = panel;
    }

    internal void SetStage(string text)
    {
        if (!_finished && !_cancellation.IsCancellationRequested) _stage.Text = text;
    }

    private void OwnerClosed(object? sender, EventArgs e) => RequestCancel();

    private void RequestCancel()
    {
        if (_finished || _cancellation.IsCancellationRequested) return;
        _stage.Text = _cancelling;
        _cancel.IsEnabled = false;
        _cancellation.Cancel();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_finished)
        {
            e.Cancel = true;
            RequestCancel();
        }
        base.OnClosing(e);
    }

    internal void Finish()
    {
        if (_finished) return;
        _finished = true;
        if (_owner != null) _owner.Closed -= OwnerClosed;
        if (IsVisible) Close();
    }
}
