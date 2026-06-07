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
        toggle.IsCheckedChanged += (_, _) => SetEnabled(e.Id, toggle.IsChecked == true);

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

        var badge = KindBadge(e.Kind);

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            Margin = new Thickness(10, 8, 10, 8),
        };
        Grid.SetColumn(toggle, 0);
        Grid.SetColumn(textCol, 1);
        Grid.SetColumn(badge, 2);
        toggle.Margin = new Thickness(0, 0, 12, 0);
        textCol.Margin = new Thickness(0, 0, 10, 0);
        grid.Children.Add(toggle);
        grid.Children.Add(textCol);
        grid.Children.Add(badge);

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

    private void SetEnabled(string id, bool enabled)
    {
        bool currentlyDisabled = _cfg.DisabledCommandIds.Contains(id, StringComparer.OrdinalIgnoreCase);
        if (enabled && currentlyDisabled)
            _cfg.DisabledCommandIds.RemoveAll(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
        else if (!enabled && !currentlyDisabled)
            _cfg.DisabledCommandIds.Add(id);
        else
            return; // no change

        _cfg.Save();
        _onChanged();   // rebuild the live registry so the change applies immediately
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
    private async Task<bool> ConfirmAsync(string message)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return false;

        var tcs = new TaskCompletionSource<bool>();
        var dlg = new FrostedWindow("Delete command?")
        {
            Width = 360,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var ok = new Button { Content = "Delete", MinWidth = 88, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Classes.Add("danger");
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
