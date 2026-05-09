using System;
using KSA;

namespace AutoStage;

static class StagingHelpers
{
    public static bool HasActiveEngineWithPropellant(Vehicle vehicle)
    {
        ReadOnlySpan<MoleState> moleStates = vehicle.Parts.Moles.States;
        Span<EngineController> engines = vehicle.Parts.Modules.Get<EngineController>();
        for (int i = 0; i < engines.Length; i++)
        {
            if (!engines[i].IsActive) continue;
            foreach (RocketCore core in engines[i].Cores)
                if (core.ResourceManager != null
                    && core.ResourceManager.ResourceAvailable(moleStates))
                    return true;
        }
        return false;
    }

    // HasNextEngineSequence is queried per frame by the gauge, but only
    // changes on sequence activation.
    private static Vehicle? _cachedVehicle;
    private static bool _cachedHasNextEngineSequence;
    private static int _cachedGeneration = -1;
    private static int _sequenceGeneration;

    public static void InvalidateSequenceCache() => _sequenceGeneration++;

    public static bool HasNextEngineSequence(Vehicle vehicle)
    {
        if (_cachedVehicle == vehicle && _cachedGeneration == _sequenceGeneration)
            return _cachedHasNextEngineSequence;

        _cachedVehicle = vehicle;
        _cachedGeneration = _sequenceGeneration;
        _cachedHasNextEngineSequence = ComputeHasNextEngineSequence(vehicle);
        return _cachedHasNextEngineSequence;
    }

    private static bool ComputeHasNextEngineSequence(Vehicle vehicle)
    {
        foreach (Sequence sequence in vehicle.Parts.SequenceList.Sequences)
        {
            if (sequence.Activated) continue;
            ReadOnlySpan<Part> parts = sequence.Parts;
            for (int i = 0; i < parts.Length; i++)
                if (parts[i].HasAny<EngineController>()) return true;
        }
        return false;
    }

    internal static void Reset()
    {
        _cachedVehicle = null;
        _cachedGeneration = -1;
        _sequenceGeneration = 0;
        _cachedHasNextEngineSequence = false;
    }
}
