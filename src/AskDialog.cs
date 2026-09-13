using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Fletta;

/// <summary>
/// Rückfrage im dunklen Stil von Fletta statt der hellen MessageBox von Windows. Enter nimmt den ersten Knopf,
/// Esc und das X den letzten — der letzte ist deshalb immer der, der nichts tut.
/// </summary>
static class AskDialog
{
    /// <returns>Index des gewählten Knopfs.</returns>
    public static int Show(Window owner, string text, params string[] buttons)
    {
        var choice = buttons.Length - 1;
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var dialog = new Window
        {
            Owner = owner,
            Title = "Fletta",
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = (Brush)owner.FindResource("ChromeBrush"),
            Foreground = (Brush)owner.FindResource("TextBrush"),
            FontFamily = owner.FontFamily,
            FontSize = owner.FontSize,
            UseLayoutRounding = true,
            Content = new StackPanel
            {
                Margin = new Thickness(22, 18, 18, 16),
                Children = { new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 }, row },
            },
        };
        dialog.SourceInitialized += (_, _) => MainWindow.ApplyDarkCaption(dialog);
        dialog.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) dialog.Close();
        };
        var (selected, accent) = ((Brush)owner.FindResource("SelectedBrush"), (Brush)owner.FindResource("AccentBrush"));
        for (var index = 0; index < buttons.Length; index++)
        {
            var chosen = index;
            var button = new Button
            {
                Content = buttons[index],
                Style = (Style)owner.FindResource("TextButton"),
                Focusable = true, // der Stil nimmt Werkzeugknöpfen den Fokus; hier soll Tab wandern
                FocusVisualStyle = null, // den Fokus zeigt die grüne Hervorhebung
                IsDefault = index == 0,
                Margin = new Thickness(6, 0, 0, 0),
            };
            // Enter löst den Knopf mit dem Fokus aus, nicht den IsDefault-Knopf: grün ist deshalb immer der fokussierte,
            // sonst sähe nach Tab weiter „Speichern“ gewählt aus, und Enter verwürfe die Änderungen (Review 2026-09-13).
            button.GotKeyboardFocus += (_, _) => (button.Background, button.Foreground) = (selected, accent);
            button.LostKeyboardFocus += (_, _) =>
            {
                button.ClearValue(Control.BackgroundProperty);
                button.ClearValue(Control.ForegroundProperty);
            };
            button.Click += (_, _) =>
            {
                choice = chosen;
                dialog.Close();
            };
            row.Children.Add(button);
        }
        dialog.Loaded += (_, _) => row.Children[0].Focus();
        dialog.ShowDialog();
        return choice;
    }
}
