using System;
using System.Text;
using Dalamud.Game.Command;
using Dalamud.Hooking;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using SharkChat.Windows;

namespace SharkChat;

public sealed unsafe class Plugin : IDalamudPlugin
{
    private const string Command = "/shark";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager         CommandManager  { get; private set; } = null!;
    [PluginService] internal static IChatGui                ChatGui         { get; private set; } = null!;
    [PluginService] internal static IPluginLog              Log             { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider    GameInterop     { get; private set; } = null!;

    public Configuration Configuration { get; init; }

    // UIModule.ProcessChatBoxEntry is the function the chat-box UI calls when the
    // player presses Enter.  It fires for channel-mode messages (say/fc/shout/yell
    // typed without a leading '/') as well as explicit /commands like /tell.
    private delegate void ProcessChatBoxEntryDelegate(
        UIModule* uiModule, Utf8String* message, nint a3, bool saveToHistory);

    private readonly Hook<ProcessChatBoxEntryDelegate>? _hook;
    private readonly WindowSystem _windowSystem = new("SharkChat");
    private readonly MainWindow   _mainWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        try
        {
            var addr = (nint)UIModule.MemberFunctionPointers.ProcessChatBoxEntry;
            Log.Debug($"[SharkChat] ProcessChatBoxEntry address: 0x{addr:X}");
            _hook = GameInterop.HookFromAddress<ProcessChatBoxEntryDelegate>(
                addr, ProcessChatBoxEntryDetour);
            _hook.Enable();
            Log.Information("[SharkChat] ProcessChatBoxEntry hook enabled.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SharkChat] Failed to hook UIModule.ProcessChatBoxEntry.");
            ChatGui.PrintError(
                "[SharkChat] Could not hook into chat — substitutions will not work. " +
                "Check /xllog for details.");
        }

        _mainWindow = new MainWindow(Configuration);
        _windowSystem.AddWindow(_mainWindow);

        PluginInterface.UiBuilder.Draw         += _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += _mainWindow.Toggle;

        CommandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle SharkChat on/off. Use '/shark config' to open settings.",
        });
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(Command);

        PluginInterface.UiBuilder.Draw         -= _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= _mainWindow.Toggle;

        _hook?.Disable();
        _hook?.Dispose();
    }

    // ── Hook detour ───────────────────────────────────────────────────────────

    private void ProcessChatBoxEntryDetour(
        UIModule* uiModule, Utf8String* message, nint a3, bool saveToHistory)
    {
        // Unconditional — fires before any guard so we can see every invocation
        // in /xllog regardless of whether rules are configured or enabled.
        Log.Debug("[SharkChat] ProcessChatBoxEntry detour called");

        if (Configuration.Enabled && message != null && Configuration.Rules.Count > 0)
        {
            try
            {
                var original = message->ToString();
                Log.Debug($"[SharkChat] Original: '{original}'");

                var modified = Substitutor.Apply(original, Configuration.Rules);

                if (!string.Equals(modified, original, StringComparison.Ordinal))
                {
                    Log.Debug($"[SharkChat] Sending modified: '{modified}'");

                    Utf8String modifiedStr = default;
                    modifiedStr.Ctor();

                    var bytes = Encoding.UTF8.GetBytes(modified + '\0');
                    fixed (byte* ptr = bytes)
                    {
                        modifiedStr.SetString(ptr);
                    }

                    _hook!.Original(uiModule, &modifiedStr, a3, saveToHistory);
                    modifiedStr.Dtor();
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[SharkChat] Error applying substitutions.");
            }
        }

        _hook!.Original(uiModule, message, a3, saveToHistory);
    }

    // ── Command ───────────────────────────────────────────────────────────────

    private void OnCommand(string command, string args)
    {
        var trimmed = args.Trim().ToLowerInvariant();

        if (trimmed is "config" or "settings" or "cfg")
        {
            _mainWindow.IsOpen = true;
            return;
        }

        Configuration.Enabled = !Configuration.Enabled;
        Configuration.Save();
        ChatGui.Print(Configuration.Enabled
            ? "[SharkChat] Substitutions enabled."
            : "[SharkChat] Substitutions disabled.");
    }
}
