using System.Windows.Automation;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using BssManager.Models;

namespace BssManager.Views;

/// <summary>
/// The health checks drawn as a web rather than a list.
///
/// A list says eleven things are true. It does not say that the listener is
/// pointless without the service, or that the ini only matters because the
/// wrapper reads it. Those relationships are the whole of RDP troubleshooting,
/// and a list is the one shape that cannot show them.
///
/// So: a hub in the middle, a ring of the checks multi-session actually
/// depends on around it, an outer ring of the ones that only affect how well a
/// session runs once it is up, spokes from every node to the hub and strands
/// around each ring. Each strand is coloured by the worse of the two nodes it
/// joins, so a broken dependency is a red line running into the middle and you
/// can see which half of the machine is at fault without reading a word.
/// </summary>
public class HealthWeb : Canvas
{
    // The two rings, clockwise from the top, each check paired with the short
    // label the diagram uses. The full name is thirty-odd characters and eleven
    // of those side by side is a page of overlapping text, so the web says
    // "rdpwrap.ini" and the inspector beside it says the whole thing.
    //
    // A check whose name is in neither list still appears -- it joins the outer
    // ring under its own name -- so a check added to the service later cannot
    // silently vanish off the diagram.
    private static readonly (string Name, string Short)[] CoreRing =
    {
        ("Terminal Services running", "TermService"),
        ("Remote Desktop connections allowed", "RDP enabled"),
        ("Listening on port 3389", "Port 3389"),
        ("RDP Wrapper installed", "RDP Wrapper"),
        ("TermService points at the wrapper", "Service hooked"),
        ("rdpwrap.ini supports this Windows build", "rdpwrap.ini"),
        ("Multiple sessions per user allowed", "Multi-user")
    };

    private static readonly (string Name, string Short)[] OuterRing =
    {
        ("Minimised sessions keep rendering", "Minimise fix"),
        ("New alts configure themselves", "Alt setup"),
        ("Roblox is staged for new alts", "Roblox staged"),
        ("Launch prompts suppressed", "Signed .rdp")
    };

    private const double NodeWidth = 116;
    private const double CoreOrb = 40;
    private const double OuterOrb = 30;
    private const double HubOrb = 74;

    private readonly Dictionary<FrameworkElement, Ellipse> _halos = new();

    public HealthWeb()
    {
        ClipToBounds = false;
        SizeChanged += (_, _) => Rebuild();
    }

    // ------------------------------------------------------------ properties

    public static readonly DependencyProperty ChecksProperty = DependencyProperty.Register(
        nameof(Checks), typeof(IEnumerable<HealthCheck>), typeof(HealthWeb),
        new PropertyMetadata(null, OnChecksChanged));

    public IEnumerable<HealthCheck>? Checks
    {
        get => (IEnumerable<HealthCheck>?)GetValue(ChecksProperty);
        set => SetValue(ChecksProperty, value);
    }

    /// <summary>
    /// The node the inspector is describing. Two-way: the web sets it when a
    /// node is clicked, and honours it when something else does the choosing --
    /// which is how a fresh scan can drop you straight on the worst problem.
    /// </summary>
    public static readonly DependencyProperty SelectedProperty = DependencyProperty.Register(
        nameof(Selected), typeof(HealthCheck), typeof(HealthWeb),
        new FrameworkPropertyMetadata(null,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnSelectedChanged));

    public HealthCheck? Selected
    {
        get => (HealthCheck?)GetValue(SelectedProperty);
        set => SetValue(SelectedProperty, value);
    }

    private static void OnChecksChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var web = (HealthWeb)d;

        if (e.OldValue is INotifyCollectionChanged old) old.CollectionChanged -= web.OnCollectionChanged;
        if (e.NewValue is INotifyCollectionChanged fresh) fresh.CollectionChanged += web.OnCollectionChanged;

        web.Rebuild();
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => Rebuild();

    private static void OnSelectedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((HealthWeb)d).ShowSelection();

    // ---------------------------------------------------------------- drawing

    private void Rebuild()
    {
        Children.Clear();
        _halos.Clear();

        var checks = Checks?.ToList() ?? new List<HealthCheck>();
        if (checks.Count == 0 || ActualWidth < 80 || ActualHeight < 80) return;

        var core = Order(checks, CoreRing, inner: true);
        var outer = Order(checks, OuterRing, inner: false);

        var cx = ActualWidth / 2;
        var cy = ActualHeight / 2;

        // Ellipse rings, not circles: the space this sits in is far wider than
        // it is tall, and a circle in it would leave the sides empty and crush
        // the labels together top and bottom.
        var corePoints = Ring(core.Count, cx, cy, ActualWidth * 0.27, ActualHeight * 0.27, -90);
        var outerPoints = Ring(outer.Count, cx, cy, ActualWidth * 0.435, ActualHeight * 0.43,
                               -90 + 180.0 / Math.Max(outer.Count, 1));

        var hub = new Point(cx, cy);
        var worst = Worst(checks);

        // Strands first so every node sits on top of its own lines.
        for (var i = 0; i < core.Count; i++)
            AddStrand(corePoints[i], hub, core[i].Check.State, straight: true);

        for (var i = 0; i < core.Count; i++)
            AddStrand(corePoints[i], corePoints[(i + 1) % core.Count],
                      Worse(core[i].Check.State, core[(i + 1) % core.Count].Check.State),
                      straight: false, hub: hub);

        for (var i = 0; i < outer.Count; i++)
        {
            // Each outer node hangs off whichever core node it is nearest to,
            // which keeps the outer ring from floating as a second halo with no
            // connection to the middle.
            var anchor = Nearest(outerPoints[i], corePoints);
            AddStrand(outerPoints[i], corePoints[anchor],
                      Worse(outer[i].Check.State, core[anchor].Check.State), straight: true);
        }

        for (var i = 0; i < outer.Count && outer.Count > 2; i++)
            AddStrand(outerPoints[i], outerPoints[(i + 1) % outer.Count],
                      Worse(outer[i].Check.State, outer[(i + 1) % outer.Count].Check.State),
                      straight: false, hub: hub);

        AddNode(hub, HubOrb, "Multi-session", worst, null, 13.5);

        for (var i = 0; i < core.Count; i++)
            AddNode(corePoints[i], CoreOrb, core[i].Label, core[i].Check.State, core[i].Check, 11);

        for (var i = 0; i < outer.Count; i++)
            AddNode(outerPoints[i], OuterOrb, outer[i].Label, outer[i].Check.State, outer[i].Check, 10);

        ShowSelection();
    }

    /// <summary>
    /// The named checks in ring order, each with its short label, then anything
    /// unaccounted for. A check the service grows later lands in the outer ring
    /// under its full name rather than disappearing.
    /// </summary>
    private static List<(HealthCheck Check, string Label)> Order(
        List<HealthCheck> all, (string Name, string Short)[] ring, bool inner)
    {
        var picked = new List<(HealthCheck, string)>();

        foreach (var (name, label) in ring)
        {
            var match = all.FirstOrDefault(c => c.Name == name);
            if (match is not null) picked.Add((match, label));
        }

        if (!inner)
        {
            var known = CoreRing.Concat(OuterRing).Select(r => r.Name).ToHashSet();
            picked.AddRange(all.Where(c => !known.Contains(c.Name)).Select(c => (c, c.Name)));
        }

        return picked;
    }

    private static Point[] Ring(int count, double cx, double cy, double rx, double ry, double startDegrees)
    {
        var points = new Point[count];
        for (var i = 0; i < count; i++)
        {
            var angle = (startDegrees + 360.0 * i / count) * Math.PI / 180;
            points[i] = new Point(cx + rx * Math.Cos(angle), cy + ry * Math.Sin(angle));
        }
        return points;
    }

    private static int Nearest(Point from, Point[] candidates)
    {
        var best = 0;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < candidates.Length; i++)
        {
            var dx = candidates[i].X - from.X;
            var dy = candidates[i].Y - from.Y;
            var distance = dx * dx + dy * dy;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = i;
        }
        return best;
    }

    private void AddStrand(Point a, Point b, HealthState state,
                           bool straight, Point? hub = null)
    {
        Geometry geometry;

        if (straight || hub is null)
        {
            geometry = new LineGeometry(a, b);
        }
        else
        {
            // Ring strands bow away from the middle. Straight chords would cut
            // the corners off and read as a polygon; a web sags outward.
            var mid = new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2);
            var control = new Point(mid.X + (mid.X - hub.Value.X) * 0.16,
                                    mid.Y + (mid.Y - hub.Value.Y) * 0.16);

            var figure = new PathFigure { StartPoint = a };
            figure.Segments.Add(new QuadraticBezierSegment(control, b, true));
            var path = new PathGeometry();
            path.Figures.Add(figure);
            geometry = path;
        }

        var trouble = state is HealthState.Failed or HealthState.Warning;

        var strand = new Path
        {
            Data = geometry,
            StrokeThickness = trouble ? 1.7 : 1.1,
            IsHitTestVisible = false,
            Stroke = new SolidColorBrush(trouble
                ? Colour(state, 0xC0)
                : Color.FromArgb(0x72, 0x35, 0x44, 0x60))
        };

        if (state == HealthState.Failed)
        {
            // A broken dependency gets a current running along it. Of everything
            // on the page this is the one thing that should catch the eye from
            // the far side of the room.
            strand.StrokeDashArray = new DoubleCollection { 4, 4 };
            strand.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation
            {
                From = 0,
                To = -8,
                Duration = TimeSpan.FromSeconds(0.9),
                RepeatBehavior = RepeatBehavior.Forever
            });
        }

        Children.Add(strand);
    }

    private void AddNode(Point centre, double orb, string label, HealthState state,
                         HealthCheck? check, double fontSize)
    {
        var ring = new Ellipse
        {
            Fill = new SolidColorBrush(Colour(state, 0x24)),
            Stroke = new SolidColorBrush(Colour(state, 0x88)),
            StrokeThickness = 1.4
        };

        // The halo is its own childless shape. Nothing carrying text may own an
        // animated surface, or the text loses subpixel antialiasing.
        var halo = new Ellipse
        {
            Margin = new Thickness(-6),
            Stroke = new SolidColorBrush(Colour(state, 0xFF)),
            StrokeThickness = 1.6,
            Opacity = 0,
            IsHitTestVisible = false
        };

        var glyph = new TextBlock
        {
            Text = StatusPalette.Mark(state),
            FontFamily = Font("IconFont", "Segoe Fluent Icons"),
            FontSize = orb * 0.36,
            Foreground = new SolidColorBrush(StatusPalette.For(state)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        var orbBox = new Grid { Width = orb, Height = orb, HorizontalAlignment = HorizontalAlignment.Center };
        orbBox.Children.Add(ring);
        orbBox.Children.Add(halo);
        orbBox.Children.Add(glyph);

        var caption = new TextBlock
        {
            Text = label,
            Width = NodeWidth,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(0, 8, 0, 0),
            FontSize = fontSize,
            FontWeight = check is null ? FontWeights.SemiBold : FontWeights.Normal,
            FontFamily = Font("UiFont", "Segoe UI"),
            Foreground = new SolidColorBrush(check is null
                ? Color.FromRgb(0xE9, 0xEE, 0xF8)
                : Color.FromRgb(0x95, 0xA2, 0xBA))
        };

        var stack = new StackPanel { Width = NodeWidth };
        stack.Children.Add(orbBox);
        stack.Children.Add(caption);

        // A Button rather than a Border with a click handler. The nodes are the
        // only way to drive this page, so they have to be reachable by tab and
        // by a screen reader, and the automation name is the check's full name
        // rather than the abbreviation the diagram shows.
        var node = new Button
        {
            Content = stack,
            Template = NodeTemplate,
            Cursor = Cursors.Hand,
            FocusVisualStyle = null,
            ToolTip = string.IsNullOrWhiteSpace(check?.Detail) ? null : check!.Detail
        };

        AutomationProperties.SetName(node, check?.Name ?? "Multi-session");
        if (!string.IsNullOrWhiteSpace(check?.Detail))
            AutomationProperties.SetHelpText(node, check!.Detail);

        node.Click += (_, _) => Selected = check;
        node.MouseEnter += (_, _) => Fade(halo, check == Selected ? 1 : 0.45);
        node.MouseLeave += (_, _) => Fade(halo, check == Selected ? 1 : 0);
        node.GotKeyboardFocus += (_, _) => Fade(halo, check == Selected ? 1 : 0.45);
        node.LostKeyboardFocus += (_, _) => Fade(halo, check == Selected ? 1 : 0);

        _halos[node] = halo;
        if (check is not null) node.Tag = check;

        Children.Add(node);

        // Measured, then placed by its orb's centre: the caption below is a
        // different height on every node and centring the whole box instead
        // would leave the orbs sitting off the ring.
        node.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        SetLeft(node, centre.X - NodeWidth / 2);
        SetTop(node, centre.Y - orb / 2);
    }

    /// <summary>
    /// A button with no chrome of its own: the node draws itself, and all the
    /// button contributes is hit testing, focus and an invoke.
    /// </summary>
    private static readonly ControlTemplate NodeTemplate = BuildNodeTemplate();

    private static ControlTemplate BuildNodeTemplate()
    {
        var surface = new FrameworkElementFactory(typeof(Border));
        surface.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        surface.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));

        var template = new ControlTemplate(typeof(Button)) { VisualTree = surface };
        template.Seal();
        return template;
    }

    private void ShowSelection()
    {
        foreach (var (node, halo) in _halos)
        {
            var chosen = Selected is not null && ReferenceEquals(node.Tag, Selected);
            Fade(halo, chosen ? 1 : 0);
        }
    }

    private static void Fade(UIElement element, double to) =>
        element.BeginAnimation(OpacityProperty, new DoubleAnimation
        {
            To = to,
            Duration = TimeSpan.FromMilliseconds(150)
        });

    // ----------------------------------------------------------------- colour

    private static Color Colour(HealthState state, byte alpha)
    {
        var c = StatusPalette.For(state);
        return Color.FromArgb(alpha, c.R, c.G, c.B);
    }

    private static HealthState Worse(HealthState a, HealthState b) =>
        Rank(a) <= Rank(b) ? a : b;

    private static HealthState Worst(IEnumerable<HealthCheck> checks) =>
        checks.Select(c => c.State).OrderBy(Rank).FirstOrDefault(HealthState.Ok);

    private static int Rank(HealthState state) => state switch
    {
        HealthState.Failed => 0,
        HealthState.Warning => 1,
        HealthState.Unknown => 2,
        _ => 3
    };

    private static string Verdict(IReadOnlyCollection<HealthCheck> checks)
    {
        var failed = checks.Count(c => c.State == HealthState.Failed);
        var warned = checks.Count(c => c.State == HealthState.Warning);

        if (failed > 0) return "Multi-session broken";
        if (warned > 0) return "Multi-session working";
        return "Multi-session healthy";
    }

    private FontFamily Font(string key, string fallback) =>
        TryFindResource(key) as FontFamily ?? new FontFamily(fallback);
}
