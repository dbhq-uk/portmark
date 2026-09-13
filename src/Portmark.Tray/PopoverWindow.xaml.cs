using System.Diagnostics;
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

        // PORTMARK_STAY keeps the panel up when focus moves elsewhere. It exists for taking
        // documentation screenshots on a busy desktop; users get normal click-away behaviour.
        bool stay = Environment.GetEnvironmentVariable("PORTMARK_STAY") == "1";
        Deactivated += (_, _) =>
        {
            if (!_opening && !stay) Hide();
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
        BuildEmptyPorts();
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

        if (capability.Status == CapabilityStatus.NeedsSetup)
        {
            // A tray app telling its user to open an administrator terminal is not a product.
            // Sell the outcome, offer a button, and let UAC be the consent step it exists to be.
            stack.Children.Add(Styled(new TextBlock { Text = "See charging speeds" }, "Value"));
            stack.Children.Add(Styled(new TextBlock
            {
                Text = "Show how much power each port can deliver and what it is delivering now. "
                     + "Needs one admin approval, and can be turned off again from the tray menu.",
            }, "Caption"));

            var enable = new System.Windows.Controls.Button
            {
                Content = "Enable",
                Margin = new Thickness(0, 9, 0, 0),
                Padding = new Thickness(14, 5, 14, 6),
                HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
                FontWeight = FontWeights.SemiBold,
            };
            enable.SetResourceReference(StyleProperty, "Flat");
            enable.Background = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["Accent"];
            enable.Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["Surface"];
            enable.Click += (_, _) =>
            {
                enable.IsEnabled = false;
                enable.Content = "Waiting for approval...";
                RunElevated("--enable", Refresh);
            };
            stack.Children.Add(enable);

            Items.Children.Add(Card(Row("IconBolt", stack, "Accent")));
            return;
        }

        stack.Children.Add(Styled(new TextBlock { Text = "Cable details unavailable" }, "Value"));
        stack.Children.Add(Styled(new TextBlock
        {
            Text = "This PC's controller does not report cable information, whichever cable you "
                 + "use. Power and video are unaffected.",
        }, "Caption"));

        Items.Children.Add(Card(Row("IconWarn", stack, "Warn")));
    }

    /// <summary>
    /// Relaunches this executable elevated to flip the extended tier, then runs the follow-up on
    /// the UI thread. Cancelling the UAC prompt is a decision, not an error, so it is silent.
    /// </summary>
    internal static void RunElevated(string verb, Action then)
    {
        Task.Run(() =>
        {
            try
            {
                var psi = new ProcessStartInfo(Environment.ProcessPath!, verb)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                };
                using Process? p = Process.Start(psi);
                p?.WaitForExit();
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                // The user said no at the UAC prompt.
            }

            System.Windows.Application.Current.Dispatcher.Invoke(then);
        });
    }

    private void BuildPower()
    {
        if (_report is null) return;

        foreach (ConnectorReport c in _report.Connectors)
        {
            PowerReport p = c.Power;
            if (!p.DataAvailable || p.MaxAvailableMilliwatts is not int mw || mw <= 0) continue;

            var stack = new StackPanel();

            // "Charging - 15W of 65W" is what a person asks; connector indices and PDO lists are
            // what a controller answers. Translate, and keep the raw form behind the toggle.
            string role = c.PowerDirection == "supplying" ? "Powering a device"
                        : c.PowerDirection == "consuming" ? "Charging this PC"
                        : "Connected";
            string headline = p.Negotiated?.NegotiatedPowerMilliwatts is int now
                ? $"{role} · {now / 1000.0:0.#}W of {mw / 1000.0:0.#}W"
                : $"{role} · up to {mw / 1000.0:0.#}W";

            stack.Children.Add(Styled(new TextBlock { Text = headline }, "Value"));
            stack.Children.Add(Styled(new TextBlock
            {
                Text = _showDetail && c.PartnerType is { } pt ? $"Port {c.Index} · {pt}" : $"Port {c.Index}",
            }, "Caption"));

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

    /// <summary>
    /// Empty is a state, not an absence. If nothing is on the USB-C ports, say so and say what
    /// will happen, instead of leaving a gap where the answer would have been.
    /// </summary>
    private void BuildEmptyPorts()
    {
        if (_report is null) return;
        bool anythingAttached = _report.Connectors.Any(c => c.Connected == true)
                                || _report.Billboards.Count > 0;
        if (anythingAttached || _report.Connectors.Count == 0) return;

        var stack = new StackPanel();
        stack.Children.Add(Styled(new TextBlock { Text = "USB-C ports are empty" }, "Value"));
        stack.Children.Add(Styled(new TextBlock
        {
            Text = "Plug in a charger, dock or device and this updates by itself.",
        }, "Caption"));
        Items.Children.Add(Card(Row("IconPort", stack, "TextMuted")));
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

    /// <summary>
    /// USB class names are firmware vocabulary. "Miscellaneous" and "Wireless Controller" mean
    /// nothing to the person reading the panel, so translate the common ones and admit the rest.
    /// </summary>
    private static string Describe(UsbDeviceReport d) => d.Product ?? d.Manufacturer ?? d.DeviceClass switch
    {
        "Miscellaneous" => "Built-in device",
        "Wireless Controller" => "Bluetooth radio",
        "Human Interface Device" => "Input device",
        "declared per interface" => "USB device",
        "Billboard" => "USB-C adapter",
        var other => other,
    };

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
