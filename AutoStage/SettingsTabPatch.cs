using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using AutoStage.Core;
using Brutal.ImGuiApi;
using Brutal.Logging;
using Brutal.Numerics;
using HarmonyLib;
using KSA;

namespace AutoStage;

/// <summary>
/// Injects AutoStage settings into the Mods tab of the game's Settings window.
///
/// The Mods tab uses BeginRegionTab which creates a child window with a 2-column
/// layout for the mod list. We replace the Mods tab's EndRegionTab call with our
/// own method that resets the columns, draws our settings (still inside the child
/// window), then closes everything normally.
/// </summary>
[HarmonyPatch(typeof(GameSettings), nameof(GameSettings.OnDrawUi))]
static class SettingsTabPatch
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);
        MethodInfo endTabBar = AccessTools.Method(typeof(ImGui), nameof(ImGui.EndTabBar));
        MethodInfo endRegionTab = AccessTools.Method(typeof(ImGuiHelper), nameof(ImGuiHelper.EndRegionTab));
        MethodInfo replacement = AccessTools.Method(typeof(SettingsTabPatch), nameof(EndModsTabWithSettings));

        int endTabBarIdx = -1;
        for (int i = codes.Count - 1; i >= 0; i--)
        {
            if (codes[i].Calls(endTabBar))
            {
                endTabBarIdx = i;
                break;
            }
        }

        if (endTabBarIdx < 0)
        {
            DefaultCategory.Log.Warning(
                "[AutoStage] Transpiler: EndTabBar not found in GameSettings.OnDrawUi");
            return codes;
        }

        bool found = false;
        for (int i = endTabBarIdx - 1; i >= 0; i--)
        {
            if (codes[i].Calls(endRegionTab))
            {
                codes[i] = new CodeInstruction(OpCodes.Call, replacement)
                    .MoveLabelsFrom(codes[i]);
                found = true;
                break;
            }
        }

        if (!found)
            DefaultCategory.Log.Warning(
                "[AutoStage] Transpiler: EndRegionTab not found before EndTabBar");

        return codes;
    }

    public static void EndModsTabWithSettings(bool alsoEndRegion)
    {
        // Reset the 2-column layout from the mod list so our content spans full width.
        // We're still inside the child window created by BeginRegionTab.
        ImGui.Columns();

        try
        {
            if (ImGui.CollapsingHeader("AutoStage Settings"u8, ImGuiTreeNodeFlags.DefaultOpen))
                DrawAutoStageSettings();
        }
        catch (Exception ex)
        {
            LogHelper.ErrorOnce("Settings.Draw",
                $"[AutoStage] Settings draw error: {ex.Message}");
        }

        // Original EndRegionTab logic: close region (Columns + EndChild) + EndTabItem
        if (alsoEndRegion)
            ImGuiHelper.EndRegion();
        ImGui.EndTabItem();
    }

    private static void DrawAutoStageSettings()
    {
        ImGui.Indent();

        ImGui.TextWrapped(
            "Per-part-variant delays in seconds. Both delays are measured " +
            "from the staging trigger, so set decoupler delay shorter than " +
            "engine delay if you want the decoupler to fire first.");
        ImGui.Spacing();
        ImGui.Spacing();

        List<PartInfo> engines = GetKnownParts(
            ref _knownEngines, t => t.RocketEngineControllers.Count > 0);
        List<PartInfo> decouplers = GetKnownParts(
            ref _knownDecouplers, t => t.Decoupler != null);

        if (ImGui.CollapsingHeader("Engine Ignition Delays"u8, ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawDelayTable(engines, "eng",
                get: id => Config.GetEngineDelay(id),
                set: (id, v) => Config.EngineDelays[id] = v);
        }

        ImGui.Spacing();
        if (ImGui.CollapsingHeader("Decoupler Delays"u8, ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawDelayTable(decouplers, "dec",
                get: id => Config.GetDecouplerDelay(id),
                set: (id, v) => Config.DecouplerDelays[id] = v);
        }

        ImGui.Spacing();
        if (ImGui.Button("Save"u8, (float2?)null))
        {
            Config.SaveGlobalConfig();
            TimedAlert.Create("AutoStage config saved", Color.Green, 2.0);
        }

        ImGui.Unindent();
    }

    private static void DrawDelayTable(List<PartInfo> parts, string idPrefix,
        Func<string, double> get, Action<string, double> set)
    {
        if (parts.Count == 0)
        {
            ImGui.TextDisabled("(no matching parts loaded)"u8);
            return;
        }

        // Find the longest display name to align the input fields
        float maxNameWidth = 0f;
        foreach (PartInfo p in parts)
        {
            float w = ImGui.CalcTextSize(p.DisplayName).X;
            if (w > maxNameWidth) maxNameWidth = w;
        }
        float indentOffset = ImGui.GetCursorPosX();
        float inputX = indentOffset + maxNameWidth + 15f;

        foreach (PartInfo p in parts)
        {
            float delay = (float)get(p.TemplateId);

            ImGui.Text(p.DisplayName);
            ImGui.SameLine(inputX);
            ImGui.SetNextItemWidth(130f);

            string inputId = $"###{idPrefix}_{p.TemplateId}";
            if (ImGui.InputFloat(inputId, ref delay, 0.1f, 1.0f, "%.1f"))
                set(p.TemplateId, Math.Max(0.0, (double)delay));
        }
    }

    private struct PartInfo
    {
        public string TemplateId;
        public string DisplayName;
    }

    private static List<PartInfo>? _knownEngines;
    private static List<PartInfo>? _knownDecouplers;

    private static List<PartInfo> GetKnownParts(ref List<PartInfo>? cache,
        Func<PartTemplate, bool> filter)
    {
        if (cache != null)
            return cache;

        cache = new List<PartInfo>();
        try
        {
            if (GameReflection.ModLibrary_AllParts?.GetValue(null)
                is not SerializedCollection<PartTemplate> collection)
                return cache;

            var raw = new List<(string id, string name)>();
            foreach (PartTemplate template in collection.GetList())
            {
                if (filter(template))
                    raw.Add((template.Id, template.DisplayName));
            }

            // Find duplicate DisplayNames and disambiguate with a short suffix
            var nameCounts = new Dictionary<string, int>();
            foreach (var (_, name) in raw)
                nameCounts[name] = nameCounts.GetValueOrDefault(name) + 1;

            foreach (var (id, name) in raw)
            {
                string displayName = name;
                if (nameCounts[name] > 1)
                {
                    int lastUnderscore = id.LastIndexOf('_');
                    string suffix = lastUnderscore >= 0 ? id.Substring(lastUnderscore + 1) : id;
                    displayName = $"{name} ({suffix})";
                }
                cache.Add(new PartInfo { TemplateId = id, DisplayName = displayName });
            }

            cache.Sort((a, b) =>
                string.Compare(a.DisplayName, b.DisplayName, StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Warning(
                $"[AutoStage] Failed to enumerate part templates: {ex.Message}");
        }
        return cache;
    }

    internal static void Reset()
    {
        _knownEngines = null;
        _knownDecouplers = null;
    }
}
