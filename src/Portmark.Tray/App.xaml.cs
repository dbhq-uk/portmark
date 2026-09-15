using System.Windows;
using Microsoft.Win32;
using Portmark.Core.Model;
using Portmark.Core.Ucsi;
using Portmark.Core.Usb;
using Forms = System.Windows.Forms;

namespace Portmark.Tray;

/// <summary>
/// The tray application. Lives in the notification area, watches the USB ports, and opens a panel
/// when clicked.
///
/// The watcher is the point of it existing. A CLI answers when asked; this tells you at the moment
/// something changes, which is when you can still do something about it.
/// </summary>
public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon? _tray;
    private PopoverWindow? _popover;
    private CancellationTokenSource? _cancel;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // The elevated half of the Enable button. The popover relaunches this same executable
        // with the runas verb and one of these flags, so the user clicks a button and answers a
        // UAC prompt instead of being told to open an administrator terminal.
        if (e.Args.Contains("--enable") || e.Args.Contains("--disable"))
        {
            bool ok = UcsiDevice.SetExtendedTier(e.Args.Contains("--enable"), out _);
            Shutdown(ok ? 0 : 1);
            return;
        }

        _popover = new PopoverWindow();
        _tray = CreateTrayIcon();
        StartWatching();

        // A USB-C charger is not a USB device and never enumerates on a hub, so the device
        // watcher cannot see one arrive. The AC power source changing is the event that
        // corresponds to plugging a charger in, and it is the only one that does.
        SystemEvents.PowerModeChanged += OnPowerModeChanged;

        // Launching the app is an explicit request to see the ports, so show the panel rather than
        // silently adding an icon the user then has to go and find.
        _popover.ShowNearTray();
    }

    private Forms.NotifyIcon CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open", null, (_, _) => ShowPopover());
        menu.Items.Add("Refresh", null, (_, _) => _popover?.Refresh());
        menu.Items.Add(new Forms.ToolStripSeparator());

        var autostart = new Forms.ToolStripMenuItem("Start with Windows") { CheckOnClick = true };
        autostart.Click += (_, _) => SetAutostart(autostart.Checked);
        menu.Items.Add(autostart);

        // Symmetry with the Enable button in the panel. A switch that is easy to turn on and
        // awkward to turn off is how trust gets spent, so both directions are one click.
        var disable = new Forms.ToolStripMenuItem("Turn off extended data");
        disable.Click += (_, _) => PopoverWindow.RunElevated("--disable", () => _popover?.Refresh());
        menu.Items.Add(disable);

        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Shutdown());

        menu.Opening += (_, _) =>
        {
            autostart.Checked = IsAutostart();
            disable.Visible = UcsiDevice.FindDevices() is { Count: > 0 } d
                              && UcsiDevice.IsTestInterfaceEnabled(d[0]);
        };

        var icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Visible = true,
            Text = "Portmark",
            ContextMenuStrip = menu,
        };

        icon.MouseClick += (_, args) =>
        {
            if (args.Button == Forms.MouseButtons.Left) ShowPopover();
        };

        return icon;
    }

    private static System.Drawing.Icon LoadIcon()
    {
        System.Windows.Resources.StreamResourceInfo? info =
            GetResourceStream(new Uri("portmark.ico", UriKind.Relative));

        // Falling back to the stock application icon is better than failing to start: a tray app
        // that will not appear because of an icon is worse than one with the wrong picture.
        return info is null
            ? System.Drawing.SystemIcons.Application
            : new System.Drawing.Icon(info.Stream);
    }

    private void ShowPopover()
    {
        if (_popover is null) return;

        if (_popover.IsVisible)
        {
            _popover.Hide();
            return;
        }

        _popover.ShowNearTray();
    }

    /// <summary>
    /// Watches in the background and raises a balloon when something changes. Arrivals that came
    /// up slower than the device is capable of are called out specifically, because that is the
    /// case the user can actually act on by changing the cable.
    /// </summary>
    private void StartWatching()
    {
        _cancel = new CancellationTokenSource();
        CancellationToken token = _cancel.Token;

        Task.Run(() =>
        {
            foreach (UsbWatchBatch batch in UsbWatcher.WatchWithFaults(TimeSpan.FromSeconds(2), token))
            {
                foreach (UsbChange change in batch.Changes)
                    Dispatcher.Invoke(() => Notify(change));

                // A device that fails enumeration never arrives, so the hub's fault status is the
                // only sign of it. The watcher yields each fault once per occurrence, from the same
                // hub poll: the port controller is not asked.
                foreach (UsbPortStatusReport fault in batch.Faults)
                    Dispatcher.Invoke(() => NotifyFault(fault));

                // A USB device arriving says nothing about the port controller, so this refresh
                // deliberately does not force a UCSI read. Polling the controller on every device
                // event is what wedged it.
                Dispatcher.Invoke(() =>
                {
                    if (_popover?.IsVisible == true) _popover.Refresh();
                });
            }
        }, token);
    }

    /// <summary>
    /// Fires when the machine moves between mains and battery, which is what plugging a USB-C
    /// charger in looks like from Windows. The port state settles a moment after the event, so
    /// give the controller time before asking it anything.
    /// </summary>
    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.StatusChange) return;

        Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ =>
            Dispatcher.Invoke(() =>
            {
                if (_popover?.IsVisible == true) _popover.Refresh();
                NotifyPowerChange();
            }));
    }

    /// <summary>Says what the charger can do, which is the question being asked at that moment.</summary>
    private void NotifyPowerChange()
    {
        if (_tray is null) return;

        try
        {
            PortmarkReport report = Portmark.Core.PortmarkReader.Read(requestIdentity: false);
            ConnectorReport? charging = report.Connectors
                .FirstOrDefault(c => c.Connected == true && c.PowerDirection == "consuming");

            if (charging?.Power is { DataAvailable: true, MaxAvailableMilliwatts: int mw } p && mw > 0)
            {
                // The contract is what was negotiated, not power measured flowing, so it is named
                // as a contract.
                string now = p.Negotiated?.NegotiatedPowerMilliwatts is int contract
                    ? $"{contract / 1000.0:0.#}W contract, supply offers up to {mw / 1000.0:0.#}W"
                    : $"Supply offers up to {mw / 1000.0:0.#}W";
                _tray.ShowBalloonTip(5000, "Charger connected", now, Forms.ToolTipIcon.None);
            }
        }
        catch (Exception)
        {
            // A power transition is a poor moment to interrogate the controller. Staying quiet is
            // better than a balloon saying something went wrong with a cable the user just plugged
            // in successfully.
        }
    }

    private void Notify(UsbChange change)
    {
        if (_tray is null) return;

        UsbDeviceReport d = change.Device;
        string name = d.Product ?? d.Manufacturer ?? $"Unidentified {d.DeviceClass} device";

        if (change.Kind == UsbChangeKind.Detached)
        {
            _tray.ShowBalloonTip(3000, "Disconnected", name, Forms.ToolTipIcon.None);
            return;
        }

        if (d.IsUnderperforming)
        {
            _tray.ShowBalloonTip(
                8000,
                $"{name} is running slower than it could",
                $"Connected at {d.Speed}. {d.LinkDiagnosis}",
                Forms.ToolTipIcon.Warning);
            return;
        }

        string detail = d.MaxPowerMilliamps is int ma
            ? $"{d.Speed}, requests up to {ma} mA"
            : d.Speed;

        _tray.ShowBalloonTip(4000, $"Connected: {name}", detail, Forms.ToolTipIcon.None);
    }

    /// <summary>
    /// Says which port the hub reports a failed connection on, in the hub's words. The balloon
    /// names no cause, because nothing read here knows one.
    /// </summary>
    private void NotifyFault(UsbPortStatusReport fault)
    {
        if (_tray is null) return;

        // The IDs are there only when the hub returned a valid descriptor with the status.
        string id = fault.VendorId is null
            ? ""
            : $" ({PopoverWindow.VendorLabel(fault.VendorId, fault.VendorName)}:{fault.ProductId})";

        _tray.ShowBalloonTip(
            8000,
            "USB hub reported a port fault",
            $"{UsbTopology.HubLabel(fault.HubPath)}, port {fault.Port}{id}: {fault.Description}.",
            Forms.ToolTipIcon.Warning);
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static bool IsAutostart()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue("Portmark") is string;
    }

    private static void SetAutostart(bool enabled)
    {
        using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled && Environment.ProcessPath is { } path) key.SetValue("Portmark", '"' + path + '"');
        else key.DeleteValue("Portmark", throwOnMissingValue: false);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _cancel?.Cancel();

        if (_tray is not null)
        {
            // Without this the icon is left behind in the notification area until the user hovers
            // over it, which looks like a crash.
            _tray.Visible = false;
            _tray.Dispose();
        }

        base.OnExit(e);
    }
}
