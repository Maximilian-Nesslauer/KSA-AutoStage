using System;
using KSA;

namespace AutoStage;

static class StagingHelpers
{
    public static bool HasActiveEngineWithPropellant(Vehicle vehicle)
    {
        ReadOnlySpan<MoleState> moleStates = vehicle.Parts.Moles.States;
        foreach (Stage stage in vehicle.Parts.StageList.Stages)
        {
            ReadOnlySpan<Part> parts = stage.Parts;
            for (int i = 0; i < parts.Length; i++)
            {
                Span<EngineController> engines = parts[i].Modules.Get<EngineController>();
                for (int j = 0; j < engines.Length; j++)
                {
                    if (!engines[j].IsActive) continue;
                    foreach (RocketCore core in engines[j].Cores)
                        if (core.ResourceManager != null
                            && core.ResourceManager.ResourceAvailable(moleStates))
                            return true;
                }
            }
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
