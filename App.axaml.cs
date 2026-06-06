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
                    _chatAgent = new PetChatAgent(control, BuildChatConfig);
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

        var settingsItem = new NativeMenuItem("Settings…") { Icon = glyphs.Render(TrayGlyph.Settings, accent) };
        settingsItem.Click += (_, _) => ShowSettings();

        var exitItem = new NativeMenuItem("Exit") { Icon = glyphs.Render(TrayGlyph.Power, FrostTheme.Exit) };
        exitItem.Click += (_, _) => desktop.Shutdown();

        var menu = new NativeMenu();
        menu.Items.Add(_wearingItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(charactersItem);
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

    /// <summary>Open (or re-focus) the single config window on the requested tab.</summary>
    private void ShowConfig(ConfigTab tab)
    {
        if (_settings is null || _store is null) return;
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
        _configWindow = new ConfigWindow(charactersView, settingsView);
        _configWindow.Select(tab);
        _configWindow.Closed += (_, _) => _configWindow = null;
        _configWindow.Show();
        _configWindow.Activate();
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
            // keyless). The exception is /fortune, which calls the active chat provider — so it's handed the
            // same "chatbot enabled?" + "provider configured?" probes the say bar uses, plus the pet control
            // (it reads the character's expressions and makes the pet wear the one the oracle divines).
            _commands ??= new ChatCommands(
                () => _settings?.EnableChatbot ?? false, BuildChatConfig, () => _petWindow?.Control);
            _sayBar = new SayBarWindow(_settings!, () => _chatAgent, () => _petWindow?.Control,
                () => BuildChatConfig() is not null, _commands);
            _sayBar.HideRequested += HideSayBar;
            _sayBar.Closed += (_, _) =>
            {
                _sayBar = null;
                if (_petWindow is not null) _petWindow.SuppressOverlayTopmost = false;
            };
        }

        _petWindow.SuppressOverlayTopmost = true; // keep the bar above the (topmost) pet overlay while open
        _sayBar.Show();
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
