using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using BssManager.Models;
using BssManager.Services;

namespace BssManager.Views;

/// <summary>What the macro dialog was told to save.</summary>
/// <param name="Settings">The per-alt macro configuration.</param>
/// <param name="PrivateServerUrl">The link to join, or empty for public servers.</param>
/// <param name="ApplyToAll">Give every RDP that same private server.</param>
public record MacroChoice(MacroSettings Settings, string PrivateServerUrl, bool ApplyToAll);

/// <summary>
/// Everything about how one alt plays: the macro install, its per-alt settings,
/// and the server it joins.
///
/// The private server lives here rather than on the RDP card because it is not
/// a property of the session -- it is part of what this alt is set up to do,
/// alongside its field and pattern. Launching an alt then needs no decisions:
/// the session opens, joins that server, and starts the macro.
/// </summary>
public partial class MacroDialog : Window
{
    private readonly KairosService _kairos;
    private readonly AltProfile _alt;
    private readonly long _fallbackPlaceId;
    private bool _installing;
    private bool _saved;

    private MacroDialog(KairosService kairos, AltProfile alt, long fallbackPlaceId)
    {
        InitializeComponent();
        _kairos = kairos;
        _alt = alt;
        _fallbackPlaceId = fallbackPlaceId;

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };
    }

    /// <summary>
    /// Shows the dialog. Returns what to save, or null if cancelled. Installing
    /// is immediate and is not undone by cancelling -- downloading the macro is
    /// not a setting.
    /// </summary>
    public static MacroChoice? Show(
        Window? owner, KairosService kairos, AltProfile alt, long fallbackPlaceId)
    {
        var dialog = new MacroDialog(kairos, alt, fallbackPlaceId);

        dialog.HeaderText.Text = $"Macro for {alt.DisplayName}";
        dialog.Load();

        if (owner is not null && owner.IsLoaded) dialog.Owner = owner;
        else dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;

        dialog.ShowDialog();
        return dialog._saved ? dialog.Read() : null;
    }

    // ------------------------------------------------------------------ load

    private void Load()
    {
        var m = _alt.Macro;

        FieldBox.ItemsSource = MacroSettings.Fields;
        FieldBox.SelectedItem = Pick(MacroSettings.Fields, m.DefaultField, "pepper");

        SprinklerBox.ItemsSource = MacroSettings.SprinklerLocations;
        SprinklerBox.SelectedItem = Pick(MacroSettings.SprinklerLocations, m.SprinklerLocation, "Center");

        RotationBox.ItemsSource = MacroSettings.RotationDirections;
        RotationBox.SelectedItem = Pick(MacroSettings.RotationDirections, m.RotationDirection, "Right");

        AltNumberBox.Text = m.AltNumber.ToString(CultureInfo.InvariantCulture);
        HiveSlotBox.Text = m.HiveSlot.ToString(CultureInfo.InvariantCulture);
        MovespeedBox.Text = m.Movespeed.ToString(CultureInfo.InvariantCulture);
        PatternSizeBox.Text = m.PatternSize.ToString(CultureInfo.InvariantCulture);
        PatternWidthBox.Text = m.PatternWidth.ToString(CultureInfo.InvariantCulture);
        SprinklerDistanceBox.Text = m.SprinklerDistance.ToString(CultureInfo.InvariantCulture);
        RotationAmountBox.Text = m.RotationAmount.ToString(CultureInfo.InvariantCulture);

        ClaimHiveBox.IsChecked = m.ClaimHive;
        ShiftLockBox.IsChecked = m.ShiftLock;
        UseToolBox.IsChecked = m.UseTool;

        LinkBox.Text = _alt.PrivateServerUrl;

        RefreshInstallState();
    }

    /// <summary>
    /// The pattern list comes from the installed copy, so it can only be filled
    /// in once there is one. A saved pattern that is no longer on disk is kept
    /// in the list rather than silently swapped for another.
    /// </summary>
    private void LoadPatterns()
    {
        var patterns = _kairos.AvailablePatterns(_alt.WindowsUsername).ToList();

        var saved = _alt.Macro.Pattern;
        if (saved.Length > 0 && !patterns.Contains(saved, StringComparer.OrdinalIgnoreCase))
            patterns.Insert(0, saved);

        PatternBox.ItemsSource = patterns;
        PatternBox.SelectedItem = Pick(patterns, saved, patterns.FirstOrDefault() ?? "GeneralBooster");
    }

    private static string Pick(IReadOnlyList<string> options, string current, string fallback) =>
        options.FirstOrDefault(o => string.Equals(o, current, StringComparison.OrdinalIgnoreCase))
        ?? fallback;

    private void RefreshInstallState()
    {
        var installed = KairosService.IsInstalled(_alt.WindowsUsername);

        InstallDot.Background = installed
            ? (Brush)FindResource("Ok")
            : (Brush)FindResource("Warn");

        InstallTitle.Text = installed
            ? $"Kairos {KairosService.Version} is installed"
            : "Kairos is not installed for this alt";

        InstallDetail.Text = installed
            ? "Settings below are written to it every time this alt launches."
            : "About 6 MB, downloaded from the project's release page. Each alt gets its own copy — they cannot share one.";

        InstallButton.Content = installed ? "Reinstall" : "Set up macro";
        InstallButton.IsEnabled = !_installing;

        // Disabled alone is not enough: the text and combo styles barely change
        // when disabled, so the fields read as editable and then swallow input.
        SettingsArea.IsEnabled = installed;
        SettingsArea.Opacity = installed ? 1.0 : 0.4;

        LoadPatterns();
        Validate();
    }

    // ------------------------------------------------------------ validation

    private void LinkBox_TextChanged(object sender, RoutedEventArgs e) => Validate();

    /// <summary>
    /// Parses the link as it is typed. A link Roblox will not accept is worth
    /// catching here, not thirty seconds into a launch when the ticket has
    /// already been spent.
    /// </summary>
    private bool Validate()
    {
        var linkOk = PrivateServerLink.TryParse(LinkBox.Text, _fallbackPlaceId, out _, out var problem);

        // Only failures say anything. Reading a good link back at you is noise:
        // you just pasted it, and Save staying live already says it was taken.
        ProblemBox.Visibility = linkOk ? Visibility.Collapsed : Visibility.Visible;
        ProblemText.Text = problem;

        SaveButton.IsEnabled = linkOk && !_installing
                               && KairosService.IsInstalled(_alt.WindowsUsername);
        return linkOk;
    }

    // --------------------------------------------------------------- install

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        if (_installing) return;

        _installing = true;
        InstallButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        InstallProblem.Text = "";

        var progress = new Progress<string>(text => InstallDetail.Text = text);

        var (ok, message) = await _kairos.InstallAsync(_alt.WindowsUsername, progress);

        _installing = false;
        RefreshInstallState();

        if (!ok) InstallProblem.Text = message;
    }

    // ------------------------------------------------------------------ save

    private MacroChoice Read()
    {
        var m = _alt.Macro.Clone();

        m.DefaultField = FieldBox.SelectedItem as string ?? m.DefaultField;
        m.Pattern = PatternBox.SelectedItem as string ?? m.Pattern;
        m.SprinklerLocation = SprinklerBox.SelectedItem as string ?? m.SprinklerLocation;
        m.RotationDirection = RotationBox.SelectedItem as string ?? m.RotationDirection;

        m.AltNumber = Number(AltNumberBox.Text, m.AltNumber, 1, 50);
        m.HiveSlot = Number(HiveSlotBox.Text, m.HiveSlot, 1, 6);
        m.Movespeed = Decimal(MovespeedBox.Text, m.Movespeed, 1, 200);
        m.PatternSize = Number(PatternSizeBox.Text, m.PatternSize, 1, 10);
        m.PatternWidth = Number(PatternWidthBox.Text, m.PatternWidth, 1, 10);
        m.SprinklerDistance = Number(SprinklerDistanceBox.Text, m.SprinklerDistance, 0, 20);
        m.RotationAmount = Number(RotationAmountBox.Text, m.RotationAmount, 0, 360);

        m.ClaimHive = ClaimHiveBox.IsChecked == true;
        m.ShiftLock = ShiftLockBox.IsChecked == true;
        m.UseTool = UseToolBox.IsChecked == true;

        // Store the normalised form, so what is saved is what gets launched.
        PrivateServerLink.TryParse(LinkBox.Text, _fallbackPlaceId, out var link, out _);

        return new MacroChoice(m, link?.Url ?? "", ApplyToAllBox.IsChecked == true);
    }

    /// <summary>
    /// Clamps rather than rejects. These all feed a macro that will do
    /// something strange with an out-of-range value rather than refuse it, and
    /// blocking Save over a typo in a spinner box is worse than correcting it.
    /// </summary>
    private static int Number(string text, int fallback, int min, int max)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return fallback;

        return Math.Clamp(value, min, max);
    }

    /// <summary>
    /// The same clamp for a value that is allowed to be fractional -- walkspeed.
    /// A comma is accepted as well as a dot, since a paste can carry either, but
    /// it is always read and written back in invariant form.
    /// </summary>
    private static double Decimal(string text, double fallback, double min, double max)
    {
        var cleaned = text.Trim().Replace(',', '.');

        if (!double.TryParse(cleaned, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return fallback;

        return Math.Clamp(value, min, max);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!Validate()) return;

        // Reading a settings area that was never enabled would write whatever
        // the empty controls happen to hold back over real settings.
        if (!SettingsArea.IsEnabled) return;

        _saved = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();
}
