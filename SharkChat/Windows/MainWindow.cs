using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace SharkChat.Windows;

public class MainWindow : Window
{
    private readonly Configuration _config;

    // Preview input buffer
    private string _preview = string.Empty;

    public MainWindow(Configuration config) : base("SharkChat##SharkChatMain")
    {
        _config = config;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(640, 420),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
    }

    public override void Draw()
    {
        DrawHeader();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawRuleTable();
        ImGui.Spacing();
        DrawAddButton();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawPreview();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        DrawHelp();
    }

    // ── Header — master toggle ────────────────────────────────────────────────

    private void DrawHeader()
    {
        var enabled = _config.Enabled;
        if (ImGui.Checkbox("##master", ref enabled))
        {
            _config.Enabled = enabled;
            _config.Save();
        }
        ImGui.SameLine();
        if (_config.Enabled)
            ImGui.Text("SharkChat is ON — substitutions active");
        else
        {
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1f),
                "SharkChat is OFF — no substitutions applied");
        }
    }

    // ── Rule table ────────────────────────────────────────────────────────────

    private void DrawRuleTable()
    {
        var rules = _config.Rules;

        if (rules.Count == 0)
        {
            ImGui.TextDisabled("No rules yet. Click “Add Rule” below to create one.");
            return;
        }

        var tableFlags = ImGuiTableFlags.Borders
                       | ImGuiTableFlags.RowBg
                       | ImGuiTableFlags.SizingFixedFit
                       | ImGuiTableFlags.ScrollY;

        float tableH = ImGui.GetContentRegionAvail().Y
                     - ImGui.GetFrameHeightWithSpacing() * 5f; // leave room for buttons + preview

        if (!ImGui.BeginTable("##rules", 6, tableFlags, new Vector2(0, tableH)))
            return;

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("On",          ImGuiTableColumnFlags.WidthFixed,   28);
        ImGui.TableSetupColumn("Replace",     ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("With",        ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Whole\nWord", ImGuiTableColumnFlags.WidthFixed,   46);
        ImGui.TableSetupColumn("Case\nSens",  ImGuiTableColumnFlags.WidthFixed,   46);
        ImGui.TableSetupColumn("",            ImGuiTableColumnFlags.WidthFixed,   24);
        ImGui.TableHeadersRow();

        for (int i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            ImGui.TableNextRow();
            ImGui.PushID(i);

            // Enabled
            ImGui.TableSetColumnIndex(0);
            var en = rule.Enabled;
            if (ImGui.Checkbox("##en", ref en)) { rule.Enabled = en; _config.Save(); }

            // From
            ImGui.TableSetColumnIndex(1);
            ImGui.SetNextItemWidth(-1);
            var from = rule.From;
            if (ImGui.InputText("##from", ref from, 64)) { rule.From = from; _config.Save(); }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("The word or phrase to look for in your typed message.");

            // To
            ImGui.TableSetColumnIndex(2);
            ImGui.SetNextItemWidth(-1);
            var to = rule.To;
            if (ImGui.InputText("##to", ref to, 200)) { rule.To = to; _config.Save(); }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("What it will be replaced with when you send the message.");

            // Whole word
            ImGui.TableSetColumnIndex(3);
            var ww = rule.WholeWordOnly;
            if (ImGui.Checkbox("##ww", ref ww)) { rule.WholeWordOnly = ww; _config.Save(); }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Whole word: only match when the word stands alone.\n" +
                    "e.g. rule [tank → tankies] won’t affect “thanks”.\n" +
                    "Recommended ON.");

            // Case sensitive
            ImGui.TableSetColumnIndex(4);
            var cs = rule.CaseSensitive;
            if (ImGui.Checkbox("##cs", ref cs)) { rule.CaseSensitive = cs; _config.Save(); }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Case sensitive: only match exact capitalisation.\n" +
                    "When OFF, “Thanks”, “thanks” and “THANKS” all match.");

            // Delete
            ImGui.TableSetColumnIndex(5);
            if (ImGui.SmallButton("×"))
            {
                rules.RemoveAt(i);
                _config.Save();
                ImGui.PopID();
                i--;
                continue;
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Delete this rule.");

            ImGui.PopID();
        }

        ImGui.EndTable();
    }

    // ── Add rule button ───────────────────────────────────────────────────────

    private void DrawAddButton()
    {
        if (ImGui.Button("+ Add Rule", new Vector2(120, 0)))
        {
            _config.Rules.Add(new SubstitutionRule());
            _config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Add a new blank substitution rule.");
    }

    // ── Live preview ──────────────────────────────────────────────────────────

    private void DrawPreview()
    {
        ImGui.TextDisabled("Preview — type a message to see how it will be transformed:");
        ImGui.SetNextItemWidth(-1);
        ImGui.InputText("##preview", ref _preview, 500);

        var result = _config.Enabled && _config.Rules.Count > 0
            ? Substitutor.Apply(_preview, _config.Rules)
            : _preview;

        if (result != _preview)
        {
            ImGui.TextColored(new Vector4(0.4f, 1f, 0.5f, 1f), $"→ {result}");
        }
        else
        {
            ImGui.TextDisabled(_config.Rules.Count == 0
                ? "→ (no rules configured)"
                : "→ (no substitutions match)");
        }
    }

    // ── Help footer ───────────────────────────────────────────────────────────

    private void DrawHelp()
    {
        ImGui.TextDisabled("/shark        — toggle substitutions on / off");
        ImGui.TextDisabled("/shark config — open this window");
        ImGui.TextDisabled("Rules are applied in order, top to bottom.");
    }
}
