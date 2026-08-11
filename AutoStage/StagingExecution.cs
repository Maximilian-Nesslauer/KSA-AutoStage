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
        // Up front so every return path invalidates.
        StagingHelpers.InvalidateSequenceCache();

        // Stock opens ActivateNextSequence with this, so the staging window
        // follows an auto-stage the same way it follows a manual one.
        SequenceList.RequestScrollToBottom();

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

        // Private setter, so reflection. Non-null because ValidateAll gates the
        // only patch that can reach this method.
        GameReflection.SequenceList_ActiveSequence!.SetValue(seqList, seqNumber);
        TimedAlert.Create($"Sequence {seqNumber} activated", Color.Yellow, 3.0);

        if (DebugConfig.AutoStage)
        {
            int engineParts = 0;
            int decouplerParts = 0;
            ReadOnlySpan<Part> members = target.Parts;
            for (int i = 0; i < members.Length; i++)
            {
                if (members[i].HasAny<EngineController>()) engineParts++;
                if (members[i].HasAny<Decoupler>()) decouplerParts++;
            }
            DefaultCategory.Log.Debug(
                $"[AutoStage] Activating sequence {seqNumber}: {members.Length} part(s), "
                + $"{engineParts} with an engine, {decouplerParts} with a decoupler.");
        }

        // Guard against re-entrant ResetCaches during part activation (same as stock)
        // Same guard stock ActivateNextSequence uses around its own activation
        // loop: SequenceList.ResetCaches early-returns while this is set, so a
        // re-entrant reset cannot rebuild Sequence._partsCache under the span
        // the loop below is iterating. Non-null because ValidateAll covers it.
        GameReflection.SequenceList_updatingSequence!.SetValue(seqList, true);
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

        GameReflection.SequenceList_updatingSequence!.SetValue(seqList, false);
        seqList.ResetCaches();
        // The scan above marks empty sequences activated on its way past them,
        // exactly the rows this prunes. Stock ends ActivateNextSequence with it
        // for the same reason.
        seqList.RemoveSpentSequences();
        StagingHelpers.InvalidateSequenceCache();

        // Must not drain IActivateInputBuffer from here: Decoupler.Decouple ->
        // Vehicle.Split -> Vehicle.AddToBubble mutates the vehicle list that
        // PhysicsBubble is iterating in the apply this runs inside. The stock
        // drain in Program.PrepareFrame runs a few ms later in the same frame,
        // once every bubble has been applied.

        // Kept defensively, not because the activation needs it: every IActivate
        // here only appends to InputEvents.IActivateInputBuffer, so the tree is
        // unchanged at this point and this recomputes the same mass, collision
        // and flight-computer config it already holds. Nothing here covers the
        // Auto-burn-through-staging path it was credited with, so dropping it
        // wants a deliberate test first.
        // Safe to call here because UpdateAfterPartTreeModification touches only
        // this vehicle's own derived state, never its PhysicsBubble.
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
            pendingEngines, engineDelay,
            Universe.GetElapsedSeconds());
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
        StagingHelpers.InvalidateSequenceCache();

        // Same derived-data refresh as ActivateNextSequenceSplit.
        vehicle.UpdateAfterPartTreeModification();
    }

    // Modules (not SubtreeModules) matches Part.ActivateInStage's scope.
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
/// Tracks parts waiting to fire after a staging delay. Decouplers and engines
/// have independent deadlines, both measured from the staging trigger.
///
/// Deadlines in sim time rather than a countdown fed by
/// Vehicle.KinematicMeasurements.DeltaTime: the detector runs on every frame,
/// including the ones the game spends paused over frozen state, and the last
/// non-zero DeltaTime would keep draining the countdown there.
/// </summary>
class PendingStaging
{
    public Vehicle Vehicle { get; }
    public List<Part>? DecouplerParts { get; private set; }
    public List<Part>? EngineParts { get; private set; }

    public double DecouplerDelay { get; }
    public double EngineDelay { get; }
    public double DecouplerDeadline { get; }
    public double EngineDeadline { get; }

    public bool DecouplersPending => DecouplerParts != null && DecouplerParts.Count > 0;
    public bool EnginesPending => EngineParts != null && EngineParts.Count > 0;
    public bool AnyPending => DecouplersPending || EnginesPending;

    public PendingStaging(Vehicle vehicle,
        List<Part>? decouplerParts, double decouplerDelay,
        List<Part>? engineParts, double engineDelay, double now)
    {
        Vehicle = vehicle;
        DecouplerParts = decouplerParts;
        EngineParts = engineParts;
        DecouplerDelay = decouplerDelay;
        EngineDelay = engineDelay;
        DecouplerDeadline = now + decouplerDelay;
        EngineDeadline = now + engineDelay;
    }

    public void ClearDecouplers() => DecouplerParts = null;
    public void ClearEngines() => EngineParts = null;
}
