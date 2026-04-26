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

    public static bool HasNextEngineSequence(Vehicle vehicle)
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
}
