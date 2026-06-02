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
using MaplePet.Engine;
using MaplePet.Rendering;

namespace MaplePet.Views;

/// <summary>
/// The character picker (opened from the tray menu). Shows one card per available character — the
/// built-in Default plus every imported one — six per row in a scrolling grid, with a trailing "+"
/// card to import a new character from a zip. Clicking a card makes the pet wear it (via the
/// <c>onSelect</c> callback); clicking a card's name renames it; right-clicking offers Export and
/// Delete. The currently worn character is highlighted.
/// </summary>
public sealed class CharacterWindow : Window
{
    private const int Columns = 6;
    private const double CardW = 96, CardH = 128, ThumbSize = 84;

    // A thumbnail only ever draws the idle frame, so decode just that one pose (not the whole footage).
    private static readonly IReadOnlyCollection<string> ThumbnailPoses =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "stand1" };

    private static readonly IBrush CardBg = new SolidColorBrush(Color.FromArgb(20, 128, 128, 128));
    private static readonly IBrush CardBorder = new SolidColorBrush(Color.FromArgb(90, 128, 128, 128));
    private static readonly IBrush CurrentBg = new SolidColorBrush(Color.FromArgb(48, 45, 125, 245));
    private static readonly IBrush CurrentBorder = new SolidColorBrush(Color.FromArgb(255, 45, 125, 245));

    private readonly CharacterStore _store;
    private readonly Settings _cfg;
    private readonly Action<string> _onSelect;

    private readonly UniformGrid _grid;
    private readonly TextBlock _status;

    // Render each card's thumbnail once and reuse it across Rebuilds (which happen on every
    // select/rename/import/delete). Keyed by character id; disposed on delete and on window close.
    private readonly Dictionary<string, Bitmap?> _thumbs = new();

    public CharacterWindow(CharacterStore store, Settings cfg, Action<string> onSelect)
    {
        _store = store;
        _cfg = cfg;
        _onSelect = onSelect;

        Title = "MaplePet Characters";
        Width = Columns * (CardW + 8) + 28; // 6 cells + per-card margins + padding/scrollbar
        Height = 520;
        MinWidth = Width;
        MinHeight = 240;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;

        _grid = new UniformGrid { Columns = Columns };
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new Border { Padding = new Thickness(8), Child = _grid },
        };

        _status = new TextBlock
        {
            Margin = new Thickness(12, 6),
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
        };

        var dock = new DockPanel();
        DockPanel.SetDock(_status, Dock.Bottom);
        dock.Children.Add(_status);
        dock.Children.Add(scroll);
        Content = dock;

        Rebuild();
    }

    protected override void OnClosed(EventArgs e)
    {
        foreach (var bmp in _thumbs.Values) bmp?.Dispose();
        _thumbs.Clear();
        base.OnClosed(e);
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
            Width = ThumbSize,
            Height = ThumbSize,
            Stretch = Stretch.Uniform,
            Source = Thumbnail(entry),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        RenderOptions.SetBitmapInterpolationMode(thumb, BitmapInterpolationMode.None);

        var nameBlock = new TextBlock
        {
            Text = entry.DisplayName,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        var namePanel = new Panel { Children = { nameBlock } };

        var content = new StackPanel
        {
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { thumb, namePanel },
        };

        var card = new Border
        {
            Width = CardW,
            Height = CardH,
            Margin = new Thickness(4),
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(8),
            Background = isCurrent ? CurrentBg : CardBg,
            BorderBrush = isCurrent ? CurrentBorder : CardBorder,
            BorderThickness = new Thickness(isCurrent ? 2 : 1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = content,
        };

        // Left-click anywhere on the card selects it (unless the name handled it for renaming).
        card.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(card).Properties.IsLeftButtonPressed)
                Select(entry);
        };

        if (!entry.IsBuiltIn)
        {
            var export = new MenuItem { Header = "Export…" };
            export.Click += (_, _) => _ = ExportAsync(entry);
            var delete = new MenuItem { Header = "Delete" };
            delete.Click += (_, _) => _ = DeleteAsync(entry);
            card.ContextMenu = new ContextMenu { Items = { export, delete } };

            // Click the name to edit it (the built-in default can't be renamed).
            nameBlock.Cursor = new Cursor(StandardCursorType.Ibeam);
            nameBlock.PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(nameBlock).Properties.IsLeftButtonPressed) return;
                e.Handled = true; // don't also trigger card-select
                BeginRename(entry, namePanel);
            };
        }

        return card;
    }

    private Control BuildAddCard()
    {
        var plus = new TextBlock
        {
            Text = "+",
            FontSize = 38,
            Foreground = CardBorder,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var content = new StackPanel
        {
            Spacing = 6,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Panel { Height = ThumbSize, Children = { plus } },
                new TextBlock { Text = "Add", TextAlignment = TextAlignment.Center, Opacity = 0.7 },
            },
        };

        var card = new Border
        {
            Width = CardW,
            Height = CardH,
            Margin = new Thickness(4),
            Padding = new Thickness(6),
            CornerRadius = new CornerRadius(8),
            Background = CardBg,
            BorderBrush = CardBorder,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = content,
        };
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

    private void BeginRename(CharacterEntry entry, Panel namePanel)
    {
        var box = new TextBox
        {
            Text = entry.DisplayName,
            MaxLength = 40,
            Padding = new Thickness(2, 0),
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
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
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
            Status($"Imported “{entry.DisplayName}”. Click its name to rename it.");
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
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
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

            const double pad = 8;
            double contentW = Math.Max(1, sprites.HalfWidth * 2);
            double contentH = Math.Max(1, sprites.HeightAboveFeet);
            double scale = Math.Min((ThumbSize - pad) / contentW, (ThumbSize - pad) / contentH);
            scale = Math.Clamp(scale, 0.3, 2.0);

            int px = (int)Math.Round(ThumbSize);
            var rtb = new RenderTargetBitmap(new PixelSize(px, px), new Vector(96, 96));
            using (var ctx = rtb.CreateDrawingContext())
            using (ctx.PushTransform(Matrix.CreateScale(scale, scale)))
            {
                double localW = ThumbSize / scale;
                double localH = ThumbSize / scale;
                double centerX = localW / 2;
                // Vertically center the sprite (it spans [feetY - height, feetY]).
                double feetY = localH / 2 + contentH / 2;
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
        var tcs = new TaskCompletionSource<bool>();
        var dlg = new Window
        {
            Title = "Confirm",
            Width = 340,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var ok = new Button { Content = "Delete", MinWidth = 80 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) => { tcs.TrySetResult(true); dlg.Close(); };
        cancel.Click += (_, _) => { tcs.TrySetResult(false); dlg.Close(); };
        dlg.Closed += (_, _) => tcs.TrySetResult(false); // X / Esc => no

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { cancel, ok },
        };
        dlg.Content = new Border
        {
            Padding = new Thickness(16),
            Child = new StackPanel
            {
                Spacing = 14,
                Children = { new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, buttons },
            },
        };

        await dlg.ShowDialog(this);
        return await tcs.Task;
    }

    private void Status(string message, bool error = false)
    {
        _status.Text = message;
        if (error)
            _status.Foreground = new SolidColorBrush(Color.FromRgb(220, 80, 80));
        else
            _status.ClearValue(TextBlock.ForegroundProperty); // revert to the theme default
    }
}
