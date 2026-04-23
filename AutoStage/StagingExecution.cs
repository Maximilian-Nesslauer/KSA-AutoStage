using System;
using System.Collections.Generic;
using AutoStage.Core;
using Brutal.Logging;
using Brutal.Numerics;
using KSA;

namespace AutoStage;

/// <summary>
/// Replaces the stock ActivateNextSequence with a two-phase activation:
/// Phase 1 (immediate): Decouplers, fairings, and other non-engine parts activate.
/// Phase 2 (after delay): Engine parts activate.
///
/// If delay is 0, both phases execute in the same frame (stock behavior).
/// </summary>
static class StagingExecution
{
    /// <summary>
    /// Activates the next sequence with split timing. Returns the pending engine
    /// parts and computed delay, or null if everything was activated immediately.
    /// </summary>
    public static PendingIgnition? ActivateNextSequenceSplit(Vehicle vehicle)
    {
        SequenceList seqList = vehicle.Parts.SequenceList;
        ReadOnlySpan<Sequence> sequences = seqList.Sequences;

        // Find the next unactivated, non-empty sequence (same logic as stock)
        Sequence? target = null;
        for (int i = 0; i < sequences.Length; i++)
        {
            Sequence seq = sequences[i];
            if (seq.Activated) continue;

            if (seq.Parts.IsEmpty)
            {
                seq.Activated = true;
                continue;
            }

            target = seq;
            break;
        }

        if (target == null)
            return null;

        int seqNumber = target.Number;
        double delay = Config.GetSequenceDelay(vehicle, seqNumber);

        // Update ActiveSequence via reflection (private setter)
        if (GameReflection.SequenceList_ActiveSequence == null)
        {
            DefaultCategory.Log.Error(
                "[AutoStage] SequenceList.ActiveSequence reflection target lost, falling back to stock activation.");
            vehicle.Parts.SequenceList.ActivateNextSequence(vehicle);
            return null;
        }
        GameReflection.SequenceList_ActiveSequence.SetValue(seqList, seqNumber);
        TimedAlert.Create($"Sequence {seqNumber} activated", Color.Yellow, 3.0);

        // Guard against re-entrant ResetCaches during part activation (same as stock)
        GameReflection.SequenceList_updatingSequence?.SetValue(seqList, true);
        target.Activated = true;

        // Split parts into engines and non-engines
        ReadOnlySpan<Part> parts = target.Parts;
        List<Part>? pendingEngines = null;

        // Activate in reverse order (same as stock)
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            Part part = parts[i];
            if (delay > 0.0 && part.HasAny<EngineController>())
            {
                pendingEngines ??= new List<Part>();
                pendingEngines.Add(part);
            }
            else
            {
                part.ActivateInStage(vehicle);
            }
        }

        GameReflection.SequenceList_updatingSequence?.SetValue(seqList, false);
        seqList.ResetCaches();

        vehicle.UpdateAfterPartTreeModification();

        if (pendingEngines == null || pendingEngines.Count == 0)
            return null;

        if (DebugConfig.IgnitionDelay)
            DefaultCategory.Log.Debug(
                $"[AutoStage] Split activation: seq {seqNumber}, " +
                $"{pendingEngines.Count} engines pending, delay={delay:F1}s");

        return new PendingIgnition(vehicle, pendingEngines, delay);
    }

    /// <summary>
    /// Activates the pending engine parts (Phase 2). Called when the ignition timer expires.
    /// Validates that parts still belong to the expected vehicle (a decouple in Phase 1
    /// could have moved them to a new vehicle).
    /// </summary>
    public static void ActivatePendingEngines(PendingIgnition pending)
    {
        int activated = 0;
        foreach (Part part in pending.EngineParts)
        {
            if (part.Tree != pending.Vehicle.Parts)
            {
                DefaultCategory.Log.Warning(
                    $"[AutoStage] Engine part '{part.DisplayName}' no longer belongs to vehicle, skipping.");
                continue;
            }
            part.ActivateInStage(pending.Vehicle);
            activated++;
        }

        if (DebugConfig.IgnitionDelay)
            DefaultCategory.Log.Debug(
                $"[AutoStage] Ignited {activated}/{pending.EngineParts.Count} engines");

        pending.Vehicle.Parts.SequenceList.ResetCaches();

        pending.Vehicle.UpdateAfterPartTreeModification();
    }
}

/// <summary>
/// Tracks engine parts waiting for ignition after a staging delay.
/// </summary>
class PendingIgnition
{
    public Vehicle Vehicle { get; }
    public List<Part> EngineParts { get; }
    public double RemainingDelay { get; set; }

    public PendingIgnition(Vehicle vehicle, List<Part> engineParts, double delaySeconds)
    {
        Vehicle = vehicle;
        EngineParts = engineParts;
        RemainingDelay = delaySeconds;
    }
}
