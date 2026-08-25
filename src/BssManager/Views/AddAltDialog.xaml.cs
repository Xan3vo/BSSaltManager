using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BssManager.Services;
using BssManager.ViewModels;

namespace BssManager.Views;

public partial class AddAltDialog : Window
{
    private readonly LocalUserService _users = new();
    private bool _adoptExisting;

    public NewAltRequest? Result { get; private set; }

    public AddAltDialog()
    {
        InitializeComponent();
        UsernameBox.Focus();

        // The dialog draws its own chrome, so there is no system caption to
        // drag it by. Anywhere on the surface works instead.
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
    }

    private void UsernameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var name = UsernameBox.Text.Trim();

        if (string.IsNullOrEmpty(name))
        {
            _adoptExisting = false;
            UsernameNote.Text = "Letters and numbers only, no spaces.";
            CreateButton.Content = "Create alt";
            return;
        }

        // Adopting an account that already exists is the common case for anyone
        // who set alts up by hand before finding this app. Detect it up front so
        // the button says what it will actually do.
        _adoptExisting = _users.UserExists(name);

        UsernameNote.Text = _adoptExisting
            ? $"'{name}' already exists on this machine. It will be adopted: its password is replaced with a generated one so sessions can log in unattended, and it is added to Remote Desktop Users. Its files are untouched."
            : "Letters and numbers only, no spaces.";

        CreateButton.Content = _adoptExisting ? "Adopt account" : "Create alt";
    }

    private void ResolutionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CustomSizePanel is null) return;
        CustomSizePanel.Visibility = SelectedTag() == "custom" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        var username = UsernameBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(username))
        {
            MessageDialog.Show("Add alt", "A Windows account name is required.",
                primary: "OK", kind: DialogKind.Warning);
            return;
        }

        // Windows rejects these outright; catching it here beats a raw Win32 error.
        if (!Regex.IsMatch(username, @"^[A-Za-z0-9._-]{1,20}$"))
        {
            MessageDialog.Show("That name will not work",
                "Use 1-20 characters: letters, numbers, dot, dash or underscore. No spaces.",
                primary: "OK", kind: DialogKind.Warning);
            return;
        }

        if (!TryGetSize(out var width, out var height))
        {
            MessageDialog.Show("Session size out of range",
                "Enter a size between 640x480 and 3840x2160.",
                primary: "OK", kind: DialogKind.Warning);
            return;
        }

        Result = new NewAltRequest(
            DisplayName: DisplayNameBox.Text.Trim(),
            Username: username,
            Width: width,
            Height: height,
            HideFromLoginScreen: HideFromLoginBox.IsChecked == true,
            AdoptExisting: _adoptExisting);

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private string SelectedTag() =>
        (ResolutionBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "1280x720";

    private bool TryGetSize(out int width, out int height)
    {
        var tag = SelectedTag();

        if (tag != "custom")
        {
            var parts = tag.Split('x');
            width = int.Parse(parts[0]);
            height = int.Parse(parts[1]);
            return true;
        }

        var okWidth = int.TryParse(CustomWidthBox.Text.Trim(), out width);
        var okHeight = int.TryParse(CustomHeightBox.Text.Trim(), out height);

        return okWidth && okHeight
               && width is >= 640 and <= 3840
               && height is >= 480 and <= 2160;
    }
}
