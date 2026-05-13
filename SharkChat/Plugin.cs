using System;
using System.Runtime.InteropServices;
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

    // ── Hook 1: RaptureShellModule.ProcessLine ────────────────────────────────
    // Called when the player submits an explicit slash-command, e.g. "/tell
    // Player@Server hello" or "/say hello".  Fires reliably for /tell.
    private delegate void ProcessLineDelegate(
        RaptureShellModule* module, byte* text, ulong length);

    private readonly Hook<ProcessLineDelegate>? _processLineHook;

    // ── Hook 2: UIModule.ProcessChatBoxEntry ──────────────────────────────────
    // Called by the chat-box UI component for channel-mode messages — i.e. when
    // the player has a channel (say/fc/shout/yell) selected in the mode picker
    // and presses Enter without typing a leading '/'.  This is the code path
    // that ProcessLine never sees.
    private delegate void ProcessChatBoxEntryDelegate(
        UIModule* uiModule, Utf8String* message, nint a3, bool saveToHistory);

    private readonly Hook<ProcessChatBoxEntryDelegate>? _chatBoxHook;

    private readonly WindowSystem _windowSystem = new("SharkChat");
    private readonly MainWindow   _mainWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // ── Hook 1 ────────────────────────────────────────────────────────────
        try
        {
            var addr = (nint)RaptureShellModule.MemberFunctionPointers.ProcessLine;
            Log.Debug($"[SharkChat] ProcessLine address: 0x{addr:X}");
            _processLineHook = GameInterop.HookFromAddress<ProcessLineDelegate>(
                addr, ProcessLineDetour);
            _processLineHook.Enable();
            Log.Information("[SharkChat] ProcessLine hook enabled.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SharkChat] Failed to hook RaptureShellModule.ProcessLine.");
        }

        // ── Hook 2 ────────────────────────────────────────────────────────────
        try
        {
            var addr = (nint)UIModule.MemberFunctionPointers.ProcessChatBoxEntry;
            Log.Debug($"[SharkChat] ProcessChatBoxEntry address: 0x{addr:X}");
            _chatBoxHook = GameInterop.HookFromAddress<ProcessChatBoxEntryDelegate>(
                addr, ProcessChatBoxEntryDetour);
            _chatBoxHook.Enable();
            Log.Information("[SharkChat] ProcessChatBoxEntry hook enabled.");
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SharkChat] Failed to hook UIModule.ProcessChatBoxEntry.");
            ChatGui.PrintError(
                "[SharkChat] One or more chat hooks failed to load. " +
                "Substitutions may be incomplete. Check /xllog for details.");
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

        _processLineHook?.Disable();
        _processLineHook?.Dispose();

        _chatBoxHook?.Disable();
        _chatBoxHook?.Dispose();
    }

    // ── Hook 1 detour — explicit /commands (/tell, /say <text>, etc.) ─────────

    private void ProcessLineDetour(RaptureShellModule* module, byte* text, ulong length)
    {
        // Unconditional entry log so we can see every invocation in /xllog,
        // even before the configuration guard below.
        Log.Debug($"[SharkChat] ProcessLine detour — len={length}");

        if (Configuration.Enabled && text != null && length > 0 && Configuration.Rules.Count > 0)
        {
            try
            {
                var original = Marshal.PtrToStringUTF8((nint)text, (int)length) ?? string.Empty;
                Log.Debug($"[SharkChat] ProcessLine — original: '{original}'");

                var modified = Substitutor.Apply(original, Configuration.Rules);

                if (!string.Equals(modified, original, StringComparison.Ordinal))
                {
                    Log.Debug($"[SharkChat] ProcessLine — sending modified: '{modified}'");

                    var bytes = Encoding.UTF8.GetBytes(modified);
                    fixed (byte* ptr = bytes)
                    {
                        _processLineHook!.Original(module, ptr, (ulong)bytes.Length);
                    }
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[SharkChat] ProcessLine — error applying substitutions.");
            }
        }

        _processLineHook!.Original(module, text, length);
    }

    // ── Hook 2 detour — channel-mode messages (say/fc/shout/yell) ────────────

    private void ProcessChatBoxEntryDetour(
        UIModule* uiModule, Utf8String* message, nint a3, bool saveToHistory)
    {
        // Unconditional entry log so we can confirm this fires for say/fc/etc.
        Log.Debug("[SharkChat] ProcessChatBoxEntry detour called");

        if (Configuration.Enabled && message != null && Configuration.Rules.Count > 0)
        {
            try
            {
                var original = message->ToString();
                Log.Debug($"[SharkChat] ProcessChatBoxEntry — original: '{original}'");

                var modified = Substitutor.Apply(original, Configuration.Rules);

                if (!string.Equals(modified, original, StringComparison.Ordinal))
                {
                    Log.Debug($"[SharkChat] ProcessChatBoxEntry — sending modified: '{modified}'");

                    Utf8String modifiedStr = default;
                    modifiedStr.Ctor();

                    var bytes = Encoding.UTF8.GetBytes(modified + '\0');
                    fixed (byte* ptr = bytes)
                    {
                        modifiedStr.SetString(ptr);
                    }

                    _chatBoxHook!.Original(uiModule, &modifiedStr, a3, saveToHistory);
                    modifiedStr.Dtor();
                    return;
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[SharkChat] ProcessChatBoxEntry — error applying substitutions.");
            }
        }

        _chatBoxHook!.Original(uiModule, message, a3, saveToHistory);
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
