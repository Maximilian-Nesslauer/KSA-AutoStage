using System;
using System.Globalization;
using AutoStage.Core;
using Brutal.ImGuiApi;
using Brutal.Logging;
using Brutal.Numerics;
using HarmonyLib;
using KSA;

namespace AutoStage;

/// <summary>
/// Adds "Ignition Delay" settings to the pinned Part Window via DrawPartInfo postfix.
/// </summary>
[HarmonyPatch(typeof(Part), nameof(Part.DrawPartInfo))]
static class PartWindowPatch
{
    static void Postfix(Part __instance)
    {
        try
        {
            DrawIgnitionDelay(__instance);
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Error($"[AutoStage] PartWindow draw error: {ex.Message}");
        }
    }

    private static void DrawIgnitionDelay(Part part)
    {
        if (!Mod.IgnitionDelayAvailable)
            return;

        Span<EngineController> engines = part.SubtreeModules.Get<EngineController>();
        if (engines.Length == 0)
            return;

        int seqNumber = part.Sequence;
        if (seqNumber <= 0)
            return;

        Vehicle? vehicle = Program.ControlledVehicle;
        if (vehicle == null || part.Tree != vehicle.Parts)
            return;

        Config.LoadVehicleOverrides(vehicle.Id);

        bool hasOverride = Config.HasSequenceOverride(vehicle, seqNumber);
        double effectiveDelay = Config.GetSequenceDelay(vehicle, seqNumber);
        double engineDefault = Config.ComputeSequenceDelayFromEngines(vehicle, seqNumber);
        float delayValue = (float)effectiveDelay;

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.PushID("AutoStageIgnDelay");

        string engineName = part.Template.DisplayName;
        string headerText = string.Format(CultureInfo.InvariantCulture,
            "Ignition Delay - {0} (Seq {1})", engineName, seqNumber);
        ImGui.Text(headerText);

        ImGui.Spacing();

        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputFloat("###val"u8, ref delayValue, 0.1f, 1.0f, "%.1f"))
        {
            Config.SetSequenceOverride(vehicle, seqNumber, delayValue);
        }
        if (ImGui.IsItemDeactivatedAfterEdit())
            Config.FlushPendingSaves();
        ImGui.SameLine();
        ImGui.TextDisabled("seconds"u8);

        if (hasOverride)
        {
            string sourceText = string.Format(CultureInfo.InvariantCulture,
                "override (default: {0:F1} s)", engineDefault);
            ImGui.TextColored(new float4(1f, 0.8f, 0.2f, 1f), sourceText);

            if (ImGui.SmallButton("Reset to default"u8))
            {
                Config.ClearSequenceOverride(vehicle, seqNumber);
                Config.FlushPendingSaves();
            }
        }
        else
        {
            string sourceText = string.Format(CultureInfo.InvariantCulture,
                "engine default ({0:F1} s)", engineDefault);
            ImGui.TextDisabled(sourceText);
        }

        ImGui.PopID();
    }
}
