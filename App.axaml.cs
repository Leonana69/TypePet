using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MaplePet.Api.Chat;
using MaplePet.Engine;
using MaplePet.Platform;
using MaplePet.Platform.Abstractions;
using MaplePet.Rendering;
using MaplePet.Views;

namespace MaplePet;

public partial class App : Application
{
    private Settings? _settings;
    private CharacterStore? _store;
    private CommandStore? _commandStore;        // the user command library (hot-reloaded skill library)
    private CommandWatcher? _commandWatcher;    // watches the library and rebuilds the registry on change
    private PetWindow? _petWindow;
    private TrayIcon? _trayIcon;
    private ConfigWindow? _configWindow;
    private SayBarWindow? _sayBar;
    private PetChatAgent? _chatAgent;
    private KnowledgeBase? _knowledge;          // bundled MapleStory RAG catalog, loaded once on first chat
    private ChatCommands? _commands;
    private MaplePet.Api.Mcp.PetMcpServer? _mcpServer;

    // The tray header ("MaplePet — <character>"), refreshed when the worn character changes.
    private NativeMenuItem? _wearingItem;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Dev-only: render the character poses to PNGs and exit (no overlay/tray).
            if (AppState.RenderPosesDir is string dir)
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    try { MaplePet.Rendering.PoseRenderTest.Run(dir, AppState.RenderPosesFrom); }
                    catch (Exception ex) { File.WriteAllText(Path.Combine(dir, "ERROR.txt"), ex.ToString()); }
                    desktop.Shutdown();
                });
                base.OnFrameworkInitializationCompleted();
                return;
            }

            // Resolve writable on-disk locations per platform (a signed macOS .app bundle's
            // Contents/MacOS is read-only, so settings/characters live under ~/Library/Application
            // Support/MaplePet there; Windows and dev runs keep their existing locations).
            var paths = PlatformServices.AppPaths;
            try { Directory.CreateDirectory(paths.DataRoot); } catch { /* best effort */ }
            _settings = Settings.Load(paths.SettingsPath);
            _store = new CharacterStore(paths.CharactersRoot);

            // If the remembered character's folder is gone (deleted out-of-band while closed), fall
            // back to the default and persist it, so the pet and the picker's highlight agree.
            if (_store.Get(_settings.CurrentCharacterId) is null)
            {
                _settings.CurrentCharacterId = CharacterStore.DefaultId;
                _settings.Save();
            }

            // The user command library (the hot-reloaded "skill" library). Built eagerly — before the say
            // bar's first open — so commands dropped into the folder register immediately and the watcher
            // can rebuild the registry live. First run seeds the bundled starter commands; the watcher
            // marshals its rebuild onto the UI thread (the registry's snapshot is read by the say bar).
            _commandStore = new CommandStore(paths.CommandsRoot);
            SeedBundledCommands(_commandStore.Root);
            _commands = BuildCommands();
            // Re-arm any persisted /remind reminders (recurring ones recompute their next fire; one-offs
            // whose time passed while closed are dropped). Safe before the pet is ready — the scheduler
            // resolves the pet lazily at fire time.
            _commands.LoadReminders(_settings.Reminders);
            _commandWatcher = new CommandWatcher(_commandStore.Root,
                () => Avalonia.Threading.Dispatcher.UIThread.Post(() => _commands?.Rebuild()));
            desktop.Exit += (_, _) => _commandWatcher?.Dispose();

            _petWindow = new PetWindow(_settings, _store);
            desktop.MainWindow = _petWindow;
            SetupTrayIcon(desktop);

            // Pop up the floating input bar when the pet is double-clicked or the global hotkey fires.
            // It's the single input surface: when the chatbot is enabled + configured, what you type goes
            // to the LLM and the pet speaks the reply; otherwise the pet just says what you typed.
            _petWindow.SayInputRequested += ShowSayInput;

            // Build the in-app chatbot agent once the control facade is live. The agent reads the active
            // provider + key from settings/secret store per message, so a provider change applies at once.
            _petWindow.ControlReady += () =>
            {
                if (_petWindow?.Control is { } control)
                    // Pass the slash-command runner so the chatbot's set_reminder/list/cancel tools schedule
                    // real reminders through the same ChatCommands (and the same shared scheduler).
                    _chatAgent = new PetChatAgent(control, BuildChatConfig,
                        (cmd, ct) => _commands?.RunAsync(cmd, ct)
                            ?? System.Threading.Tasks.Task.FromResult(CommandResult.Error("Reminders aren't ready yet.")));
            };

            // Re-launching MaplePet while it's running exits the second process at once (see Program.Main),
            // but it pokes us on the way out; acknowledge with a quick speech bubble so the double-click
            // isn't silent. The poke arrives on a thread-pool thread, so marshal onto the UI thread.
            if (MaplePet.Platform.PlatformServices.SingleInstance is { } single)
                single.Activated += () =>
                    Avalonia.Threading.Dispatcher.UIThread.Post(AcknowledgeSecondInstance);

            // Optionally expose the pet over a local MCP tool server so an LLM/agent can drive it.
            // Started once the control facade is live (end of PetWindow.OnOpened); off by default.
            if (_settings.EnableMcpServer)
            {
                int port = _settings.McpPort;
                _petWindow.ControlReady += () =>
                {
                    if (_petWindow?.Control is { } control)
                        _mcpServer = MaplePet.Api.Mcp.PetMcpServer.Start(control, port);
                };
                desktop.Exit += (_, _) => _mcpServer?.Stop();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void SetupTrayIcon(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var accent = FrostTheme.Accent;

        // A non-clickable header that names the app and the worn character.
        _wearingItem = new NativeMenuItem(WearingLabel())
        {
            Icon = AppIcon.Bitmap,
            IsEnabled = false,
        };

        var glyphs = PlatformServices.TrayGlyphs;

        var charactersItem = new NativeMenuItem("Characters…") { Icon = glyphs.Render(TrayGlyph.Contact, accent) };
        charactersItem.Click += (_, _) => ShowCharacters();

        var commandsItem = new NativeMenuItem("Commands…") { Icon = glyphs.Render(TrayGlyph.Settings, accent) };
        commandsItem.Click += (_, _) => ShowCommands();

        var settingsItem = new NativeMenuItem("Settings…") { Icon = glyphs.Render(TrayGlyph.Settings, accent) };
        settingsItem.Click += (_, _) => ShowSettings();

        var exitItem = new NativeMenuItem("Exit") { Icon = glyphs.Render(TrayGlyph.Power, FrostTheme.Exit) };
        exitItem.Click += (_, _) => desktop.Shutdown();

        var menu = new NativeMenu();
        menu.Items.Add(_wearingItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(charactersItem);
        menu.Items.Add(commandsItem);
        menu.Items.Add(settingsItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(exitItem);

        // Belt-and-suspenders header refresh when the menu opens (a no-op on Windows: the Win32 tray
        // backend never raises NativeMenu.Opening — only macOS does). The header is also kept current
        // by ApplyCharacter and the picker's onChanged callback.
        menu.Opening += (_, _) =>
        {
            if (_wearingItem is not null)
                _wearingItem.Header = WearingLabel();
        };

        _trayIcon = new TrayIcon
        {
            Icon = AppIcon.WindowIcon(),
            ToolTipText = AppInfo.NameAndVersion,
            Menu = menu,
            IsVisible = true,
        };

        TrayIcon.SetIcons(this, new TrayIcons { _trayIcon });
    }

    private string WearingLabel()
    {
        var name = _store?.Get(_settings?.CurrentCharacterId ?? CharacterStore.DefaultId)?.DisplayName ?? "Default";
        return $"MaplePet — {name}";
    }

    /// <summary>The running pet's response to a blocked second launch — a brief speech bubble so the
    /// user gets visible feedback instead of a silently-ignored double-click. Control is null only in
    /// the sliver before the pet finishes opening; a missed acknowledgement there is harmless. Skipped
    /// while the overlay is hidden behind a fullscreen app: the game loop is frozen then, so the bubble
    /// wouldn't render and its countdown wouldn't tick — it would instead pop up, stale, when the user
    /// later returns to the desktop.</summary>
    private void AcknowledgeSecondInstance()
    {
        if (_petWindow is { IsOverlayHidden: false })
            _ = _petWindow.Control?.Say("I'm already here!");
    }

    private void ShowSettings() => ShowConfig(ConfigTab.Settings);
    private void ShowCharacters() => ShowConfig(ConfigTab.Characters);
    private void ShowCommands() => ShowConfig(ConfigTab.Commands);

    /// <summary>Open (or re-focus) the single config window on the requested tab.</summary>
    private void ShowConfig(ConfigTab tab)
    {
        if (_settings is null || _store is null || _commandStore is null) return;
        if (_configWindow is not null)
        {
            _configWindow.Select(tab);
            _configWindow.Activate();
            return;
        }

        // (true) while the user is capturing a hotkey -> suspend the live hook so it doesn't swallow the
        // very chord being captured; (false) when done -> re-arm with the (possibly new) saved gesture.
        var settingsView = new SettingsView(_settings, capturing =>
        {
            if (capturing) _petWindow?.SuspendSayHotkey();
            else _petWindow?.SetSayHotkey(_settings.SayInputHotkey);
        });
        var charactersView = new CharacterView(_store, _settings, ApplyCharacter, () =>
        {
            if (_wearingItem is not null) _wearingItem.Header = WearingLabel(); // refresh after a rename
        });
        // Toggling/deleting a command persists to settings and rebuilds the live registry at once.
        var commandsView = new CommandsView(_commandStore, _settings, () => _commands?.Rebuild());
        _configWindow = new ConfigWindow(charactersView, commandsView, settingsView);
        _configWindow.Select(tab);
        _configWindow.Closed += (_, _) => _configWindow = null;
        CenterOnPetScreen(_configWindow, 660, 600); // ConfigWindow's fixed logical size
        _configWindow.Show();
        _configWindow.Activate();
    }

    /// <summary>Position a window centered on the display the pet is currently on (falls back to the
    /// platform default if the pet's screen can't be resolved). Call before Show, with the window's
    /// known logical size.</summary>
    private void CenterOnPetScreen(Window w, double logicalW, double logicalH)
    {
        if (_petWindow?.CurrentScreenBounds is not { } b) return;
        var screen = _petWindow.Screens?.ScreenFromBounds(b);
        if (screen is null) return;
        double sc = screen.Scaling;
        var wa = screen.WorkingArea; // physical px, excludes the menu bar / dock
        int pw = (int)Math.Round(logicalW * sc);
        int ph = (int)Math.Round(logicalH * sc);
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Position = new PixelPoint(wa.X + (wa.Width - pw) / 2, wa.Y + (wa.Height - ph) / 2);
    }

    /// <summary>Open (or re-focus) the floating say-input bar; what the user types is spoken by the
    /// pet via the control API. Invoked by a pet double-click or the global hotkey.</summary>
    private void ShowSayInput()
    {
        if (_petWindow is null) return;

        // A persistent singleton: the say bar is hidden (not destroyed) so the conversation history and
        // agent context survive between opens. It owns the chat flow (say vs LLM, history, the pet's
        // thinking animation + spoken reply); the app only shows/hides it.
        if (_sayBar is null)
        {
            // Slash commands run locally; most need no LLM (/rank picks its server per call via a flag, all
            // keyless). The exception is /fortune, which calls the active chat provider. The registry merges
            // these built-ins with the user's command library; it's built eagerly at startup (so dropped
            // commands register before the first open), with a fallback here for safety.
            _commands ??= BuildCommands();
            _sayBar = new SayBarWindow(_settings!, () => _chatAgent, () => _petWindow?.Control,
                () => BuildChatConfig() is not null, _commands,
                () => _petWindow?.CurrentScreenBounds);
            _sayBar.HideRequested += HideSayBar;
            _sayBar.Closed += (_, _) =>
            {
                _sayBar = null;
                if (_petWindow is not null) _petWindow.SuppressOverlayTopmost = false;
            };
        }

        _petWindow.SuppressOverlayTopmost = true; // keep the bar above the (topmost) pet overlay while open
        _sayBar.Show();
        _sayBar.SnapToScreen(); // the pet may have moved to another display since the last open
        _sayBar.Activate();

        // The bar is summoned while another app owns the foreground (global hotkey) or the click that
        // summoned it was swallowed by the mouse hook, so Windows' foreground lock lets plain Activate()
        // raise the bar WITHOUT giving it keyboard focus. Force it to the foreground (no-op on macOS),
        // then move Avalonia focus into the text box — once the native window exists.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (_sayBar is null) return;
            PlatformServices.ForceForeground(_sayBar.TryGetPlatformHandle()?.Handle ?? 0);
            _sayBar.Activate();
            _sayBar.FocusInput();
        }, Avalonia.Threading.DispatcherPriority.Input);
    }

    /// <summary>Hide (not destroy) the say bar and stop suppressing the overlay's topmost re-assert.</summary>
    private void HideSayBar()
    {
        _sayBar?.Hide();
        if (_petWindow is not null) _petWindow.SuppressOverlayTopmost = false;
    }

    /// <summary>Build the slash-command registry: the built-ins (/rank, /fortune, /clear, /help) merged
    /// with the enabled user commands from <see cref="_commandStore"/>. The same probes the say bar uses
    /// are forwarded so /fortune and prompt-kind commands can reach the active provider, plus the live pet
    /// control and the user's scripts-enabled / disabled-ids settings (read fresh each rebuild). The
    /// reminder-changed callback persists the /remind list to settings (see <see cref="PersistReminders"/>).</summary>
    private ChatCommands BuildCommands() => new(
        () => _settings?.EnableChatbot ?? false,
        BuildChatConfig,
        () => _petWindow?.Control,
        _commandStore,
        () => (IReadOnlyCollection<string>?)_settings?.DisabledCommandIds ?? Array.Empty<string>(),
        () => _settings?.EnableUserScripts ?? true,
        (id, hosts) => _settings is not null
            && _settings.IsNetworkApproved(id, MaplePet.Engine.CommandManifest.HostsSignature(hosts)),
        PersistReminders);

    /// <summary>Write the current reminders back to <c>settings.json</c>. Marshaled onto the UI thread so
    /// concurrent timer-thread fires and UI edits never race on the file write; the snapshot itself is taken
    /// thread-safely by the scheduler.</summary>
    private void PersistReminders() => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
    {
        if (_settings is null || _commands is null) return;
        _settings.Reminders = _commands.ReminderSnapshot().ToList();
        _settings.Save();
    });

    /// <summary>On first run (an empty library), copy the bundled starter commands out of the app bundle
    /// (<c>avares://MaplePet/Assets/Commands/**</c>) into the writable commands root. A non-empty root —
    /// already seeded, or the repo's own <c>Assets/Commands</c> in a dev run — is left untouched, so user
    /// edits are never clobbered. Best-effort: a failure just leaves the library empty.</summary>
    private static void SeedBundledCommands(string root)
    {
        try
        {
            if (Directory.Exists(root) && Directory.EnumerateDirectories(root).Any()) return;
            Directory.CreateDirectory(root);

            const string marker = "/Assets/Commands/";
            foreach (var asset in Avalonia.Platform.AssetLoader.GetAssets(
                         new Uri("avares://MaplePet/Assets/Commands/"), null))
            {
                int idx = asset.AbsolutePath.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (idx < 0) continue;
                string rel = asset.AbsolutePath[(idx + marker.Length)..];     // e.g. cmd_ssc/command.md
                if (rel.Length == 0) continue;
                string dest = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                using var s = Avalonia.Platform.AssetLoader.Open(asset);
                using var fs = File.Create(dest);
                s.CopyTo(fs);
            }
        }
        catch { /* best effort; the user can still drop commands in manually */ }
    }

    /// <summary>Resolve the active provider into a chat session config (backend + model + web search),
    /// reading the key from the secret store. Returns null when nothing usable is configured — the agent
    /// then tells the user to add a key. Called per message so a provider/key change applies at once.</summary>
    private ChatSessionConfig? BuildChatConfig()
    {
        if (_settings is null) return null;
        var profile = _settings.Providers.FirstOrDefault(p => p.Id == _settings.ActiveProviderId)
                      ?? _settings.Providers.FirstOrDefault();
        if (profile is null) return null;

        var secrets = PlatformServices.SecretStore;
        string key = profile.UsesKey ? (secrets.Get(profile.Id) ?? "") : "";
        if (profile.UsesKey && string.IsNullOrEmpty(key)) return null; // needs a key but none stored

        try
        {
            var backend = ChatBackendFactory.Create(profile.Kind, profile.BaseUrl, key, profile.Model);
            // Web search/fetch and the MapleStory knowledge base are keyless — enabled unless turned off.
            // The catalog is loaded once and reused (it's static, bundled data).
            WebTools? web = _settings.EnableWebSearch ? new WebTools() : null;
            KnowledgeBase? knowledge = _settings.EnableMapleKnowledge ? (_knowledge ??= KnowledgeBase.LoadBundled()) : null;
            return new ChatSessionConfig(backend, profile.Model, profile.MaxTokens, web, knowledge);
        }
        catch
        {
            return null; // a malformed base URL / model would otherwise throw out of the backend ctor
        }
    }

    /// <summary>Make the pet wear the given character and remember the choice. Invoked by the
    /// character picker when a card is selected, a new import is added, or the worn one is deleted.</summary>
    private void ApplyCharacter(string id)
    {
        if (_settings is null || _store is null || _petWindow is null) return;

        var sprites = CharacterLoader.Load(_store, id,
            hitTestPoses: CharacterAnimator.ActivePoses, posesToLoad: CharacterAnimator.LivePoses, loadExpressions: true);
        _petWindow.SetCharacter(sprites);
        _settings.CurrentCharacterId = id;
        _settings.Save();

        // Keep the tray header's "wearing" line current.
        if (_wearingItem is not null) _wearingItem.Header = WearingLabel();
    }

}
