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
            DefaultCategory.Log.Error($"[AutoStage] Settings draw error: {ex.Message}");
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
            "Ignition delay per engine variant in seconds. " +
            "After staging, decouplers fire immediately but engines " +
            "wait the configured delay before igniting.");
        ImGui.Spacing();
        ImGui.Spacing();

        List<EngineInfo> engines = GetKnownEngines();
        if (engines.Count > 0)
        {
            // Find the longest display name to align the input fields
            float maxNameWidth = 0f;
            foreach (EngineInfo engine in engines)
            {
                float w = ImGui.CalcTextSize(engine.DisplayName).X;
                if (w > maxNameWidth) maxNameWidth = w;
            }
            // Account for indent offset so SameLine positions correctly
            float indentOffset = ImGui.GetCursorPosX();
            float inputX = indentOffset + maxNameWidth + 15f;

            foreach (EngineInfo engine in engines)
            {
                float delay = (float)Config.GetEngineDelay(engine.TemplateId);

                ImGui.Text(engine.DisplayName);
                ImGui.SameLine(inputX);
                ImGui.SetNextItemWidth(130f);

                string inputId = $"###delay_{engine.TemplateId}";
                if (ImGui.InputFloat(inputId, ref delay, 0.1f, 1.0f, "%.1f"))
                {
                    Config.EngineDelays[engine.TemplateId] = Math.Max(0.0, (double)delay);
                }
            }
        }

        ImGui.Spacing();
        if (ImGui.Button("Save"u8, (float2?)null))
        {
            Config.SaveGlobalConfig();
            Alert.Create("AutoStage config saved", Color.Green, 2.0);
        }

        ImGui.Unindent();
    }

    private struct EngineInfo
    {
        public string TemplateId;
        public string DisplayName;
    }

    private static List<EngineInfo>? _knownEngines;

    private static List<EngineInfo> GetKnownEngines()
    {
        if (_knownEngines != null)
            return _knownEngines;

        _knownEngines = new List<EngineInfo>();
        try
        {
            object? allParts = GameReflection.ModLibrary_AllParts?.GetValue(null);
            if (allParts == null) return _knownEngines;

            MethodInfo? getList = allParts.GetType().GetMethod("GetList");
            if (getList?.Invoke(allParts, null) is not System.Collections.IList list)
                return _knownEngines;

            var raw = new List<(string id, string name)>();
            foreach (object? item in list)
            {
                if (item is PartTemplate template
                    && template.RocketEngineControllers.Count > 0)
                {
                    raw.Add((template.Id, template.DisplayName));
                }
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
                    // Extract short variant from TemplateId, e.g. "EngineA3" from
                    // "CorePropulsionA_Prefab_EngineA3"
                    int lastUnderscore = id.LastIndexOf('_');
                    string suffix = lastUnderscore >= 0 ? id.Substring(lastUnderscore + 1) : id;
                    displayName = $"{name} ({suffix})";
                }
                _knownEngines.Add(new EngineInfo
                {
                    TemplateId = id,
                    DisplayName = displayName
                });
            }

            _knownEngines.Sort((a, b) =>
                string.Compare(a.DisplayName, b.DisplayName, StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Warning(
                $"[AutoStage] Failed to enumerate engine templates: {ex.Message}");
        }
        return _knownEngines;
    }

    internal static void Reset()
    {
        _knownEngines = null;
    }
}
