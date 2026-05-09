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
    private enum DelayKind { Engine, Decoupler }

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

        // Modules (not SubtreeModules) so we only show the slider for parts
        // ActivateInStage will actually fire.
        bool hasEngine = part.Modules.Get<EngineController>().Length > 0;
        bool hasDecoupler = part.Modules.Get<Decoupler>().Length > 0;
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
            DrawDelayBlock(vehicle, seqNumber, DelayKind.Engine,
                idScope: "AutoStageIgnDelay",
                header: string.Format(CultureInfo.InvariantCulture,
                    "Ignition Delay - {0} (Seq {1})", partName, seqNumber));
        }

        if (hasDecoupler)
        {
            DrawDelayBlock(vehicle, seqNumber, DelayKind.Decoupler,
                idScope: "AutoStageDecDelay",
                header: string.Format(CultureInfo.InvariantCulture,
                    "Decoupler Delay - {0} (Seq {1})", partName, seqNumber));
        }
    }

    private static void DrawDelayBlock(Vehicle vehicle, int seqNumber, DelayKind kind,
        string idScope, string header)
    {
        double effectiveDelay = kind == DelayKind.Engine
            ? Config.GetSequenceEngineDelay(vehicle, seqNumber)
            : Config.GetSequenceDecouplerDelay(vehicle, seqNumber);
        double partDefault = kind == DelayKind.Engine
            ? Config.ComputeSequenceEngineDelay(vehicle, seqNumber)
            : Config.ComputeSequenceDecouplerDelay(vehicle, seqNumber);
        bool hasOverride = kind == DelayKind.Engine
            ? Config.HasSequenceEngineOverride(vehicle, seqNumber)
            : Config.HasSequenceDecouplerOverride(vehicle, seqNumber);

        ImGui.PushID(idScope);

        ImGui.Text(header);
        ImGui.Spacing();

        float delayValue = (float)effectiveDelay;
        ImGui.SetNextItemWidth(120f);
        if (ImGui.InputFloat("###val"u8, ref delayValue, 0.1f, 1.0f, "%.1f"))
        {
            if (kind == DelayKind.Engine)
                Config.SetSequenceEngineOverride(vehicle, seqNumber, delayValue);
            else
                Config.SetSequenceDecouplerOverride(vehicle, seqNumber, delayValue);
        }
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
                if (kind == DelayKind.Engine)
                    Config.ClearSequenceEngineOverride(vehicle, seqNumber);
                else
                    Config.ClearSequenceDecouplerOverride(vehicle, seqNumber);
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
