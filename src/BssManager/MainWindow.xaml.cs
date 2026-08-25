using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using BssManager.ViewModels;
using BssManager.Views;

namespace BssManager;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm = new();

    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        // The view model asks for a new alt; the window is what knows how to
        // put a dialog on screen.
        _vm.PromptForNewAlt = () =>
        {
            var dialog = new AddAltDialog { Owner = this };
            return dialog.ShowDialog() == true ? dialog.Result : null;
        };

        // The marker cannot be placed before layout has run: every nav item is
        // still zero high, so every offset would come out the same.
        Loaded += (_, _) => MoveNavIndicator(animate: false);

    }

    // ------------------------------------------------------------ navigation

    private void Nav_Checked(object sender, RoutedEventArgs e) => MoveNavIndicator(animate: true);

    /// <summary>
    /// Slides the rail's marker to whichever item is current. One marker that
    /// travels reads as the page moving; a marker per item that switches on
    /// and off reads as two separate things blinking.
    /// </summary>
    private void MoveNavIndicator(bool animate)
    {
        if (NavPanel is null || NavIndicator is null) return;

        var current = NavPanel.Children
            .OfType<ToggleButton>()
            .FirstOrDefault(b => b.IsChecked == true);

        if (current is null || current.ActualHeight <= 0) return;

        // Measured against the panel rather than assumed from the item height:
        // the items carry margins, and guessing the pitch would drift.
        var top = current.TranslatePoint(new Point(0, 0), NavPanel).Y;
        var target = top + (current.ActualHeight - NavIndicator.Height) / 2;

        var transform = (TranslateTransform)NavIndicator.RenderTransform;

        if (!animate)
        {
            transform.Y = target;
            return;
        }

        transform.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(260),
            EasingFunction = new QuarticEase { EasingMode = EasingMode.EaseOut }
        });
    }

    /// <summary>The banner says multi-session is broken; this is where it is fixed.</summary>
    private void GoToHealth_Click(object sender, RoutedEventArgs e) =>
        _vm.ActiveTab = MainTab.Health;

    // --------------------------------------------------------- window chrome

    private void Minimise_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void Maximise_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// A maximised WindowChrome window covers the work area plus the invisible
    /// resize border, so the content is inset by however far it actually
    /// spills. The glyph swaps too: a maximised window offers restore.
    /// </summary>
    private void Window_StateChanged(object sender, EventArgs e)
    {
        var maximised = WindowState == WindowState.Maximized;

        Root.Margin = MaximiseFix.Overhang(this);

        MaxGlyph.Text = maximised ? "" : "";
        MaxButton.ToolTip = maximised ? "Restore" : "Maximise";
    }

    // ------------------------------------------------------------- card menu

    /// <summary>
    /// Opens a card's overflow menu. A ContextMenu normally waits for a
    /// right-click, and nothing about this button suggests that.
    /// </summary>
    private void More_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;

        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.HorizontalOffset = -140;
        menu.IsOpen = true;
    }

    /// <summary>
    /// A hidden RDP window has no taskbar button and no Alt-Tab entry, and it
    /// outlives this app. Leaving one hidden on the way out would strand it.
    /// </summary>
    protected override void OnClosed(EventArgs e)
    {
        _vm.RevealWindowsOnExit();
        base.OnClosed(e);
    }
}
