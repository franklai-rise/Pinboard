using System.Windows;
using System.Windows.Controls;
using Pinboard.App.Services;
using TextBox = System.Windows.Controls.TextBox;
using Button = System.Windows.Controls.Button;
using Orientation = System.Windows.Controls.Orientation;

namespace Pinboard.App;

internal sealed class TextEntryDialog : Window
{
    private readonly TextBox _input = new() { Margin = new Thickness(0, 18, 0, 22), MinWidth = 320 };
    public string Value => _input.Text.Trim();

    public TextEntryDialog(string title, string prompt)
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var stack = new StackPanel { Margin = new Thickness(28) };
        stack.Children.Add(new TextBlock { Text = title, FontSize = 21, FontWeight = FontWeights.SemiBold });
        stack.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0), Style = (Style)FindResource("SecondaryText") });
        stack.Children.Add(_input);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        buttons.Children.Add(new Button { Content = LocalizationService.T("CommonCancel"), IsCancel = true });
        var accept = new Button { Content = LocalizationService.T("RenameBoardSave"), IsDefault = true, Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(8, 0, 0, 0) };
        accept.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(Value)) DialogResult = true; };
        buttons.Children.Add(accept);
        stack.Children.Add(buttons);
        Content = stack;
        Loaded += (_, _) => _input.Focus();
    }
}
