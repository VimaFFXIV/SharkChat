using System;
using System.Reflection;
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

    // ── Hook 1 : RaptureShellModule.ProcessLine ───────────────────────────────
    // ProcessLine fires for all explicit /commands (/tell confirmed in v1.0.4).
    // We resolve the address via reflection so the code compiles against any
    // Dalamud reference assembly — the type only needs to exist at runtime.
    private delegate void ProcessLineDelegate(nint module, byte* text, ulong length);
    private readonly Hook<ProcessLineDelegate>? _processLineHook;

    // ── Hook 2 : UIModule.ProcessChatBoxEntry ─────────────────────────────────
    // Kept with an unconditional diagnostic log; v1.0.6 confirmed it does NOT
    // fire for channel-mode messages, so it acts as a safety net for any path
    // that does reach it.
    private delegate void ProcessChatBoxEntryDelegate(
        UIModule* uiModule, Utf8String* message, nint a3, bool saveToHistory);
    private readonly Hook<ProcessChatBoxEntryDelegate>? _chatBoxHook;

    private readonly WindowSystem _windowSystem = new("SharkChat");
    private readonly MainWindow   _mainWindow;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        // ── Hook 1 — ProcessLine (reflection address lookup) ──────────────────
        try
        {
            var addr = ResolveProcessLineAddress();
            if (addr.HasValue && addr.Value != 0)
            {
                Log.Debug($"[SharkChat] ProcessLine address (reflection): 0x{addr.Value:X}");
                _processLineHook = GameInterop.HookFromAddress<ProcessLineDelegate>(
                    addr.Value, ProcessLineDetour);
                _processLineHook.Enable();
                Log.Information("[SharkChat] ProcessLine hook enabled.");
            }
            else
            {
                Log.Warning("[SharkChat] RaptureShellModule.ProcessLine not found via " +
                            "reflection — /tell substitutions will not work.");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[SharkChat] Failed to hook ProcessLine.");
        }

        // ── Hook 2 — ProcessChatBoxEntry ──────────────────────────────────────
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
            Log.Error(ex, "[SharkChat] Failed to hook ProcessChatBoxEntry.");
        }

        if (_processLineHook == null && _chatBoxHook == null)
        {
            ChatGui.PrintError(
                "[SharkChat] No chat hooks could be loaded — substitutions will not work. " +
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

    /// <summary>
    /// Walks all loaded assemblies looking for
    /// <c>FFXIVClientStructs.FFXIV.Client.UI.RaptureShellModule.MemberFunctionPointers.ProcessLine</c>
    /// as either a static property or a static field returning <see cref="nint"/>.
    /// Returns <c>null</c> when the type is absent (e.g. in some Dalamud builds).
    /// </summary>
    private static nint? ResolveProcessLineAddress()
    {
        const string typeName = "FFXIVClientStructs.FFXIV.Client.UI.RaptureShellModule";
        const string mfpName  = "MemberFunctionPointers";
        const string fnName   = "ProcessLine";

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            var rsm = asm.GetType(typeName);
            if (rsm == null) continue;

            var mfp = rsm.GetNestedType(mfpName,
                BindingFlags.Public | BindingFlags.NonPublic);
            if (mfp == null) break;

            // Try property first (source-generated FFXIVClientStructs ≥ v2).
            var prop = mfp.GetProperty(fnName,
                BindingFlags.Public | BindingFlags.Static);
            if (prop?.GetValue(null) is nint pa && pa != 0)
                return pa;

            // Fall back to field (older generated output).
            var field = mfp.GetField(fnName,
                BindingFlags.Public | BindingFlags.Static);
            if (field?.GetValue(null) is nint fa && fa != 0)
                return fa;

            break; // found the type, but couldn't read the address
        }

        return null;
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

    // ── Hook 1 detour ─────────────────────────────────────────────────────────

    private void ProcessLineDetour(nint module, byte* text, ulong length)
    {
        // UNCONDITIONAL — appears in /xllog for every invocation.
        Log.Debug($"[SharkChat] ProcessLine fired — len={length}");

        if (Configuration.Enabled && text != null && Configuration.Rules.Count > 0)
        {
            try
            {
                // Accept both length-prefixed and null-terminated strings.
                var original = length > 0
                    ? (Marshal.PtrToStringUTF8((nint)text, (int)length) ?? string.Empty)
                    : (Marshal.PtrToStringUTF8((nint)text) ?? string.Empty);

                if (!string.IsNullOrEmpty(original))
                {
                    Log.Debug($"[SharkChat] ProcessLine — original: '{original}'");
                    var modified = Substitutor.Apply(original, Configuration.Rules);

                    if (!string.Equals(modified, original, StringComparison.Ordinal))
                    {
                        Log.Debug($"[SharkChat] ProcessLine — modified: '{modified}'");
                        var bytes = Encoding.UTF8.GetBytes(modified);
                        fixed (byte* ptr = bytes)
                        {
                            _processLineHook!.Original(module, ptr, (ulong)bytes.Length);
                        }
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[SharkChat] ProcessLine error.");
            }
        }

        _processLineHook!.Original(module, text, length);
    }

    // ── Hook 2 detour ─────────────────────────────────────────────────────────

    private void ProcessChatBoxEntryDetour(
        UIModule* uiModule, Utf8String* message, nint a3, bool saveToHistory)
    {
        // UNCONDITIONAL — appears in /xllog for every invocation.
        Log.Debug("[SharkChat] ProcessChatBoxEntry fired");

        if (Configuration.Enabled && message != null && Configuration.Rules.Count > 0)
        {
            try
            {
                var original = message->ToString();
                Log.Debug($"[SharkChat] ProcessChatBoxEntry — original: '{original}'");

                var modified = Substitutor.Apply(original, Configuration.Rules);

                if (!string.Equals(modified, original, StringComparison.Ordinal))
                {
                    Log.Debug($"[SharkChat] ProcessChatBoxEntry — modified: '{modified}'");

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
                Log.Error(ex, "[SharkChat] ProcessChatBoxEntry error.");
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
