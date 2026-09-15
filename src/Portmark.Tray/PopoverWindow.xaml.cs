using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Portmark.Core;
using Portmark.Core.Model;
using Portmark.Core.Ucsi;
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

    private DateTime _lastUcsiRead = DateTime.MinValue;

    /// <summary>
    /// USB device enumeration is cheap and safe to repeat. Reading UCSI is neither: it talks to an
    /// embedded controller that misbehaves when polled hard. So the device list refreshes every
    /// time and the UCSI half is reused unless it is stale or the caller insists.
    /// </summary>
    public void Refresh(bool forceUcsi = false)
    {
        StatusText.Text = "Reading...";
        try
        {
            _devices = UsbDeviceScanner.ScanAll();

            bool stale = DateTime.UtcNow - _lastUcsiRead > TimeSpan.FromSeconds(10);
            if (_report is null || stale || forceUcsi)
            {
                // The panel shows no identity, and this refresh also runs on power and device
                // events, so it never sends identity requests to the port controller.
                _report = PortmarkReader.Read(requestIdentity: false);
                _lastUcsiRead = DateTime.UtcNow;
            }
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
                RunElevated("--enable", () => Refresh(forceUcsi: true));
            };
            stack.Children.Add(enable);

            Items.Children.Add(Card(Row("IconBolt", stack, "Accent"),
            "The chips are every power level this supply offers. The highlighted one is the "
          + "contract in force right now, negotiated between the supply and this PC."));
            return;
        }

        stack.Children.Add(Styled(new TextBlock { Text = "Cable details unavailable" }, "Value"));
        stack.Children.Add(Styled(new TextBlock
        {
            Text = "This PC's controller does not report cable information, whichever cable you "
                 + "use. Power and video are unaffected.",
        }, "Caption"));

        Items.Children.Add(Card(Row("IconWarn", stack, "Warn"),
            "portmark asked this PC's port controller what it can report. The controller itself "
          + "declares that cable identification is not supported in its firmware, so it cannot "
          + "describe any cable. That is a limit of this PC, not a sign that a cable is faulty."));
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
            bool supplying = c.PowerDirection == "supplying";

            // The source's list is the one that counts, as in PortmarkReader.Summarise. While this PC
            // supplies, the contract was negotiated against this PC's offer, and the attached
            // device's own offer (MaxAvailableMilliwatts) has nothing to do with it. The first
            // version divided this PC's contract by the device's ceiling, and lit the device's chips
            // by the voltage of an object this PC offered.
            List<PowerObjectReport> offers = supplying ? p.LocalSource : p.PartnerSource;
            int? ceiling = supplying ? PowerDataObject.OfferCeilingMilliwatts(p.LocalSource) : p.MaxAvailableMilliwatts;

            // A port that is giving power out may have no offer of its own to list, but "powering
            // the dock" is exactly as much an answer as "charging at 15W". Skipping it made port 2
            // vanish from the panel entirely.
            if ((!p.DataAvailable || ceiling is not > 0) && c.Connected == true && supplying)
            {
                var give = new StackPanel();
                give.Children.Add(Styled(new TextBlock { Text = "Powering a device" }, "Value"));
                give.Children.Add(Styled(new TextBlock { Text = PortCaption(c) }, "Caption"));
                Items.Children.Add(Card(Row("IconBolt", give, "TextMuted"),
            "This port is supplying power to whatever is plugged into it."));
                continue;
            }

            if (!p.DataAvailable || ceiling is not int mw || mw <= 0) continue;

            var stack = new StackPanel();

            // "Charging - 15W of 65W" is what a person asks; connector indices and PDO lists are
            // what a controller answers. Translate, and keep the raw form behind the toggle. The
            // negotiated figure is the contract, not a measurement of what is flowing.
            string role = supplying ? "Powering a device"
                        : c.PowerDirection == "consuming" ? "Charging this PC"
                        : "Connected";
            string headline = p.Negotiated?.NegotiatedPowerMilliwatts is int now
                ? $"{role} · {now / 1000.0:0.#}W contract of {mw / 1000.0:0.#}W offered"
                : $"{role} · up to {mw / 1000.0:0.#}W offered";

            stack.Children.Add(Styled(new TextBlock { Text = headline }, "Value"));
            stack.Children.Add(Styled(new TextBlock
            {
                Text = PortCaption(c),
            }, "Caption"));

            // The ladder is the substance: every level the supply offers, with the one in force
            // lit. This is what the CLI shows and what the first cut of this panel hid behind a
            // toggle, which made the panel a summary of a tool instead of the tool.
            // The Request names an object by its position in the source's list, so the chip is lit
            // by that position. Matching by voltage could light an object the request never named.
            var ladder = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
            int? activePosition = p.Negotiated?.ObjectPosition;
            foreach (PowerObjectReport pdo in offers.Where(x => x.Kind != "unrecognised"))
            {
                string label = pdo.VoltageMillivolts is int mv && pdo.MaxCurrentMilliamps is int ma
                    ? $"{mv / 1000.0:0.#}V · {ma / 1000.0:0.##}A"
                    : pdo.Display;
                ladder.Children.Add(Chip(label, pdo.Position is int pos && pos == activePosition));
            }
            if (ladder.Children.Count > 0) stack.Children.Add(ladder);

            Items.Children.Add(Card(Row("IconBolt", stack, "Accent"), supplying
                ? "The chips are every power level this PC's port controller reports it offers on this "
                + "port. The highlighted one is the contract in force right now, negotiated between "
                + "this PC and the attached device."
                : "The chips are every power level this supply offers. The highlighted one is the "
                + "contract in force right now, negotiated between the supply and this PC."));
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
                Text = b.CarriesVideo ? "Video active" : b.SupportsVideo ? "Video idle" : b.Truncated ? "Video unknown" : "No video",
            }, "Value"));

            foreach (AlternateModeReport m in b.Modes)
                stack.Children.Add(Styled(new TextBlock
                {
                    Text = $"{ModeLabel(m.Name, m.Svid, m.VendorName)} · {m.State}",
                }, "Caption"));
            if (b.TruncationNote is not null)
                stack.Children.Add(Styled(new TextBlock { Text = b.TruncationNote, TextWrapping = TextWrapping.Wrap }, "Caption"));
            if (b.Modes.Count == 0 && !b.Truncated)
                stack.Children.Add(Styled(new TextBlock { Text = "No alternate modes" }, "Caption"));

            Items.Children.Add(Card(Row("IconDisplay", stack, b.CarriesVideo ? "Accent" : "TextMuted"),
            "This USB-C adapter declares which video modes it supports and whether one is running. "
          + "Entered successfully means your display signal is flowing through this port now."));
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

        var list = new StackPanel();
        list.Children.Add(Styled(new TextBlock { Text = $"{_devices.Count} devices" }, "Value"));

        foreach (UsbDeviceReport d in _devices.OrderBy(x => x.IsHub ? 1 : 0))
            list.Children.Add(DeviceRow(d));

        Items.Children.Add(Card(Row("IconChip", list, "TextMuted"),
            "Everything attached over USB, with the link speed each device actually negotiated. "
          + "Amber means the hub reports the device can go faster than the link it came up on. "
          + "Run portmark usb for what the hub reports about the port and its connector."));
    }

    /// <summary>
    /// USB class names are firmware vocabulary. "Miscellaneous" and "Wireless Controller" mean
    /// nothing to the person reading the panel, so translate the common ones and admit the rest.
    /// </summary>
    private static string PortCaption(ConnectorReport c)
    {
        var bits = new List<string> { $"Port {c.Index}" };
        if (c.Capability is { } cap)
        {
            if (cap.SupportsUsb3) bits.Add("USB 3.x");
            else if (cap.SupportsUsb2) bits.Add("USB 2.0");
            if (cap.SupportsAlternateModes) bits.Add("alt modes");
        }
        if (_staticDetail && c.PartnerType is { } pt) bits.Add(pt);
        return string.Join(" · ", bits);
    }

    // PortCaption is called from an instance context but reads the toggle through a static so it
    // can stay a pure function of its argument.
    private static bool _staticDetail;

    /// <summary>
    /// A vendor ID with the name registered to it in brackets, or the bare ID when the vendor list
    /// names none. The same form as the CLI: the registered name, not the maker.
    /// </summary>
    internal static string VendorLabel(string vendorId, string? vendorName)
        => vendorName is null ? vendorId : $"{vendorId} ({vendorName})";

    /// <summary>
    /// A mode's name, with the registered vendor name added only where the name is the bare SVID,
    /// as the CLI does. A mode with a name of its own, such as DisplayPort, is left as it is.
    /// </summary>
    internal static string ModeLabel(string name, string svid, string? vendorName)
        => vendorName is not null && name.Contains(svid, StringComparison.OrdinalIgnoreCase)
            ? $"{name} ({vendorName})"
            : name;

    /// <summary>"High, 480 Mbps" is two facts; the row only has room for the one that matters.</summary>
    private static string ShortSpeed(string speed)
    {
        int comma = speed.IndexOf(',');
        return comma >= 0 ? speed[(comma + 1)..].Trim() : speed;
    }

    /// <summary>A name on the left, its link speed on the right, and any shortfall spelt out.</summary>
    private Grid DeviceRow(UsbDeviceReport d)
    {
        var grid = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        TextBlock name = Styled(new TextBlock
        {
            Text = Describe(d),
            TextTrimming = TextTrimming.CharacterEllipsis,
        }, d.IsHub ? "Caption" : "Body");

        TextBlock speed = Styled(new TextBlock
        {
            Text = ShortSpeed(d.Speed),
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        }, "Caption");

        if (d.IsUnderperforming)
            speed.Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["Warn"];

        Grid.SetColumn(name, 0);
        Grid.SetColumn(speed, 1);
        grid.Children.Add(name);
        grid.Children.Add(speed);

        if (!d.IsUnderperforming) return grid;

        // The warning belongs with the device, in amber, not in a separate card.
        var outer = new Grid();
        outer.ColumnDefinitions.Add(new ColumnDefinition());
        var stack = new StackPanel { Margin = new Thickness(0, 5, 0, 0) };
        grid.Margin = new Thickness(0);
        stack.Children.Add(grid);
        TextBlock why = Styled(new TextBlock
        {
            // The hub's evidence says the device can go faster; nothing here says why it did not.
            Text = d.ExpectedSpeed is { } could ? $"Capable of {could}, running slower" : "Running below what it reports it can do",
        }, "Caption");
        why.Foreground = (System.Windows.Media.Brush)System.Windows.Application.Current.Resources["Warn"];
        stack.Children.Add(why);
        outer.Children.Add(stack);
        return outer;
    }

    /// <summary>A small rounded tag. The active one is filled with the accent.</summary>
    private static Border Chip(string text, bool active)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 11,
            FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
        };

        var chip = new Border
        {
            Child = block,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 3, 8, 4),
            Margin = new Thickness(0, 0, 6, 6),
        };

        var res = System.Windows.Application.Current.Resources;
        if (active)
        {
            chip.Background = (System.Windows.Media.Brush)res["Accent"];
            block.Foreground = (System.Windows.Media.Brush)res["Surface"];
        }
        else
        {
            chip.Background = (System.Windows.Media.Brush)res["Divider"];
            block.Foreground = (System.Windows.Media.Brush)res["TextMuted"];
        }

        return chip;
    }

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

    /// <summary>
    /// Every card takes a hover explanation. The owner of this project asked twice what two of
    /// the cards meant, and if the owner has to ask, a stranger has no chance: the affordance
    /// belongs on the card, not in a README.
    /// </summary>
    private static Border Card(UIElement child, string? explain = null)
    {
        var border = new Border { Child = child };
        border.SetResourceReference(StyleProperty, "Card");
        if (explain is not null)
        {
            border.ToolTip = new System.Windows.Controls.ToolTip
            {
                Content = new TextBlock { Text = explain, TextWrapping = TextWrapping.Wrap, MaxWidth = 300 },
            };
            ToolTipService.SetInitialShowDelay(border, 350);
        }
        return border;
    }

    private static TextBlock Styled(TextBlock block, string style)
    {
        block.SetResourceReference(StyleProperty, style);
        return block;
    }

    // The refresh button is an explicit request, so it bypasses the staleness check.
    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh(forceUcsi: true);

    private void OnToggleDetail(object sender, RoutedEventArgs e)
    {
        _showDetail = !_showDetail;
        _staticDetail = _showDetail;
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
