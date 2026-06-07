using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MaplePet.Engine;

namespace MaplePet.Views;

/// <summary>
/// The Commands tab of <see cref="ConfigWindow"/> — the management surface for the user command library.
/// Lists every installed command (built-ins are not shown; they're always on), each with an on/off switch,
/// a kind badge, its help text, and an error chip when its manifest is invalid. Turning one off persists
/// to <see cref="Settings.DisabledCommandIds"/> and rebuilds the live registry at once (via
/// <c>onChanged</c>); Delete removes its folder. A header link reveals the commands folder so a user can
/// drop a new <c>cmd_*</c> folder in — which hot-reloads without a restart.
/// </summary>
public sealed class CommandsView : UserControl
{
    private readonly CommandStore _store;
    private readonly Settings _cfg;
    private readonly Action _onChanged;   // rebuild the live registry after a toggle/delete

    private readonly StackPanel _list;
    private readonly TextBlock _status;

    /// <param name="onChanged">Invoked after the library is mutated (toggle/delete) so the host can rebuild
    /// the live command registry immediately.</param>
    public CommandsView(CommandStore store, Settings cfg, Action onChanged)
    {
        _store = store;
        _cfg = cfg;
        _onChanged = onChanged;

        _list = new StackPanel { Spacing = 6, Margin = new Thickness(14, 8, 14, 12) };
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _list,
        };

        var hint = new TextBlock
        {
            Text = "Drop a command folder into the commands directory — it appears here instantly.",
            VerticalAlignment = VerticalAlignment.Center,
        };
        hint.Classes.Add("caption");

        var folderLink = new Button { Content = "Open folder ↗", VerticalAlignment = VerticalAlignment.Center };
        folderLink.Classes.Add("link");
        ToolTip.SetTip(folderLink, _store.Root);
        folderLink.Click += (_, _) => _ = RevealFolderAsync();

        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(hint, 0);
        Grid.SetColumn(folderLink, 1);
        headerGrid.Children.Add(hint);
        headerGrid.Children.Add(folderLink);
        var header = new Border { Padding = new Thickness(18, 6, 12, 8), Child = headerGrid };

        _status = new TextBlock
        {
            Foreground = FrostTheme.TextSecondary,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
        };
        var footer = new Border
        {
            BorderBrush = FrostTheme.Hairline,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(18, 9),
            MinHeight = 38,
            Child = _status,
        };

        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        dock.Children.Add(header);
        dock.Children.Add(footer);
        dock.Children.Add(scroll);
        Content = dock;

        Rebuild();
    }

    /// <summary>Recreate the rows from the current store + enabled set.</summary>
    private void Rebuild()
    {
        _list.Children.Clear();
        var entries = _store.List();
        if (entries.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = "No commands installed yet. Built-in commands (/rank, /fortune, /clear, /help) are always available.",
                Foreground = FrostTheme.TextSecondary,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(6, 10, 6, 0),
            });
            return;
        }
        foreach (var e in entries)
            _list.Children.Add(BuildRow(e));
    }

    private Control BuildRow(CommandEntry e)
    {
        bool enabled = !_cfg.DisabledCommandIds.Contains(e.Id, StringComparer.OrdinalIgnoreCase);

        var toggle = new ToggleSwitch
        {
            IsChecked = enabled,
            OnContent = "",
            OffContent = "",
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = e.Valid, // an invalid command can't be enabled
        };
        toggle.IsCheckedChanged += (_, _) => _ = SetEnabledAsync(e.Id, toggle.IsChecked == true);

        var title = new TextBlock
        {
            Text = "/" + e.Name,
            FontWeight = FontWeight.SemiBold,
            FontSize = 13,
            Foreground = FrostTheme.TextPrimary,
        };
        var help = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(e.Error) ? (string.IsNullOrWhiteSpace(e.Help) ? "(no description)" : e.Help) : "⚠ " + e.Error,
            FontSize = 11,
            Foreground = string.IsNullOrWhiteSpace(e.Error) ? FrostTheme.TextSecondary : FrostTheme.StatusError,
            TextWrapping = TextWrapping.Wrap,
        };
        var textCol = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        textCol.Children.Add(title);
        textCol.Children.Add(help);

        // Right side: an optional network status/affordance (for kind:script commands that declare hosts:)
        // followed by the kind badge.
        var right = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var net = NetworkControl(e);
        if (net is not null) right.Children.Add(net);
        right.Children.Add(KindBadge(e.Kind));

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(10, 8, 10, 8),
        };
        Grid.SetColumn(toggle, 0);
        Grid.SetColumn(textCol, 1);
        Grid.SetColumn(right, 2);
        toggle.Margin = new Thickness(0, 0, 12, 0);
        textCol.Margin = new Thickness(0, 0, 10, 0);
        grid.Children.Add(toggle);
        grid.Children.Add(textCol);
        grid.Children.Add(right);

        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(10),
            Child = grid,
        };

        var delete = new MenuItem { Header = "Delete" };
        delete.Click += (_, _) => _ = DeleteAsync(e);
        card.ContextMenu = new ContextMenu { Items = { delete } };

        return card;
    }

    private static Control KindBadge(string kind)
    {
        var label = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(kind) ? "?" : kind,
            FontSize = 9,
            FontWeight = FontWeight.Bold,
            Foreground = FrostTheme.BadgeText,
        };
        return new Border
        {
            Background = FrostTheme.AccentBrush,
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(7, 2),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = label,
        };
    }

    /// <summary>For a <c>kind:script</c> command that declares a <c>hosts:</c> allowlist: a "🌐 ✓" badge when
    /// its network access is approved, otherwise an "Allow network" button that opens the approval prompt.
    /// Null for every other command (no network capability). Bundled net commands ship enabled but
    /// <i>unapproved</i>, so this button is how the user grants the capability without toggling the command
    /// off and on.</summary>
    private Control? NetworkControl(CommandEntry e)
    {
        if (!string.Equals(e.Kind, "script", StringComparison.OrdinalIgnoreCase)) return null;
        var m = _store.ReadManifest(e.Id);
        if (m is null || m.Hosts.Count == 0) return null;

        if (_cfg.IsNetworkApproved(e.Id, CommandManifest.HostsSignature(m.Hosts)))
        {
            var ok = new TextBlock
            {
                Text = "🌐 ✓",
                FontSize = 11,
                Foreground = FrostTheme.TextSecondary,
                VerticalAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(ok, "Network approved: " + string.Join(", ", m.Hosts));
            return ok;
        }

        var btn = new Button { Content = "Allow network", FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        btn.Classes.Add("link");
        ToolTip.SetTip(btn, "This command wants to reach: " + string.Join(", ", m.Hosts));
        btn.Click += (_, _) => _ = PromptNetworkAsync(e.Id, m);
        return btn;
    }

    private async Task SetEnabledAsync(string id, bool enabled)
    {
        bool changed = false;
        bool currentlyDisabled = _cfg.DisabledCommandIds.Contains(id, StringComparer.OrdinalIgnoreCase);
        if (enabled && currentlyDisabled)
        {
            _cfg.DisabledCommandIds.RemoveAll(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
            changed = true;
        }
        else if (!enabled && !currentlyDisabled)
        {
            _cfg.DisabledCommandIds.Add(id);
            changed = true;
        }

        // On enable, offer the network approval prompt for a command that declares hosts: and isn't granted.
        if (enabled)
        {
            var m = _store.ReadManifest(id);
            if (m is not null && m.Kind == CommandKind.Script && m.Hosts.Count > 0
                && !_cfg.IsNetworkApproved(id, CommandManifest.HostsSignature(m.Hosts))
                && await ConfirmNetworkAsync(m.Name, m.Hosts))
            {
                GrantNetwork(id, m);
                changed = true;
            }
        }

        if (!changed) return;
        _cfg.Save();
        Rebuild();      // refresh the row's network affordance
        _onChanged();   // rebuild the live registry so the change applies immediately
    }

    private async Task PromptNetworkAsync(string id, CommandManifest m)
    {
        if (!await ConfirmNetworkAsync(m.Name, m.Hosts)) return;
        GrantNetwork(id, m);
        _cfg.Save();
        Rebuild();      // flip the button → "🌐 ✓"
        _onChanged();   // rebuild so httpGet becomes available to the running command
    }

    private void GrantNetwork(string id, CommandManifest m)
    {
        string sig = CommandManifest.HostsSignature(m.Hosts);
        _cfg.NetworkApprovedCommands.RemoveAll(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase));
        _cfg.NetworkApprovedCommands.Add(new NetworkGrant { Id = id, Hosts = sig });
    }

    private async Task DeleteAsync(CommandEntry e)
    {
        if (!await ConfirmAsync($"Delete the \"/{e.Name}\" command? This removes its files and can't be undone."))
            return;
        _store.Delete(e.Id);
        _cfg.DisabledCommandIds.RemoveAll(x => string.Equals(x, e.Id, StringComparison.OrdinalIgnoreCase));
        _cfg.Save();
        Rebuild();
        _onChanged();
        Status($"Deleted \"/{e.Name}\".");
    }

    private async Task RevealFolderAsync()
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top is null) return;
            var folder = await top.StorageProvider.TryGetFolderFromPathAsync(_store.Root);
            if (folder is not null) await top.Launcher.LaunchUriAsync(folder.Path);
            else await top.Launcher.LaunchUriAsync(new Uri(_store.Root));
        }
        catch (Exception ex)
        {
            Status("Couldn't open the folder: " + ex.Message, error: true);
        }
    }

    // ------------------------------------------------------------------ small modal confirm
    /// <summary>The delete confirmation (a danger-styled "Delete" button).</summary>
    private Task<bool> ConfirmAsync(string message) =>
        ConfirmAsync("Delete command?", message, "Delete", danger: true);

    /// <summary>The network-access prompt: lists the hosts the command wants to reach and asks to allow.</summary>
    private Task<bool> ConfirmNetworkAsync(string name, IReadOnlyList<string> hosts)
    {
        string list = string.Join("\n", hosts.Select(h => "  •  " + h));
        string msg = $"The \"/{name}\" command makes network requests to:\n\n{list}\n\n" +
                     "Allow it to reach these hosts? Only these are reachable, over HTTPS.";
        return ConfirmAsync("Allow network access?", msg, "Allow", danger: false);
    }

    private async Task<bool> ConfirmAsync(string title, string message, string okText, bool danger)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return false;

        var tcs = new TaskCompletionSource<bool>();
        var dlg = new FrostedWindow(title)
        {
            Width = 360,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var ok = new Button { Content = okText, MinWidth = 88, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        if (danger) ok.Classes.Add("danger");
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel.Classes.Add("ghost");
        ok.Click += (_, _) => { tcs.TrySetResult(true); dlg.Close(); };
        cancel.Click += (_, _) => { tcs.TrySetResult(false); dlg.Close(); };
        dlg.Closed += (_, _) => tcs.TrySetResult(false);

        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        text.Classes.Add("rowLabel");
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, ok },
        };
        var body = new StackPanel { Margin = new Thickness(20, 6, 20, 18), Spacing = 18, Children = { text, buttons } };
        dlg.SetDialogBody(body);

        await dlg.ShowDialog(owner);
        return await tcs.Task;
    }

    private void Status(string message, bool error = false)
    {
        _status.Text = message;
        _status.Foreground = error ? FrostTheme.StatusError : FrostTheme.TextSecondary;
    }
}
