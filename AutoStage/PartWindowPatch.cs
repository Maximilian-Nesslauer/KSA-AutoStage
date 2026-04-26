using System;
using System.Globalization;
using AutoStage.Core;
using Brutal.ImGuiApi;
using Brutal.Numerics;
using HarmonyLib;
using KSA;

namespace AutoStage;

/// <summary>
/// Adds "Ignition Delay" and "Decoupler Delay" settings to the pinned Part
/// Window via DrawPartInfo postfix. Each section only shows if the part
/// actually has the corresponding module.
/// </summary>
[HarmonyPatch(typeof(Part), nameof(Part.DrawPartInfo))]
static class PartWindowPatch
{
    static void Postfix(Part __instance)
    {
        try
        {
            DrawDelays(__instance);
        }
        catch (Exception ex)
        {
            LogHelper.ErrorOnce("PartWindow.Draw",
                $"[AutoStage] PartWindow draw error: {ex.Message}");
        }
    }

    private static void DrawDelays(Part part)
    {
        if (!Mod.IgnitionDelayAvailable)
            return;

        bool hasEngine = part.SubtreeModules.Get<EngineController>().Length > 0;
        bool hasDecoupler = part.SubtreeModules.Get<Decoupler>().Length > 0;
        if (!hasEngine && !hasDecoupler)
            return;

        int seqNumber = part.Sequence;
        if (seqNumber <= 0)
            return;

        Vehicle? vehicle = Program.ControlledVehicle;
        if (vehicle == null || part.Tree != vehicle.Parts)
            return;

        Config.LoadVehicleOverrides(vehicle.Id);

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        string partName = part.Template.DisplayName;

        if (hasEngine)
        {
            DrawDelayBlock(
                idScope: "AutoStageIgnDelay",
                header: string.Format(CultureInfo.InvariantCulture,
                    "Ignition Delay - {0} (Seq {1})", partName, seqNumber),
                effectiveDelay: Config.GetSequenceEngineDelay(vehicle, seqNumber),
                partDefault: Config.ComputeSequenceEngineDelay(vehicle, seqNumber),
                hasOverride: Config.HasSequenceEngineOverride(vehicle, seqNumber),
                setOverride: v => Config.SetSequenceEngineOverride(vehicle, seqNumber, v),
                clearOverride: () => Config.ClearSequenceEngineOverride(vehicle, seqNumber));
        }

        if (hasDecoupler)
        {
            DrawDelayBlock(
                idScope: "AutoStageDecDelay",
                header: string.Format(CultureInfo.InvariantCulture,
                    "Decoupler Delay - {0} (Seq {1})", partName, seqNumber),
                effectiveDelay: Config.GetSequenceDecouplerDelay(vehicle, seqNumber),
                partDefault: Config.ComputeSequenceDecouplerDelay(vehicle, seqNumber),
                hasOverride: Config.HasSequenceDecouplerOverride(vehicle, seqNumber),
                setOverride: v => Config.SetSequenceDecouplerOverride(vehicle, seqNumber, v),
                clearOverride: () => Config.ClearSequenceDecouplerOverride(vehicle, seqNumber));
        }
    }

    private static void DrawDelayBlock(
        string idScope,
        string header,
        double effectiveDelay,
        double partDefault,
        bool hasOverride,
        Action<double> setOverride,
        Action clearOverride)
    {
        ImGui.PushID(idScope);

        ImGui.Text(header);
        ImGui.Spacing();

        float delayValue = (float)effectiveDelay;
        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputFloat("###val"u8, ref delayValue, 0.1f, 1.0f, "%.1f"))
            setOverride(delayValue);
        if (ImGui.IsItemDeactivatedAfterEdit())
            Config.FlushPendingSaves();
        ImGui.SameLine();
        ImGui.TextDisabled("seconds"u8);

        if (hasOverride)
        {
            string source = string.Format(CultureInfo.InvariantCulture,
                "override (default: {0:F1} s)", partDefault);
            ImGui.TextColored(new float4(1f, 0.8f, 0.2f, 1f), source);

            if (ImGui.SmallButton("Reset to default"u8))
            {
                clearOverride();
                Config.FlushPendingSaves();
            }
        }
        else
        {
            string source = string.Format(CultureInfo.InvariantCulture,
                "part default ({0:F1} s)", partDefault);
            ImGui.TextDisabled(source);
        }

        ImGui.Spacing();
        ImGui.PopID();
    }
}
