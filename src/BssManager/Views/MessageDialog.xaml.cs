using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace BssManager.Views;

public enum DialogKind
{
    Info,
    Warning,
    Danger
}

/// <summary>Which button the user pressed.</summary>
public enum DialogChoice
{
    Primary,
    Secondary,
    Cancel
}

/// <summary>
/// Replaces MessageBox. The system one draws a light-themed window with the
/// Windows title bar, which looked like a different application every time it
/// appeared over this one.
/// </summary>
public partial class MessageDialog : Window
{
    private DialogChoice _choice = DialogChoice.Cancel;

    private MessageDialog()
    {
        InitializeComponent();

        // No system caption to drag by, so the whole surface moves it.
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
    }

    /// <summary>
    /// Shows a dialog and returns the button pressed. Pass null for
    /// <paramref name="secondary"/> or <paramref name="cancel"/> to hide them.
    /// </summary>
    public static DialogChoice Show(
        string title,
        string message,
        string primary = "OK",
        string? secondary = null,
        string? cancel = null,
        DialogKind kind = DialogKind.Info)
    {
        var dialog = new MessageDialog();

        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.PrimaryButton.Content = primary;

        if (secondary is null) dialog.SecondaryButton.Visibility = Visibility.Collapsed;
        else dialog.SecondaryButton.Content = secondary;

        if (cancel is null) dialog.CancelButton.Visibility = Visibility.Collapsed;
        else dialog.CancelButton.Content = cancel;

        dialog.ApplyKind(kind);

        // Owner keeps it centred on the app and modal to it. During startup
        // there may not be a main window yet.
        var owner = Application.Current?.MainWindow;
        if (owner is not null && owner.IsLoaded && !ReferenceEquals(owner, dialog))
            dialog.Owner = owner;
        else
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        dialog.ShowDialog();
        return dialog._choice;
    }

    private static SolidColorBrush Brush(string hex) =>
        new((Color)ColorConverter.ConvertFromString(hex));

    private void ApplyKind(DialogKind kind)
    {
        var (ring, edge, glyphColour, glyph) = kind switch
        {
            DialogKind.Danger => ("#2A1218", "#5C2431", "#FF5C5C", "!"),
            DialogKind.Warning => ("#241C0C", "#4A3A17", "#F0B23C", "!"),
            _ => ("#122246", "#25406E", "#5C8CFF", "i")
        };

        IconRing.Background = Brush(ring);
        IconRing.BorderBrush = Brush(edge);
        IconGlyph.Foreground = Brush(glyphColour);
        IconGlyph.Text = glyph;

        // A destructive confirmation should not have a friendly blue button.
        // Swapping the style, not the Background: the primary template holds
        // its own brush so the hover fade can animate it, and ignores any
        // Background set from outside.
        if (kind == DialogKind.Danger && TryFindResource("DangerButton") is Style danger)
            PrimaryButton.Style = danger;
    }

    private void Primary_Click(object sender, RoutedEventArgs e) => Close(DialogChoice.Primary);
    private void Secondary_Click(object sender, RoutedEventArgs e) => Close(DialogChoice.Secondary);
    private void Cancel_Click(object sender, RoutedEventArgs e) => Close(DialogChoice.Cancel);

    private void Close(DialogChoice choice)
    {
        _choice = choice;
        Close();
    }
}
