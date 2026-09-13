using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Portmark.Core;
using Portmark.Core.Model;
using Portmark.Core.Usb;

namespace Portmark.Tray;

/// <summary>
/// The panel the tray icon opens.
///
/// Built around one idea: a glance should answer the question. Each row is an icon, a value, and
/// at most a short caption. The prose, the raw bytes and the per-PDO breakdown live behind the
/// detail toggle, because the person who wants those already knows to look for them, and everyone
/// else is trying to find out whether their charger is doing 65W.
///
/// The exception is when the machine cannot answer. Then it says so, in words, at the top. Showing
/// a blank panel and letting someone conclude their cable is broken is the failure this project
/// exists to avoid, and it is worth the space.
/// </summary>
public partial class PopoverWindow : Window
{
    private bool _showDetail;

    /// <summary>
    /// Suppresses the click-away close while the panel is still opening. Deactivated fires before
    /// a newly shown window has taken focus, so without this the panel hides itself the instant it
    /// appears and looks like it never opened at all.
    /// </summary>
    private bool _opening;
    private PortmarkReport? _report;
    private IReadOnlyList<UsbDeviceReport> _devices = [];

    public PopoverWindow()
    {
        InitializeComponent();
        Deactivated += (_, _) =>
        {
            if (!_opening) Hide();
        };
    }

    public void Refresh()
    {
        StatusText.Text = "Reading...";
        try
        {
            _report = PortmarkReader.Read();
            _devices = UsbDeviceScanner.ScanAll();
        }
        catch (Exception ex)
        {
            _report = null;
            StatusText.Text = $"Could not read: {ex.Message}";
        }

        Build();
    }

    private void Build()
    {
        Items.Children.Clear();
        if (_report is null) return;

        BuildLimitation(_report.Capability);
        BuildPower();
        BuildVideo();
        BuildDevices();

        int slow = _devices.Count(d => d.IsUnderperforming);
        StatusText.Text = slow > 0
            ? $"{_devices.Count} devices · {slow} below capability"
            : $"{_devices.Count} devices · all at full speed";
    }

    /// <summary>The one place worth spending words: when this PC simply cannot tell you.</summary>
    private void BuildLimitation(CapabilityReport capability)
    {
        bool noCableData = capability.Features?.CableDetailsAvailable == false;
        if (capability.Status == CapabilityStatus.Ok && !noCableData) return;

        var stack = new StackPanel();
        stack.Children.Add(Styled(new TextBlock
        {
            Text = noCableData ? "Cable details unavailable" : "Setup needed",
        }, "Value"));

        stack.Children.Add(Styled(new TextBlock
        {
            Text = noCableData
                ? "This PC's controller does not report cable information, whichever cable you use. "
                + "Power and video below are unaffected."
                : capability.Remedy ?? capability.Explanation,
        }, "Caption"));

        Items.Children.Add(Card(Row("IconWarn", stack, "Warn")));
    }

    private void BuildPower()
    {
        if (_report is null) return;

        foreach (ConnectorReport c in _report.Connectors)
        {
            PowerReport p = c.Power;
            if (!p.DataAvailable || p.MaxAvailableMilliwatts is not int mw || mw <= 0) continue;

            var stack = new StackPanel();
            string headline = p.Negotiated?.NegotiatedPowerMilliwatts is int now
                ? $"{mw / 1000.0:0.#}W available · {now / 1000.0:0.#}W drawing"
                : $"{mw / 1000.0:0.#}W available";

            stack.Children.Add(Styled(new TextBlock { Text = headline }, "Value"));
            stack.Children.Add(Styled(new TextBlock { Text = $"Port {c.Index}" }, "Caption"));

            if (_showDetail)
                foreach (PowerObjectReport pdo in p.PartnerSource.Where(x => x.Kind != "unrecognised"))
                    stack.Children.Add(Styled(new TextBlock { Text = pdo.Display }, "Caption"));

            Items.Children.Add(Card(Row("IconBolt", stack, "Accent")));
        }
    }

    private void BuildVideo()
    {
        if (_report is null) return;

        foreach (BillboardReport b in _report.Billboards)
        {
            var stack = new StackPanel();
            stack.Children.Add(Styled(new TextBlock
            {
                Text = b.CarriesVideo ? "Video active" : b.SupportsVideo ? "Video idle" : "No video",
            }, "Value"));

            stack.Children.Add(Styled(new TextBlock
            {
                Text = b.Modes.Count > 0 ? b.Modes[0].Name : "No alternate modes",
            }, "Caption"));

            if (_showDetail)
                foreach (AlternateModeReport m in b.Modes)
                    stack.Children.Add(Styled(new TextBlock { Text = m.State }, "Caption"));

            Items.Children.Add(Card(Row("IconDisplay", stack, b.CarriesVideo ? "Accent" : "TextMuted")));
        }
    }

    private void BuildDevices()
    {
        if (_devices.Count == 0) return;

        foreach (UsbDeviceReport d in _devices.Where(x => x.IsUnderperforming))
        {
            var slow = new StackPanel();
            slow.Children.Add(Styled(new TextBlock { Text = Describe(d) }, "Value"));
            slow.Children.Add(Styled(new TextBlock
            {
                Text = _showDetail ? d.LinkDiagnosis ?? "" : $"{d.Speed} · could be faster",
            }, "Caption"));
            Items.Children.Add(Card(Row("IconWarn", slow, "Warn")));
        }

        var list = new StackPanel();
        list.Children.Add(Styled(new TextBlock { Text = $"{_devices.Count} devices" }, "Value"));

        if (_showDetail)
        {
            foreach (UsbDeviceReport d in _devices.OrderBy(x => x.IsHub ? 1 : 0))
                list.Children.Add(Styled(new TextBlock { Text = $"{Describe(d)} · {d.Speed}" }, "Caption"));
        }
        else
        {
            string names = string.Join(", ", _devices.Where(x => !x.IsHub).Take(3).Select(Describe));
            list.Children.Add(Styled(new TextBlock { Text = names }, "Caption"));
        }

        Items.Children.Add(Card(Row("IconChip", list, "TextMuted")));
    }

    private static string Describe(UsbDeviceReport d) => d.Product ?? d.Manufacturer ?? d.DeviceClass;

    /// <summary>An icon in a fixed gutter, then the content, so every row sits on the same grid.</summary>
    private Grid Row(string iconKey, UIElement content, string brushKey)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new Path
        {
            Data = (Geometry)Resources[iconKey],
            Width = 19,
            Height = 19,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 0, 0),
        };

        var brush = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources[brushKey];

        // Closed shapes read better filled, open ones better stroked. Getting this the wrong way
        // round turns the chip into a black square and the bolt into a wireframe.
        if (iconKey is "IconChip" or "IconDisplay")
        {
            icon.Stroke = brush;
            icon.StrokeThickness = 1.7;
        }
        else
        {
            icon.Fill = brush;
        }

        Grid.SetColumn(icon, 0);
        Grid.SetColumn(content, 1);
        grid.Children.Add(icon);
        grid.Children.Add(content);
        return grid;
    }

    private static Border Card(UIElement child)
    {
        var border = new Border { Child = child };
        border.SetResourceReference(StyleProperty, "Card");
        return border;
    }

    private static TextBlock Styled(TextBlock block, string style)
    {
        block.SetResourceReference(StyleProperty, style);
        return block;
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();

    private void OnToggleDetail(object sender, RoutedEventArgs e)
    {
        _showDetail = !_showDetail;
        Build();
    }

    private void OnQuit(object sender, RoutedEventArgs e) => System.Windows.Application.Current.Shutdown();

    /// <summary>Anchors the panel to the notification area rather than the middle of the screen.</summary>
    public void ShowNearTray()
    {
        _opening = true;
        Refresh();
        Show();
        UpdateLayout();

        System.Windows.Forms.Screen screen = System.Windows.Forms.Screen.PrimaryScreen
                                             ?? System.Windows.Forms.Screen.AllScreens[0];
        System.Drawing.Rectangle area = screen.WorkingArea;

        // WorkingArea is in physical pixels and WPF positions in device-independent units, so this
        // must be scaled or the panel lands off-screen on any display above 100%.
        PresentationSource? source = PresentationSource.FromVisual(this);
        double scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        double scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;

        Left = (area.Right / scaleX) - Width;
        Top = (area.Bottom / scaleY) - ActualHeight;

        Activate();

        // Release the guard once the window has settled, so clicking elsewhere closes it again.
        Dispatcher.BeginInvoke(new Action(() => _opening = false),
                               System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }
}
