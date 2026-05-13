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

    // ProcessChatBox — the game function that receives and dispatches all typed
    // chat input. Signature valid for FFXIV 7.x / Dalamud API 15.
    // If this stops working after a patch, update the signature here.
    private const string ProcessChatBoxSig =
        "48 89 5C 24 ?? 57 48 83 EC 20 48 8B FA 48 8B D9 45 84 C9";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager         CommandManager  { get; private set; } = null!;
    [PluginService] internal static IChatGui                ChatGui         { get; private set; } = null!;
    [PluginService] internal static IPluginLog              Log             { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider    GameInterop     { get; private set; } = null!;

    public Configuration Configuration { get; init; }

    private delegate void ProcessChatBoxDelegate(
        UIModule* uiModule, Utf8String* message, nint unused, byte a4);

    private readonly Hook<ProcessChatBoxDelegate>? _hook;
    private readonly WindowSystem _windowSystem = new("SharkChat");
    private readonly MainWindow   _mainWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // Hook the chat-send function.
        try
        {
            _hook = GameInterop.HookFromSignature<ProcessChatBoxDelegate>(
                ProcessChatBoxSig, ProcessChatBoxDetour);
            _hook.Enable();
        }
        catch (Exception ex)
        {
            Log.Error(ex,
                "[SharkChat] Failed to hook ProcessChatBox. " +
                "The signature may need updating for this patch.");
            ChatGui.PrintError(
                "[SharkChat] Could not hook into chat — substitutions will not work. " +
                "Check /xllog for details.");
        }

        _mainWindow = new MainWindow(Configuration);
        _windowSystem.AddWindow(_mainWindow);

        PluginInterface.UiBuilder.Draw        += _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += _mainWindow.Toggle;

        CommandManager.AddHandler(Command, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle SharkChat on/off. Use '/shark config' to open settings.",
        });
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(Command);

        PluginInterface.UiBuilder.Draw        -= _windowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= _mainWindow.Toggle;

        _hook?.Disable();
        _hook?.Dispose();
    }

    // ── Hook detour ───────────────────────────────────────────────────────────

    private void ProcessChatBoxDetour(
        UIModule* uiModule, Utf8String* message, nint unused, byte a4)
    {
        if (Configuration.Enabled && message != null && Configuration.Rules.Count > 0)
        {
            try
            {
                var original = message->ToString();
                var modified = Substitutor.Apply(original, Configuration.Rules);

                if (!string.Equals(modified, original, StringComparison.Ordinal))
                {
                    // Stack-allocate a new Utf8String for the modified text and
                    // pass that to the original function instead.
                    var bytes = Encoding.UTF8.GetBytes(modified + '\0');
                    Utf8String modifiedStr = default;
                    fixed (byte* ptr = bytes)
                    {
                        modifiedStr.SetString(ptr);
                    }
                    _hook!.Original(uiModule, &modifiedStr, unused, a4);
                    modifiedStr.Dtor();
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[SharkChat] Error applying substitutions.");
            }
        }

        _hook!.Original(uiModule, message, unused, a4);
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

        // No args — toggle master switch
        Configuration.Enabled = !Configuration.Enabled;
        Configuration.Save();
        ChatGui.Print(Configuration.Enabled
            ? "[SharkChat] Substitutions enabled."
            : "[SharkChat] Substitutions disabled.");
    }
}
