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

    // In FFXIV 7.5 the function was renamed ProcessChatBoxEntry and the last
    // parameter changed from byte to bool. Using MemberFunctionPointers means
    // the address is resolved by FFXIVClientStructs at runtime, so it survives
    // future patches automatically as long as FFXIVClientStructs is updated.
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
            _hook = GameInterop.HookFromAddress<ProcessChatBoxEntryDelegate>(
                (nint)UIModule.MemberFunctionPointers.ProcessChatBoxEntry,
                ProcessChatBoxEntryDetour);
            _hook.Enable();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SharkChat] Failed to hook ProcessChatBoxEntry.");
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
        if (Configuration.Enabled && message != null && Configuration.Rules.Count > 0)
        {
            try
            {
                var original = message->ToString();
                Log.Debug($"[SharkChat] Hook fired — original: '{original}'");

                var modified = Substitutor.Apply(original, Configuration.Rules);

                if (!string.Equals(modified, original, StringComparison.Ordinal))
                {
                    Log.Debug($"[SharkChat] Sending modified: '{modified}'");

                    // Ctor() initialises the inline buffer and internal fields —
                    // skipping it leaves the struct zeroed and SetString() may
                    // write to a null pointer, silently producing no output.
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
