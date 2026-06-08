using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using MaplePet.Api.Hub;
using MaplePet.Engine;

namespace MaplePet.Views;

/// <summary>
/// The Browse tab of <see cref="ConfigWindow"/> — discover + one-click install community commands from the
/// hub. Fetches the registry through <see cref="HubClient"/> (cached, anonymous), shows a searchable/filtered
/// list, and per entry offers Install / Update / Installed / "needs newer app". Installing an untrusted
/// command goes through a pre-install disclosure dialog; a freshly installed <c>kind:script</c> lands disabled
/// (handled by the client). Updating a locally-edited command prompts before overwriting.
/// </summary>
public sealed class HubView : UserControl
{
    private enum Tier { All, Official, Community }

    private readonly HubClient _hub;
    private readonly CommandStore _store;
    private readonly Settings _cfg;
    private readonly Action _onChanged;

    private readonly StackPanel _list;
    private readonly TextBlock _status;
    private readonly TextBox _search;
    private readonly Button _tabAll, _tabOfficial, _tabCommunity;

    private List<HubEntry> _entries = new();
    private Dictionary<string, (string InstalledId, HubProvenance Prov)> _installed = new(StringComparer.OrdinalIgnoreCase);
    private Tier _tier = Tier.All;
    private bool _busy;

    public HubView(HubClient hub, CommandStore store, Settings cfg, Action onChanged)
    {
        _hub = hub;
        _store = store;
        _cfg = cfg;
        _onChanged = onChanged;

        _search = new TextBox
        {
            Watermark = "Search commands…",
            Width = 220,
            VerticalAlignment = VerticalAlignment.Center,
        };
        // TextChanged (not GetObservable) so it does NOT fire during construction — RenderRows touches
        // _list, which isn't built yet at this point.
        _search.TextChanged += (_, _) => RenderRows();

        _tabAll = FilterButton("All", Tier.All);
        _tabOfficial = FilterButton("Official", Tier.Official);
        _tabCommunity = FilterButton("Community", Tier.Community);
        var filters = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center,
            Children = { _tabAll, _tabOfficial, _tabCommunity },
        };

        var refresh = new Button { Content = "Refresh ↻", VerticalAlignment = VerticalAlignment.Center };
        refresh.Classes.Add("link");
        refresh.Click += (_, _) => _ = LoadAsync(force: true);

        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(8, 0, 8, 0) };
        Grid.SetColumn(_search, 0);
        Grid.SetColumn(filters, 1);
        Grid.SetColumn(refresh, 2);
        filters.Margin = new Thickness(10, 0, 0, 0);
        filters.HorizontalAlignment = HorizontalAlignment.Left;
        headerGrid.Children.Add(_search);
        headerGrid.Children.Add(filters);
        headerGrid.Children.Add(refresh);
        var header = new Border { Padding = new Thickness(10, 8, 10, 8), Child = headerGrid };

        _list = new StackPanel { Spacing = 6, Margin = new Thickness(14, 4, 14, 12) };
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _list,
        };

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

        SetActiveTier();
        // The index is fetched lazily the first time the Browse tab is actually shown (below), not here —
        // so opening the Characters/Settings tab doesn't kick off a hub network request.
    }

    private bool _loadedOnce;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_loadedOnce) return;
        _loadedOnce = true;
        _ = LoadAsync(force: false);
    }

    /// <summary>Re-read which hub commands are installed (called by the host after an external change too).</summary>
    public void RefreshInstalled()
    {
        _installed = _store.List()
            .Where(e => e.Hub is not null && !string.IsNullOrEmpty(e.Hub!.HubId))
            .GroupBy(e => e.Hub!.HubId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => (g.First().Id, g.First().Hub!), StringComparer.OrdinalIgnoreCase);
        RenderRows();
    }

    private async Task LoadAsync(bool force)
    {
        Status("Loading the hub…");
        try
        {
            var (index, err) = await _hub.GetIndexAsync(force, CancellationToken.None);
            _entries = index.Commands ?? new();
            RefreshInstalled();   // also renders
            if (err is not null) Status("Couldn't reach the hub — showing the last cached list. " + err, error: true);
            else if (_entries.Count == 0) Status("The hub has no commands yet.");
            else Status($"{_entries.Count} command(s) available.");
        }
        catch (Exception ex)
        {
            Status("Failed to load the hub: " + ex.Message, error: true);
        }
    }

    private void RenderRows()
    {
        _list.Children.Clear();
        string q = (_search.Text ?? "").Trim();
        var shown = _entries.Where(e => MatchesTier(e) && MatchesSearch(e, q)).ToList();
        if (shown.Count == 0)
        {
            _list.Children.Add(new TextBlock
            {
                Text = _entries.Count == 0 ? "Nothing here yet." : "No commands match your search.",
                Foreground = FrostTheme.TextSecondary,
                Margin = new Thickness(6, 10, 6, 0),
                TextWrapping = TextWrapping.Wrap,
            });
            return;
        }
        foreach (var e in shown.OrderByDescending(e => e.Official).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase))
            _list.Children.Add(BuildRow(e));
    }

    private bool MatchesTier(HubEntry e) => _tier switch
    {
        Tier.Official => e.Official,
        Tier.Community => !e.Official,
        _ => true,
    };

    private static bool MatchesSearch(HubEntry e, string q)
    {
        if (q.Length == 0) return true;
        bool C(string? s) => s is not null && s.Contains(q, StringComparison.OrdinalIgnoreCase);
        return C(e.Name) || C(e.Title) || C(e.Author) || C(e.Description) || e.Tags.Any(t => C(t));
    }

    private Control BuildRow(HubEntry e)
    {
        var title = new TextBlock { Text = "/" + e.Name, FontWeight = FontWeight.SemiBold, FontSize = 13, Foreground = FrostTheme.TextPrimary };
        var meta = new TextBlock
        {
            Text = $"v{e.Version}" + (string.IsNullOrWhiteSpace(e.Author) ? "" : $"  ·  by {e.Author}"),
            FontSize = 10, Foreground = FrostTheme.TextSecondary,
        };
        var desc = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(e.Description) ? (string.IsNullOrWhiteSpace(e.Title) ? "(no description)" : e.Title) : e.Description,
            FontSize = 11, Foreground = FrostTheme.TextSecondary, TextWrapping = TextWrapping.Wrap,
        };
        var textCol = new StackPanel { Spacing = 1, VerticalAlignment = VerticalAlignment.Center };
        textCol.Children.Add(title);
        textCol.Children.Add(meta);
        textCol.Children.Add(desc);
        string? hint = ContextHint(e);
        if (hint is not null)
            textCol.Children.Add(new TextBlock { Text = hint, FontSize = 10, Foreground = FrostTheme.TextSecondary, TextWrapping = TextWrapping.Wrap });

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        if (e.Official) right.Children.Add(Pill("official", FrostTheme.AccentBrush, FrostTheme.BadgeText));
        right.Children.Add(Pill(string.IsNullOrWhiteSpace(e.Kind) ? "?" : e.Kind, new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)), FrostTheme.TextPrimary));
        right.Children.Add(ActionButton(e));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(10, 8, 10, 8) };
        Grid.SetColumn(textCol, 0);
        Grid.SetColumn(right, 1);
        textCol.Margin = new Thickness(0, 0, 10, 0);
        grid.Children.Add(textCol);
        grid.Children.Add(right);

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0xFF, 0xFF)),
            CornerRadius = new CornerRadius(10),
            Child = grid,
        };
    }

    private Control ActionButton(HubEntry e)
    {
        // Compatibility gate first.
        if (!SemVer.AtLeast(AppInfo.Version, e.MinAppVersion))
        {
            var b = new Button { Content = $"Needs app ≥ {e.MinAppVersion}", FontSize = 11, IsEnabled = false, VerticalAlignment = VerticalAlignment.Center };
            b.Classes.Add("ghost");
            return b;
        }

        if (_installed.TryGetValue(e.Id, out var inst))
        {
            var remove = new Button { Content = "Remove", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            remove.Classes.Add("danger");
            remove.IsEnabled = !_busy;
            remove.Click += (_, _) => _ = RemoveAsync(e, inst.InstalledId);

            // Up to date → just Remove. Update available → Update + Remove side by side.
            if (SemVer.Compare(inst.Prov.Version, e.Version) >= 0)
                return remove;

            var upd = new Button { Content = $"Update → {e.Version}", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            upd.Classes.Add("accent");
            upd.IsEnabled = !_busy;
            upd.Click += (_, _) => _ = UpdateAsync(e, inst.InstalledId);
            return new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center,
                Children = { upd, remove },
            };
        }

        var install = new Button { Content = "Install", FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        install.Classes.Add("accent");
        install.IsEnabled = !_busy;
        install.Click += (_, _) => _ = InstallAsync(e);
        return install;
    }

    /// <summary>A non-blocking note when the user's environment won't actually run this kind yet.</summary>
    private string? ContextHint(HubEntry e)
    {
        if (string.Equals(e.Kind, "script", StringComparison.OrdinalIgnoreCase) && !_cfg.EnableUserScripts)
            return "Enable user scripts in Settings to run this.";
        if (string.Equals(e.Kind, "prompt", StringComparison.OrdinalIgnoreCase) && !_cfg.EnableChatbot)
            return "Needs the chatbot enabled + configured.";
        if (e.Hosts.Count > 0)
            return "Requests network access: " + string.Join(", ", e.Hosts);
        return null;
    }

    private async Task InstallAsync(HubEntry e)
    {
        if (_busy) return;
        if (!await ConfirmInstallAsync(e)) return;
        _busy = true; RenderRows();
        Status($"Downloading /{e.Name}…");
        var r = await _hub.InstallAsync(e, _store, _cfg, CancellationToken.None);
        _busy = false;
        // Back on the UI thread (no ConfigureAwait above) — safe to rebuild the registry + refresh views.
        if (r.Ok) _onChanged();
        RefreshInstalled();
        Status(r.Message, error: !r.Ok);
    }

    private async Task UpdateAsync(HubEntry e, string installedId)
    {
        if (_busy) return;
        UpdateMode mode = UpdateMode.Overwrite;
        if (HubClient.IsInstalledDirty(_store, installedId))
        {
            var choice = await DirtyChoiceAsync(e.Name);
            if (choice == DirtyChoice.Skip) return;
            mode = choice == DirtyChoice.Copy ? UpdateMode.Copy : UpdateMode.Overwrite;
        }
        else if (!await ConfirmInstallAsync(e, update: true))
        {
            return;
        }

        _busy = true; RenderRows();
        Status($"Updating /{e.Name}…");
        var r = await _hub.UpdateAsync(e, installedId, _store, _cfg, mode, CancellationToken.None);
        _busy = false;
        // Back on the UI thread (no ConfigureAwait above) — safe to rebuild the registry + refresh views.
        if (r.Ok) _onChanged();
        RefreshInstalled();
        Status(r.Message, error: !r.Ok);
    }

    /// <summary>Uninstall a hub command: delete its folder and drop its settings (disable flag + network
    /// grant), then rebuild the registry and refresh the views. Runs entirely on the UI thread.</summary>
    private async Task RemoveAsync(HubEntry e, string installedId)
    {
        if (_busy) return;
        if (!await ConfirmAsync($"Remove /{e.Name}?",
                $"This uninstalls \"/{e.Name}\" and deletes its files. You can reinstall it from the hub anytime.",
                "Remove", danger: true))
            return;

        _busy = true; RenderRows();
        Status($"Removing /{e.Name}…");
        _store.Delete(installedId);
        _cfg.DisabledCommandIds.RemoveAll(x => string.Equals(x, installedId, StringComparison.OrdinalIgnoreCase));
        _cfg.NetworkApprovedCommands.RemoveAll(g => g is not null && string.Equals(g.Id, installedId, StringComparison.OrdinalIgnoreCase));
        _cfg.Save();
        _busy = false;
        _onChanged();          // rebuild the live registry + refresh the Commands tab
        RefreshInstalled();    // recompute this view's installed state + re-render (button → Install)
        Status($"Removed /{e.Name}.");
    }

    // ------------------------------------------------------------------ dialogs
    private Task<bool> ConfirmInstallAsync(HubEntry e, bool update = false)
    {
        bool script = string.Equals(e.Kind, "script", StringComparison.OrdinalIgnoreCase);
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{(string.IsNullOrWhiteSpace(e.Author) ? "Unknown author" : "by " + e.Author)}  ·  {(e.Official ? "official" : "community")}  ·  v{e.Version}");
        sb.AppendLine();
        sb.AppendLine(KindDescription(e.Kind));
        if (e.Hosts.Count > 0)
            sb.AppendLine("Network access (HTTPS only): " + string.Join(", ", e.Hosts));
        if (script)
        {
            sb.AppendLine();
            sb.AppendLine("This is a community script. It runs in a sandbox and will be installed DISABLED — review it in the Commands tab before enabling.");
        }
        string title = update ? $"Update /{e.Name}?" : $"Install /{e.Name}?";
        return ConfirmAsync(title, sb.ToString().TrimEnd(), update ? "Update" : "Install", danger: script);
    }

    private static string KindDescription(string kind) => kind.ToLowerInvariant() switch
    {
        "script" => "Runs sandboxed JavaScript on your computer.",
        "prompt" => "Sends instructions to your AI provider using your own API key.",
        "link" => "Can open a web link when you run it.",
        "clipboard" => "Can copy text to your clipboard.",
        "image" => "Shows an image.",
        "pet" => "Animates the pet.",
        "text" => "Makes the pet speak some text.",
        _ => "A pet command.",
    };

    private enum DirtyChoice { Overwrite, Copy, Skip }

    private async Task<DirtyChoice> DirtyChoiceAsync(string name)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return DirtyChoice.Skip;
        var tcs = new TaskCompletionSource<DirtyChoice>();
        var dlg = new FrostedWindow("Command was edited locally")
        {
            Width = 380, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        var text = new TextBlock
        {
            Text = $"You've edited \"/{name}\" since installing it. Updating will replace it.\n\n" +
                   "• Overwrite — take the new version, discard your edits.\n" +
                   "• Keep a copy — install the new version separately, keep yours.\n" +
                   "• Skip — don't update.",
            TextWrapping = TextWrapping.Wrap,
        };
        text.Classes.Add("rowLabel");
        Button Mk(string content, DirtyChoice c, string cls)
        {
            var b = new Button { Content = content, MinWidth = 96, HorizontalContentAlignment = HorizontalAlignment.Center };
            b.Classes.Add(cls);
            b.Click += (_, _) => { tcs.TrySetResult(c); dlg.Close(); };
            return b;
        }
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right,
            Children = { Mk("Skip", DirtyChoice.Skip, "ghost"), Mk("Keep a copy", DirtyChoice.Copy, "accent"), Mk("Overwrite", DirtyChoice.Overwrite, "danger") },
        };
        dlg.Closed += (_, _) => tcs.TrySetResult(DirtyChoice.Skip);
        dlg.SetDialogBody(new StackPanel { Margin = new Thickness(20, 6, 20, 18), Spacing = 18, Children = { text, buttons } });
        await dlg.ShowDialog(owner);
        return await tcs.Task;
    }

    private async Task<bool> ConfirmAsync(string title, string message, string okText, bool danger)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return false;
        var tcs = new TaskCompletionSource<bool>();
        var dlg = new FrostedWindow(title)
        {
            Width = 400, SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
        };
        var ok = new Button { Content = okText, MinWidth = 88, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Classes.Add(danger ? "danger" : "accent");
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel.Classes.Add("ghost");
        ok.Click += (_, _) => { tcs.TrySetResult(true); dlg.Close(); };
        cancel.Click += (_, _) => { tcs.TrySetResult(false); dlg.Close(); };
        dlg.Closed += (_, _) => tcs.TrySetResult(false);
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        text.Classes.Add("rowLabel");
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, ok },
        };
        dlg.SetDialogBody(new StackPanel { Margin = new Thickness(20, 6, 20, 18), Spacing = 18, Children = { text, buttons } });
        await dlg.ShowDialog(owner);
        return await tcs.Task;
    }

    // ------------------------------------------------------------------ small helpers
    private Button FilterButton(string text, Tier tier)
    {
        var b = new Button { Content = text };
        b.Classes.Add("tab");
        b.Click += (_, _) => { _tier = tier; SetActiveTier(); RenderRows(); };
        return b;
    }

    private void SetActiveTier()
    {
        void S(Button b, bool on) { b.Classes.Remove("tabActive"); if (on) b.Classes.Add("tabActive"); }
        S(_tabAll, _tier == Tier.All);
        S(_tabOfficial, _tier == Tier.Official);
        S(_tabCommunity, _tier == Tier.Community);
    }

    private static Control Pill(string text, IBrush bg, IBrush fg) => new Border
    {
        Background = bg,
        CornerRadius = new CornerRadius(7),
        Padding = new Thickness(7, 2),
        VerticalAlignment = VerticalAlignment.Center,
        Child = new TextBlock { Text = text, FontSize = 9, FontWeight = FontWeight.Bold, Foreground = fg },
    };

    private void Status(string message, bool error = false)
    {
        _status.Text = message;
        _status.Foreground = error ? FrostTheme.StatusError : FrostTheme.TextSecondary;
    }
}
