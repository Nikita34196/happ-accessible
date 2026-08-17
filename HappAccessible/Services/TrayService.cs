using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using Application = System.Windows.Application;

namespace HappAccessible.Services;

public sealed record TrayServerEntry(
    string Uri,
    string Name,
    string Protocol,
    bool IsWhitelistBypass,
    int? LatencyMs);

public sealed class TrayMenuSnapshot
{
    public bool IsConnected { get; init; }
    public string? ConnectedUri { get; init; }
    public string? SelectedUri { get; init; }
    public IReadOnlyList<TrayServerEntry> Servers { get; init; } = [];
}

/// <summary>
/// System tray icon with a Vireo/Clash-style menu: show, connect/disconnect,
/// nested server list (pick &amp; connect), refresh subscription, exit.
/// Menu is rebuilt on each open so checks stay current.
/// </summary>
public sealed class TrayService : IDisposable
{
    private const int MaxServersInMenu = 80;
    private readonly NotifyIcon _icon;
    private readonly Window _window;
    private readonly ContextMenuStrip _menu;
    private readonly object _gate = new();

    public Func<TrayMenuSnapshot>? SnapshotProvider { get; set; }

    public event Action? ConnectRequested;
    public event Action? DisconnectRequested;
    public event Action? ShowRequested;
    public event Action? ExitRequested;
    public event Action? RefreshSubscriptionRequested;
    public event Action<string>? ServerConnectRequested;

    public TrayService(Window window)
    {
        _window = window;
        _icon = new NotifyIcon
        {
            Text = "Happ Accessible",
            Visible = true,
            Icon = SystemIcons.Application
        };

        _menu = new ContextMenuStrip();
        _menu.Opening += (_, _) => RebuildMenu();
        _icon.ContextMenuStrip = _menu;

        _icon.DoubleClick += (_, _) => ShowRequested?.Invoke();
        _icon.BalloonTipTitle = "Happ Accessible";
    }

    public void SetTooltip(string text)
    {
        // NotifyIcon.Text max ~63 chars
        _icon.Text = text.Length <= 63 ? text : text[..60] + "…";
    }

    public void Notify(string message)
    {
        _icon.BalloonTipText = message;
        _icon.ShowBalloonTip(2500);
    }

    public void HideWindowToTray()
    {
        _window.Hide();
        Notify("Свёрнуто в трей. Откройте через меню значка.");
    }

    public void ShowWindow()
    {
        _window.Show();
        if (_window.WindowState == WindowState.Minimized)
            _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Focus();
    }

    private void RebuildMenu()
    {
        TrayMenuSnapshot snap;
        try
        {
            snap = SnapshotProvider?.Invoke() ?? new TrayMenuSnapshot();
        }
        catch
        {
            snap = new TrayMenuSnapshot();
        }

        lock (_gate)
        {
            _menu.Items.Clear();

            AddItem(_menu.Items, "Показать окно", () => ShowRequested?.Invoke());
            _menu.Items.Add(new ToolStripSeparator());

            var connect = AddItem(_menu.Items, "Подключить выбранный", () => ConnectRequested?.Invoke());
            connect.Enabled = snap.Servers.Count > 0 && !snap.IsConnected;

            var disconnect = AddItem(_menu.Items, "Отключить", () => DisconnectRequested?.Invoke());
            disconnect.Enabled = snap.IsConnected;

            _menu.Items.Add(new ToolStripSeparator());

            var serversRoot = new ToolStripMenuItem(EscapeAmpersand(
                snap.Servers.Count == 0
                    ? "Серверы (список пуст)"
                    : $"Серверы ({snap.Servers.Count})"))
            {
                Enabled = snap.Servers.Count > 0
            };
            _menu.Items.Add(serversRoot);

            if (snap.Servers.Count > 0)
                PopulateServers(serversRoot, snap);

            AddItem(_menu.Items, "Обновить подписку", () => RefreshSubscriptionRequested?.Invoke());
            _menu.Items.Add(new ToolStripSeparator());
            AddItem(_menu.Items, "Выход", () => ExitRequested?.Invoke());
        }
    }

    private void PopulateServers(ToolStripMenuItem root, TrayMenuSnapshot snap)
    {
        var list = snap.Servers;
        var truncated = false;
        if (list.Count > MaxServersInMenu)
        {
            list = list.Take(MaxServersInMenu).ToList();
            truncated = true;
        }

        // Group by protocol when there are many nodes (like multi-core trays).
        var useGroups = list.Count > 12;
        if (useGroups)
        {
            foreach (var group in list.GroupBy(s => FormatProtocol(s.Protocol)).OrderBy(g => g.Key))
            {
                var groupItem = new ToolStripMenuItem(EscapeAmpersand($"{group.Key} ({group.Count()})"));
                foreach (var s in group)
                    groupItem.DropDownItems.Add(CreateServerItem(s, snap));
                root.DropDownItems.Add(groupItem);
            }
        }
        else
        {
            foreach (var s in list)
                root.DropDownItems.Add(CreateServerItem(s, snap));
        }

        if (truncated)
        {
            root.DropDownItems.Add(new ToolStripSeparator());
            root.DropDownItems.Add(new ToolStripMenuItem(
                EscapeAmpersand($"Показаны первые {MaxServersInMenu}. Остальные — в окне приложения."))
            {
                Enabled = false
            });
        }
    }

    private ToolStripMenuItem CreateServerItem(TrayServerEntry s, TrayMenuSnapshot snap)
    {
        var label = BuildServerLabel(s);
        var item = new ToolStripMenuItem(EscapeAmpersand(label))
        {
            Tag = s.Uri,
            Checked = string.Equals(s.Uri, snap.ConnectedUri, StringComparison.Ordinal)
                      || (!snap.IsConnected
                          && string.Equals(s.Uri, snap.SelectedUri, StringComparison.Ordinal)),
            CheckOnClick = false
        };
        var uri = s.Uri;
        item.Click += (_, _) => ServerConnectRequested?.Invoke(uri);
        return item;
    }

    private static string BuildServerLabel(TrayServerEntry s)
    {
        var ping = s.LatencyMs is null ? ""
            : s.LatencyMs < 0 ? " …"
            : $" · {s.LatencyMs} мс";
        var wl = s.IsWhitelistBypass ? " ★" : "";
        var proto = FormatProtocol(s.Protocol);
        var name = s.Name;
        if (name.Length > 48)
            name = name[..45] + "…";
        return $"{name}{wl} ({proto}{ping})";
    }

    private static string FormatProtocol(string protocol) =>
        string.IsNullOrWhiteSpace(protocol) ? "?" : protocol.Trim().ToLowerInvariant();

    private static string EscapeAmpersand(string text) =>
        (text ?? "").Replace("&", "&&", StringComparison.Ordinal);

    private static ToolStripMenuItem AddItem(ToolStripItemCollection items, string text, Action action)
    {
        var item = new ToolStripMenuItem(EscapeAmpersand(text));
        item.Click += (_, _) => action();
        items.Add(item);
        return item;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _menu.Dispose();
        _icon.Dispose();
    }
}
