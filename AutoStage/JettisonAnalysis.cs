using System;
using System.Collections.Generic;
using AutoStage.Core;
using Brutal.Logging;
using KSA;

namespace AutoStage;

/// <summary>
/// Works out which parts the next sequence would throw overboard, without
/// activating it.
///
/// Vehicle.Split hands the child-side subtree of a decoupled connection to the
/// new vehicle and keeps the root side, so a decoupler sheds exactly the
/// subtree below the child-side part of its connector's connection. Mirroring
/// that rule lets the detector tell "this sequence only sheds burnt-out
/// hardware" apart from "this sequence drops an engine that is still firing".
///
/// Only the shape of the jettison is cached, because only the shape is fixed
/// between part-tree and sequence edits. Whether shedding it is safe depends
/// on engine and tank state that moves every frame, so those questions are
/// answered live by SurveyActiveEngines and CarriesOffUsablePropellant.
/// </summary>
static class JettisonAnalysis
{
    private static readonly HashSet<Part> _jettisonSet = new();

    private static Vehicle? _cachedVehicle;
    private static int _cachedGeneration = -1;
    private static int _cachedPartCount = -1;
    private static int _cachedSequenceNumber = -1;
    private static bool _cachedValid;

    /// <summary>
    /// The parts the next unactivated sequence would shed, or null when that
    /// sequence is not a pure jettison: it lights an engine, or decouples
    /// nothing this analysis can predict.
    ///
    /// The returned set is a buffer reused by the next rebuild, so read it
    /// within the frame rather than holding on to it.
    /// </summary>
    public static IReadOnlySet<Part>? GetPendingJettison(Vehicle vehicle)
    {
        int generation = StagingHelpers.SequenceGeneration;
        // Part count catches tree changes the sequence generation misses, such
        // as a decouple the player triggered from a part menu.
        int partCount = vehicle.Parts.Count;
        // Which sequence is pending is the analysis' actual subject, so it is
        // read rather than inferred from an invalidation hook. Patching every
        // path that can activate a sequence is a claim about the whole game;
        // this is a claim about one short loop over the sequence list.
        int sequenceNumber = vehicle.Parts.SequenceList.GetNextSequenceNumber();

        if (_cachedVehicle != vehicle
            || _cachedGeneration != generation
            || _cachedPartCount != partCount
            || _cachedSequenceNumber != sequenceNumber)
        {
            // Invalidate before rebuilding, so a throw mid-rebuild leaves a
            // half-built set marked unusable instead of armed.
            _cachedValid = false;
            _cachedValid = Rebuild(vehicle);
            _cachedVehicle = vehicle;
            _cachedGeneration = generation;
            _cachedPartCount = partCount;
            _cachedSequenceNumber = sequenceNumber;
        }

        return _cachedValid ? _jettisonSet : null;
    }

    private static bool Rebuild(Vehicle vehicle)
    {
        _jettisonSet.Clear();

        SequenceList seqList = vehicle.Parts.SequenceList;
        int number = seqList.GetNextSequenceNumber();
        if (number < 0)
            return Decline("nothing left to stage");

        Sequence? target = FindSequence(seqList, number);
        if (target == null)
            return Decline($"sequence {number} is not in the list");

        bool anyDecoupler = false;
        ReadOnlySpan<Part> parts = target.Parts;
        for (int i = 0; i < parts.Length; i++)
        {
            Part part = parts[i];
            // An engine in the sequence means activating it lights the next
            // stage. That is a staging decision, not shedding dead weight.
            if (part.HasAny<EngineController>())
                return Decline($"sequence {number} lights an engine");

            Span<Decoupler> decouplers = part.Modules.Get<Decoupler>();
            for (int d = 0; d < decouplers.Length; d++)
            {
                switch (GetJettisonedRoot(decouplers[d], out Part? root))
                {
                    case Separation.Predicted:
                        anyDecoupler = true;
                        AddSubtree(root!);
                        break;
                    case Separation.None:
                        break;
                    case Separation.Unpredictable:
                        // One ambiguous decoupler makes the whole verdict wrong,
                        // because the set would be missing whatever it takes.
                        return Decline($"sequence {number} has a decoupler whose "
                                       + "separation cannot be predicted");
                }
            }
        }

        if (!anyDecoupler)
            return Decline($"sequence {number} separates nothing");

        if (DebugConfig.AutoStage)
        {
            int engines = 0;
            foreach (Part part in _jettisonSet)
                engines += part.Modules.Get<EngineController>().Length;
            DefaultCategory.Log.Debug(
                $"[AutoStage] Spent-stage jettison armed: sequence {number} would shed "
                + $"{_jettisonSet.Count} part(s) carrying {engines} engine(s).");
        }

        return true;
    }

    private static Sequence? FindSequence(SequenceList seqList, int number)
    {
        foreach (Sequence sequence in seqList.Sequences)
        {
            if (sequence.Number == number) return sequence;
        }
        return null;
    }

    // Declining is the normal case, so it is logged only under the debug flag,
    // where "the boosters rode along" would otherwise look identical to a
    // correct refusal.
    private static bool Decline(string reason)
    {
        if (DebugConfig.AutoStage)
            DefaultCategory.Log.Debug($"[AutoStage] No spent-stage jettison: {reason}.");
        return false;
    }

    /// <summary>
    /// True when the jettison would take propellant an engine staying with the
    /// vehicle can still draw from, which is the crossfeed case: a booster
    /// whose own engine is spent may still be feeding the core.
    ///
    /// Live, not cached: tank contents and fuel-link state move within a stage.
    /// The caller runs it as the last gate before staging, so the scan happens
    /// a few times per stage rather than every frame.
    ///
    /// Reports <paramref name="reason"/> instead of logging, so the caller can
    /// log it once per transition rather than on every evaluation.
    /// </summary>
    public static bool CarriesOffUsablePropellant(Vehicle vehicle, IReadOnlySet<Part> jettison,
        out string? reason)
    {
        reason = null;
        ReadOnlySpan<MoleState> moleStates = vehicle.Parts.Moles.States;

        foreach (Part part in jettison)
        {
            // SubtreeModules, not Modules: the set holds tree parts, and a tank
            // usually sits on one of their sub-parts.
            Span<Tank> tanks = part.SubtreeModules.Get<Tank>();
            for (int i = 0; i < tanks.Length; i++)
            {
                Tank tank = tanks[i];
                if (tank.ComputeSubstanceMass(moleStates) <= 0f) continue;

                // AvailableConsumers lists the consumers permitted to draw from
                // this tank under the staging filter: ResourceManager.CreateOrders
                // adds an engine only when PassesSameStageFilter accepts the tank.
                // That is narrower than what an engine on a FurtherestToNearest
                // or NearestToFurtherest flow rule actually drains, so this gate
                // can miss a cross-stage feed. It cannot on stock parts, whose
                // decoupler joints carry no BulkFluid capability and so cannot
                // pass a fluid graph across a separation at all; a modded joint
                // that does would need the retained engines' own ConsumptionOrder
                // walked instead.
                foreach ((ResourceManager manager, int _) in tank.AvailableConsumers)
                {
                    if (manager.Consumer is not Combustor consumer) continue;
                    if (jettison.Contains(consumer.Parent.FullPart)) continue;

                    reason = $"a tank on '{part.DisplayName}' still feeds "
                             + $"'{consumer.Parent.FullPart.DisplayName}', which stays aboard";
                    return true;
                }
            }
        }

        // A player-drawn fuel link is plumbing the resource graph does not
        // necessarily model, so a link across the cut blocks on its own.
        ReadOnlySpan<FuelLink> links = vehicle.Parts.FuelLinks.Links;
        for (int i = 0; i < links.Length; i++)
        {
            FuelLink link = links[i];
            if (!link.Enabled) continue;
            if (jettison.Contains(link.PartA.FullPart)
                == jettison.Contains(link.PartB.FullPart)) continue;

            reason = $"an enabled fuel link joins '{link.PartA.FullPart.DisplayName}' and "
                     + $"'{link.PartB.FullPart.DisplayName}' across the separation";
            return true;
        }

        return false;
    }

    private enum Separation { Predicted, None, Unpredictable }

    private static Separation GetJettisonedRoot(Decoupler decoupler, out Part? root)
    {
        root = null;

        // Decoupler.IsActive never flips (InputEvents applies ActivateOp.Decouple
        // straight to Decoupler.Decouple), so a spent decoupler is recognised by
        // its connector having lost the connection, which is the same guard
        // Decoupler.SetIsActive uses before queueing a decouple. Firing it again
        // separates nothing.
        Part.Connection? connection = decoupler.Connector.Connection;
        if (connection == null)
            return Separation.None;

        Part near = decoupler.Connector.ConnectionPart;
        Part far = connection.OtherPart(near);

        // Vehicle.Split keeps whichever endpoint is not a tree child of the
        // other, which only names a side when exactly one of them is the tree
        // parent. On any other connection its pick depends on connector order,
        // so say so rather than guess at what would leave.
        bool nearIsParent = near.TreeChildren.Contains(far);
        bool farIsParent = far.TreeChildren.Contains(near);
        if (nearIsParent == farIsParent)
            return Separation.Unpredictable;

        root = nearIsParent ? far : near;
        return Separation.Predicted;
    }

    private static void AddSubtree(Part root)
    {
        _jettisonSet.Add(root);
        PartTreeChildrenIterator iterator = new PartTreeChildrenIterator(root);
        while (true)
        {
            Part? node = iterator.GetNextNode();
            if (node == null) break;
            _jettisonSet.Add(node);
        }
    }

    internal static void Reset()
    {
        _jettisonSet.Clear();
        _cachedVehicle = null;
        _cachedGeneration = -1;
        _cachedPartCount = -1;
        _cachedSequenceNumber = -1;
        _cachedValid = false;
    }
}
