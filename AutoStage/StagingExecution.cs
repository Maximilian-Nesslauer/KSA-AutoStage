using System;
using System.Collections.Generic;
using AutoStage.Core;
using Brutal.Logging;
using Brutal.Numerics;
using KSA;

namespace AutoStage;

/// <summary>
/// Replaces the stock ActivateNextSequence with a split activation that can
/// defer decouplers and engines independently.
///
/// Parts go into one of three buckets:
///   - activate now: non-engine non-decoupler parts, and parts whose only
///     engine/decoupler is already active (no work to do).
///   - pending decouplers: decoupler parts, if decouplerDelay > 0.
///   - pending engines: engine parts (with any inactive engine), if engineDelay > 0.
///
/// Decoupler and engine delays are both measured from the staging trigger,
/// so they run independently. If a part has both modules (atypical), the
/// engine delay wins.
/// </summary>
static class StagingExecution
{
    public static PendingStaging? ActivateNextSequenceSplit(Vehicle vehicle)
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
        double engineDelay = Config.GetSequenceEngineDelay(vehicle, seqNumber);
        double decouplerDelay = Config.GetSequenceDecouplerDelay(vehicle, seqNumber);

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

        ReadOnlySpan<Part> parts = target.Parts;
        List<Part>? pendingEngines = null;
        List<Part>? pendingDecouplers = null;

        // Activate in reverse order (same as stock)
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            Part part = parts[i];
            bool hasInactiveEngine = HasInactiveEngine(part);
            bool hasInactiveDecoupler = HasInactiveDecoupler(part);

            if (engineDelay > 0.0 && hasInactiveEngine)
            {
                pendingEngines ??= new List<Part>();
                pendingEngines.Add(part);
            }
            else if (decouplerDelay > 0.0 && hasInactiveDecoupler)
            {
                pendingDecouplers ??= new List<Part>();
                pendingDecouplers.Add(part);
            }
            else
            {
                part.ActivateInStage(vehicle);
            }
        }

        GameReflection.SequenceList_updatingSequence?.SetValue(seqList, false);
        seqList.ResetCaches();

        // Drain the IActivate buffer so IsActive flips and decouples are visible
        // now, not at end of frame. This lets the following refresh see the
        // correct VehicleConfig immediately.
        InputEvents.IActivateInputBuffer.ApplyAll();

        vehicle.UpdateAfterPartTreeModification();

        bool anyPending = (pendingEngines != null && pendingEngines.Count > 0)
                       || (pendingDecouplers != null && pendingDecouplers.Count > 0);
        if (!anyPending)
            return null;

        if (DebugConfig.IgnitionDelay)
        {
            int eng = pendingEngines?.Count ?? 0;
            int dec = pendingDecouplers?.Count ?? 0;
            DefaultCategory.Log.Debug(
                $"[AutoStage] Split activation: seq {seqNumber}, " +
                $"{dec} decouplers pending (delay={decouplerDelay:F1}s), " +
                $"{eng} engines pending (delay={engineDelay:F1}s)");
        }

        return new PendingStaging(vehicle,
            pendingDecouplers, decouplerDelay,
            pendingEngines, engineDelay);
    }

    /// <summary>
    /// Fires the given pending parts. Validates each part still belongs to
    /// the expected vehicle (an earlier decouple may have moved it).
    /// </summary>
    public static void ActivatePendingParts(Vehicle vehicle, List<Part> parts, string label)
    {
        int activated = 0;
        foreach (Part part in parts)
        {
            if (part.Tree != vehicle.Parts)
            {
                DefaultCategory.Log.Warning(
                    $"[AutoStage] {label} part '{part.DisplayName}' no longer belongs to vehicle, skipping.");
                continue;
            }
            part.ActivateInStage(vehicle);
            activated++;
        }

        if (DebugConfig.IgnitionDelay)
            DefaultCategory.Log.Debug(
                $"[AutoStage] Fired {activated}/{parts.Count} {label} parts");

        vehicle.Parts.SequenceList.ResetCaches();

        // Drain the IActivate buffer so IsActive flips and decouples are visible
        // now, not at end of frame. This lets the following refresh see the
        // correct VehicleConfig immediately.
        InputEvents.IActivateInputBuffer.ApplyAll();

        vehicle.UpdateAfterPartTreeModification();
    }

    private static bool HasInactiveEngine(Part part)
    {
        Span<EngineController> engines = part.Modules.Get<EngineController>();
        if (engines.Length == 0) return false;
        for (int i = 0; i < engines.Length; i++)
            if (!engines[i].IsActive) return true;
        return false;
    }

    private static bool HasInactiveDecoupler(Part part)
    {
        Span<Decoupler> decouplers = part.Modules.Get<Decoupler>();
        if (decouplers.Length == 0) return false;
        for (int i = 0; i < decouplers.Length; i++)
            if (!decouplers[i].IsActive) return true;
        return false;
    }
}

/// <summary>
/// Tracks parts waiting to fire after a staging delay. Decouplers and
/// engines have independent countdowns measured from the staging trigger.
/// </summary>
class PendingStaging
{
    public Vehicle Vehicle { get; }
    public List<Part>? DecouplerParts { get; private set; }
    public double DecouplerRemaining { get; set; }
    public List<Part>? EngineParts { get; private set; }
    public double EngineRemaining { get; set; }

    public bool DecouplersPending => DecouplerParts != null && DecouplerParts.Count > 0;
    public bool EnginesPending => EngineParts != null && EngineParts.Count > 0;
    public bool AnyPending => DecouplersPending || EnginesPending;

    public PendingStaging(Vehicle vehicle,
        List<Part>? decouplerParts, double decouplerDelay,
        List<Part>? engineParts, double engineDelay)
    {
        Vehicle = vehicle;
        DecouplerParts = decouplerParts;
        DecouplerRemaining = decouplerDelay;
        EngineParts = engineParts;
        EngineRemaining = engineDelay;
    }

    public void ClearDecouplers() => DecouplerParts = null;
    public void ClearEngines() => EngineParts = null;
}
