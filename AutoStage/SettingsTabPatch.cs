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
/// Injects AutoStage settings into the Mods page of the game's Settings window.
///
/// The settings window has no tab bar any more; it is a nav rail whose pages all
/// render into one body child, closed by a single ConsoleStyle.PopWidgetStyle.
/// Inserting the drawer call before that is enough to land inside the body with
/// the console widget style still pushed, and it composes with any other mod
/// doing the same because nothing is replaced. The drawer itself checks which
/// page is open, since the whole body is one code path now.
/// </summary>
[HarmonyPatch(typeof(GameSettings), nameof(GameSettings.OnDrawUi))]
static class SettingsTabPatch
{
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var codes = new List<CodeInstruction>(instructions);
        MethodInfo? anchor = AccessTools.Method(typeof(ConsoleStyle),
            nameof(ConsoleStyle.PopWidgetStyle), Type.EmptyTypes);
        MethodInfo drawer = AccessTools.Method(typeof(SettingsTabPatch), nameof(DrawSettingsPage));

        if (anchor == null)
        {
            DefaultCategory.Log.Warning(
                "[AutoStage] Transpiler: ConsoleStyle.PopWidgetStyle not found; "
                + "settings page not patched.");
            return codes;
        }

        int anchorIdx = -1;
        for (int i = 0; i < codes.Count; i++)
        {
            if (codes[i].Calls(anchor))
            {
                anchorIdx = i;
                break;
            }
        }

        if (anchorIdx < 0)
        {
            DefaultCategory.Log.Warning(
                $"[AutoStage] Transpiler: no ConsoleStyle.PopWidgetStyle() call in "
                + $"GameSettings.OnDrawUi ({codes.Count} IL instructions scanned); "
                + "settings page not patched.");
            return codes;
        }

        // Insert, do not replace: labels stay on the anchor so a jump to it
        // skips the drawer rather than landing mid-call.
        codes.Insert(anchorIdx, new CodeInstruction(OpCodes.Call, drawer));
        return codes;
    }

    public static void DrawSettingsPage()
    {
        if (!GameReflection.IsModsSettingsPageOpen())
            return;

        try
        {
            ConsoleWidgets.Rule();
            ConsoleWidgets.RegionHeader("AUTOSTAGE".AsSpan());
            DrawAutoStageSettings();
        }
        catch (Exception ex)
        {
            LogHelper.ErrorOnce("Settings.Draw",
                $"[AutoStage] Settings draw error: {ex.Message}");
        }
    }

    private static void DrawAutoStageSettings()
    {
        // Takes effect immediately; the Save button below writes it to disk,
        // same as the delay tables.
        bool dropSpentStages = Config.DropSpentStages;
        ConsoleWidgets.BeginRow("DROP SPENT STAGES EARLY".AsSpan());
        if (ConsoleWidgets.Checkbox("AutoStageDropSpent".AsSpan(), ref dropSpentStages, pending: false))
            Config.DropSpentStages = dropSpentStages;
        if (ConsoleWidgets.RowHovered)
            ConsoleWidgets.Tooltip("Stage as soon as the next sequence would shed nothing but burnt-out engines, so spent boosters drop while the core stage keeps firing. Off: staging waits until every active engine is dry.".AsSpan());
        ConsoleWidgets.EndRow();

        // The delay tables need the part library and the sequence internals the
        // ignition-delay reflection resolves; the checkbox above does not.
        if (!Mod.IgnitionDelayAvailable)
        {
            ImGui.TextDisabled("(delay settings unavailable on this game build)"u8);
            return;
        }

        ImGui.TextWrapped(
            "Per-part-variant delays in seconds. Both delays are measured " +
            "from the staging trigger, so set decoupler delay shorter than " +
            "engine delay if you want the decoupler to fire first.");
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

        if (ImGui.CollapsingHeader("Decoupler Delays"u8, ImGuiTreeNodeFlags.DefaultOpen))
        {
            DrawDelayTable(decouplers, "dec",
                get: id => Config.GetDecouplerDelay(id),
                set: (id, v) => Config.DecouplerDelays[id] = v);
        }

        ImGui.Spacing();
        if (ConsoleWidgets.Button("SAVE".AsSpan()))
        {
            Config.SaveGlobalConfig();
            TimedAlert.Create("AutoStage config saved", Color.Green, 2.0);
        }
    }

    private static void DrawDelayTable(List<PartInfo> parts, string idPrefix,
        Func<string, double> get, Action<string, double> set)
    {
        if (parts.Count == 0)
        {
            ImGui.TextDisabled("(no matching parts loaded)"u8);
            return;
        }

        // The row lays the label and control out like every other settings row;
        // the field itself stays an InputFloat so typing an exact delay and the
        // step buttons keep working, which a drag control would take away.
        foreach (PartInfo p in parts)
        {
            float delay = (float)get(p.TemplateId);

            ConsoleWidgets.BeginRow(p.DisplayName.AsSpan());
            ImGui.SetNextItemWidth(ConsoleWidgets.RowControlWidth);
            string inputId = $"###{idPrefix}_{p.TemplateId}";
            if (ImGui.InputFloat(inputId, ref delay, 0.1f, 1.0f, "%.1f"))
                set(p.TemplateId, Math.Max(0.0, (double)delay));
            ConsoleWidgets.EndRow();
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
