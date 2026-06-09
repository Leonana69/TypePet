using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using TypePet.Engine;
using TypePet.Rendering;

namespace TypePet.Views;

/// <summary>
/// The Characters tab of <see cref="ConfigWindow"/>. Shows one card per available character — the
/// built-in Default plus every imported one — six per row in a scrolling grid, with a trailing import
/// card to add a new character from a zip. Clicking a card makes the pet wear it (via the
/// <c>onSelect</c> callback); right-clicking offers Rename, Export, and Delete. The currently worn
/// character gets an accent ring and a "WORN" badge.
/// </summary>
public sealed class CharacterView : UserControl
{
    private const int Columns = 6;
    private const double CardW = 96, CardH = 158;
    // The character preview is a portrait well: its width is capped by the card, but its height fills
    // most of the (taller) card. The footage is drawn at 100% (native pixels), centered in the well.
    private const double ThumbW = 84, ThumbH = 110;
    // The smallest gap kept between the feet and the well's bottom edge when a tall figure would
    // otherwise reach past it.
    private const double ThumbFootMargin = 6;

    // Where the user can build/obtain importable character footage.
    private const string MapleSimUrl = "https://maple-sim.net/";

    // A thumbnail only ever draws the idle frame, so decode just that one pose (not the whole footage).
    private static readonly IReadOnlyCollection<string> ThumbnailPoses =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "stand1" };

    private readonly CharacterStore _store;
    private readonly Settings _cfg;
    private readonly Action<string> _onSelect;
    private readonly Action? _onChanged; // store mutated without a re-select (e.g. rename)

    private readonly UniformGrid _grid;
    private readonly TextBlock _status;

    // Render each card's thumbnail once and reuse it across Rebuilds (which happen on every
    // select/rename/import/delete). Keyed by character id; disposed on delete and on window close.
    private readonly Dictionary<string, Bitmap?> _thumbs = new();

    /// <param name="onChanged">Notified when the store is mutated without re-selecting (a rename), so
    /// callers can refresh anything derived from the worn character's display name (e.g. the tray header).</param>
    public CharacterView(CharacterStore store, Settings cfg, Action<string> onSelect, Action? onChanged = null)
    {
        _store = store;
        _cfg = cfg;
        _onSelect = onSelect;
        _onChanged = onChanged;

        _grid = new UniformGrid { Columns = Columns };
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new Border { Padding = new Thickness(10, 4, 10, 10), Child = _grid },
        };

        var hint = new TextBlock
        {
            Text = "Click a character to wear it · right-click to rename, export, or delete",
            VerticalAlignment = VerticalAlignment.Center,
        };
        hint.Classes.Add("caption");

        // Link out to maple-sim.net, where the importable character footage is built/exported.
        var link = new Button { Content = "Build your character ↗", VerticalAlignment = VerticalAlignment.Center };
        link.Classes.Add("link");
        ToolTip.SetTip(link, "Open " + MapleSimUrl + " in your browser");
        link.Click += (_, _) => _ = OpenLinkAsync();

        var headerGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(hint, 0);
        Grid.SetColumn(link, 1);
        headerGrid.Children.Add(hint);
        headerGrid.Children.Add(link);
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

    /// <summary>Release the cached thumbnail bitmaps. Called by the host window when it closes.</summary>
    public void DisposeThumbnails()
    {
        foreach (var bmp in _thumbs.Values) bmp?.Dispose();
        _thumbs.Clear();
    }

    /// <summary>Recreate every card from the current store + selection (after any change).</summary>
    private void Rebuild()
    {
        _grid.Children.Clear();
        foreach (var entry in _store.List())
            _grid.Children.Add(BuildCard(entry));
        _grid.Children.Add(BuildAddCard());
    }

    // ------------------------------------------------------------------ cards
    private Control BuildCard(CharacterEntry entry)
    {
        bool isCurrent = string.Equals(entry.Id, _cfg.CurrentCharacterId, StringComparison.Ordinal);

        var thumb = new Image
        {
            Width = ThumbW,
            Height = ThumbH,
            Stretch = Stretch.Uniform,
            Source = Thumbnail(entry),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapInterpolationMode(thumb, BitmapInterpolationMode.None);

        var thumbWell = new Border
        {
            Width = ThumbW + 14,
            Height = ThumbH + 14,
            CornerRadius = new CornerRadius(10),
            Background = FrostTheme.ThumbWell,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = thumb,
        };

        var nameBlock = new TextBlock
        {
            Text = entry.DisplayName,
            Foreground = FrostTheme.TextPrimary,
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
#if MACOS
            // macOS' system font (San Francisco) seats a single line of text high in its line box — the
            // descent leaves an extra gap below the glyphs — so the name reads as sitting a touch above
            // the centre of its band under the thumbnail. Nudge it down to land where Windows' Segoe UI
            // already does. (Same root cause as the NumericUpDown vertical-centering note in App.axaml.)
            Margin = new Thickness(0, 3, 0, 0),
#endif
        };
        var namePanel = new Panel { Children = { nameBlock } };

        var content = new StackPanel
        {
            Spacing = 3, // sit the name close under the thumbnail
            VerticalAlignment = VerticalAlignment.Center,
            Children = { thumbWell, namePanel },
        };

        // Overlay host so the worn badge can float over the top-right corner.
        var inner = new Grid();
        inner.Children.Add(content);
        if (isCurrent)
            inner.Children.Add(WornBadge());

        var card = new Border
        {
            Width = CardW,
            Height = CardH,
            Margin = new Thickness(4),
            Padding = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = inner,
        };
        card.Classes.Add("charCard");
        if (isCurrent) card.Classes.Add("selected");

        // Left-click anywhere on the card selects it (unless the name handled it for renaming).
        card.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(card).Properties.IsLeftButtonPressed)
                Select(entry);
        };

        if (!entry.IsBuiltIn)
        {
            // The built-in default can't be renamed/exported/deleted, so it gets no context menu.
            // Rename lives here (not on a name-click) so it doesn't fight the card's wear-on-click.
            var rename = new MenuItem { Header = "Rename" };
            rename.Click += (_, _) => BeginRename(entry, namePanel);
            var export = new MenuItem { Header = "Export…" };
            export.Click += (_, _) => _ = ExportAsync(entry);
            var delete = new MenuItem { Header = "Delete" };
            delete.Click += (_, _) => _ = DeleteAsync(entry);
            card.ContextMenu = new ContextMenu { Items = { rename, export, delete } };
        }

        return card;
    }

    private static Control WornBadge()
    {
        var label = new TextBlock
        {
            Text = "WORN",
            FontSize = 9,
            FontWeight = FontWeight.Bold,
            Foreground = FrostTheme.BadgeText,
        };
        return new Border
        {
            Background = FrostTheme.AccentBrush,
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(7, 2),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 4, 0),
            Child = label,
        };
    }

    private Control BuildAddCard()
    {
        var plus = new TextBlock
        {
            Text = "+",
            FontSize = 36,
            Foreground = FrostTheme.AccentBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var label = new TextBlock
        {
            Text = "Import .zip",
            Foreground = FrostTheme.TextSecondary,
            FontSize = 12,
            TextAlignment = TextAlignment.Center,
        };
        var content = new StackPanel
        {
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Panel { Height = ThumbH + 14, Children = { plus } },
                label,
            },
        };

        var card = new Border
        {
            Width = CardW,
            Height = CardH,
            Margin = new Thickness(4),
            Padding = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = content,
        };
        card.Classes.Add("addCard");
        card.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(card).Properties.IsLeftButtonPressed)
                _ = ImportAsync();
        };
        return card;
    }

    // ------------------------------------------------------------------ actions
    private void Select(CharacterEntry entry)
    {
        _onSelect(entry.Id); // applies to the pet + persists CurrentCharacterId (App)
        Rebuild();           // refresh the highlight
    }

    /// <summary>Open maple-sim.net in the user's default browser via the platform launcher.</summary>
    private async Task OpenLinkAsync()
    {
        try
        {
            var top = TopLevel.GetTopLevel(this);
            if (top is not null) await top.Launcher.LaunchUriAsync(new Uri(MapleSimUrl));
        }
        catch (Exception ex)
        {
            Status("Couldn't open the link: " + ex.Message, error: true);
        }
    }

    private void BeginRename(CharacterEntry entry, Panel namePanel)
    {
        var box = new TextBox
        {
            Text = entry.DisplayName,
            MaxLength = 40,
            FontSize = 12,
            Padding = new Thickness(4, 1),
            // Collapse the Fluent default 32px MinHeight so the field hugs the 12px text instead
            // of standing ~1.5x too tall over the name it replaces (same trick as TextBox.sayInput).
            MinHeight = 0,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        namePanel.Children.Clear();
        namePanel.Children.Add(box);
        box.Focus();
        box.SelectAll();

        bool done = false;
        void Commit(bool keepEditingOnDuplicate)
        {
            if (done) return;

            var newName = (box.Text ?? string.Empty).Trim();
            // Refuse a rename that would duplicate another character's name (case-insensitive); two
            // identically-named cards are ambiguous and would also collide on the export file name.
            if (newName.Length > 0 && _store.IsNameTaken(newName, entry.Id))
            {
                Status($"A character named “{newName}” already exists. Choose a different name.", error: true);
                if (keepEditingOnDuplicate)
                {
                    box.SelectAll();
                    box.Focus();
                    return; // committed via Enter: stay in the editor so the user can retype
                }
                done = true; // committed by clicking away: abandon the rename, keep the old name
                Rebuild();
                return;
            }

            done = true;
            _store.Rename(entry.Id, box.Text);
            Status(string.Empty); // clear any prior duplicate warning
            Rebuild();
            _onChanged?.Invoke(); // refresh anything keyed on the (possibly worn) character's name
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Commit(keepEditingOnDuplicate: true); }
            else if (e.Key == Key.Escape) { e.Handled = true; done = true; Rebuild(); }
        };
        box.LostFocus += (_, _) => Commit(keepEditingOnDuplicate: false);
    }

    private async Task ImportAsync()
    {
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null) { Status("Couldn't open the file picker.", error: true); return; }

            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose a character .zip",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Character zip") { Patterns = new[] { "*.zip" } },
                },
            });
            if (files.Count == 0) return;

            var path = files[0].TryGetLocalPath();
            if (path is null) { Status("Couldn't read that file location.", error: true); return; }

            // Extract/copy off the UI thread so a large footage zip doesn't freeze the picker.
            var entry = await Task.Run(() => _store.Import(path));
            _onSelect(entry.Id); // wear the freshly imported character
            Rebuild();
            Status($"Imported “{entry.DisplayName}”. Right-click it to rename.");
        }
        catch (Exception ex)
        {
            Status("Import failed: " + ex.Message, error: true);
        }
    }

    private async Task ExportAsync(CharacterEntry entry)
    {
        try
        {
            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null) { Status("Couldn't open the save dialog.", error: true); return; }

            var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Export character",
                SuggestedFileName = entry.DisplayName + ".zip",
                DefaultExtension = "zip",
                FileTypeChoices = new[]
                {
                    new FilePickerFileType("Character zip") { Patterns = new[] { "*.zip" } },
                },
            });
            var path = file?.TryGetLocalPath();
            if (path is null) return;

            await Task.Run(() => _store.Export(entry.Id, path)); // zip off the UI thread
            Status($"Exported “{entry.DisplayName}” to {path}");
        }
        catch (Exception ex)
        {
            Status("Export failed: " + ex.Message, error: true);
        }
    }

    private async Task DeleteAsync(CharacterEntry entry)
    {
        if (entry.IsBuiltIn) return;
        if (!await ConfirmAsync($"Delete “{entry.DisplayName}”? This removes its files and can't be undone."))
            return;

        bool wasCurrent = string.Equals(entry.Id, _cfg.CurrentCharacterId, StringComparison.Ordinal);
        _store.Delete(entry.Id);
        if (_thumbs.Remove(entry.Id, out var thumb)) thumb?.Dispose();
        if (wasCurrent)
            _onSelect(CharacterStore.DefaultId); // deleting the worn character restores the default
        Rebuild();
        Status($"Deleted “{entry.DisplayName}”.");
    }

    // ------------------------------------------------------------------ thumbnail
    /// <summary>The card thumbnail for a character, rendered once and cached by id.</summary>
    private Bitmap? Thumbnail(CharacterEntry entry)
    {
        if (_thumbs.TryGetValue(entry.Id, out var cached)) return cached;
        var bmp = RenderThumbnail(entry);
        _thumbs[entry.Id] = bmp;
        return bmp;
    }

    /// <summary>Render the character's idle (stand1) frame into a small bitmap for its card. Decodes
    /// only the stand1 pose and disposes that throwaway sprite set once the bitmap is rasterized.</summary>
    private Bitmap? RenderThumbnail(CharacterEntry entry)
    {
        try
        {
            using var sprites = CharacterLoader.Load(_store, entry.Id, ThumbnailPoses);
            if (sprites is null) return null;

            // Draw the footage at 100% — it's native-resolution pixel art, so any non-integer scale
            // would shimmer/blur it. Every character shares one body rig, so at 1:1 their heads already
            // come out the same size with no scaling. Center the figure vertically in the well and
            // horizontally on the navel; anything that overruns the well (a long weapon, a tall hat) is
            // cropped by the bitmap's own bounds.
            int pw = (int)Math.Round(ThumbW), ph = (int)Math.Round(ThumbH);
            var rtb = new RenderTargetBitmap(new PixelSize(pw, ph), new Vector(96, 96));
            using (var ctx = rtb.CreateDrawingContext())
            {
                double centerX = ThumbW / 2;
                // Vertically center the drawn figure (it spans HeightAboveFeet up from the feet), but
                // never push the feet past the bottom edge — a tall hat then crops at the top instead.
                double feetY = Math.Min(ThumbH - ThumbFootMargin, (ThumbH + sprites.HeightAboveFeet) / 2);
                PetRenderer.DrawCharacter(ctx, sprites, "stand1", 0, centerX, feetY, flipHorizontal: false);
            }
            return rtb;
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ small modal confirm
    private async Task<bool> ConfirmAsync(string message)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return false;

        var tcs = new TaskCompletionSource<bool>();
        var dlg = new FrostedWindow("Delete character?")
        {
            Width = 360,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };

        var ok = new Button
        {
            Content = "Delete",
            MinWidth = 88,
            IsDefault = true,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        ok.Classes.Add("danger");
        var cancel = new Button
        {
            Content = "Cancel",
            IsCancel = true,
            MinWidth = 88,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        cancel.Classes.Add("ghost");
        ok.Click += (_, _) => { tcs.TrySetResult(true); dlg.Close(); };
        cancel.Click += (_, _) => { tcs.TrySetResult(false); dlg.Close(); };
        dlg.Closed += (_, _) => tcs.TrySetResult(false); // X / Esc => no

        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap };
        text.Classes.Add("rowLabel");

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, ok },
        };

        var body = new StackPanel
        {
            Margin = new Thickness(20, 6, 20, 18),
            Spacing = 18,
            Children = { text, buttons },
        };
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
