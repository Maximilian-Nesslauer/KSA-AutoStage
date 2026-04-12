using System.Diagnostics;
using AutoStage.Core;
using Brutal.Logging;
using Brutal.Numerics;
using KSA;

namespace AutoStage;

/// <summary>
/// Hooks Vehicle.UpdateFromTaskResults (main thread, after worker results are applied)
/// to detect propellant depletion and trigger staging.
///
/// State machine:
///   Monitoring -> all active engines lose propellant -> stage, enter AwaitingPropagation
///   AwaitingPropagation -> new engines get propellant -> back to Monitoring
///   AwaitingPropagation -> still dry after propagation delay -> cascade stage
///
/// AwaitingPropagation is needed because newly activated engines have
/// IsPropellantAvailable=false until the worker thread processes them (1 frame).
/// During this time we also force BurnMode=Auto to prevent the worker from
/// aborting the burn.
/// </summary>
static class StagingDetectionPatch
{
    enum State { Monitoring, AwaitingPropagation }

    private static State _state = State.Monitoring;
    private static int _propagationFrames;
    private static FlightComputerBurnMode _triggeredMode;

    // 1 frame for worker thread to process new engines, +1 margin.
    private const int PropagationDelay = 2;

    internal static void Prefix(Vehicle __instance,
        out (FlightComputerBurnMode mode, bool hadPropellant) __state)
    {
        if (__instance != Program.ControlledVehicle || !Mod.AutoStageEnabled)
        {
            __state = default;
            return;
        }
        __state = (
            __instance.FlightComputer.BurnMode,
            StagingHelpers.HasActiveEngineWithPropellant(__instance)
        );
    }

    internal static void Postfix(Vehicle __instance,
        (FlightComputerBurnMode mode, bool hadPropellant) __state)
    {
#if DEBUG
        long perfStart = DebugConfig.Performance ? Stopwatch.GetTimestamp() : 0;
#endif
        if (__instance != Program.ControlledVehicle || !Mod.AutoStageEnabled)
            return;

        FlightComputer fc = __instance.FlightComputer;
        bool hasPropellant = StagingHelpers.HasActiveEngineWithPropellant(__instance);

        switch (_state)
        {
            case State.Monitoring:
                if (__state.hadPropellant && !hasPropellant
                    && !IsBurnComplete(fc)
                    && StagingHelpers.HasNextEngineSequence(__instance))
                {
                    ExecuteStaging(__instance, fc, __state.mode);
                }
                break;

            case State.AwaitingPropagation:
                _propagationFrames++;
                MaintainBurnMode(fc);

                if (hasPropellant)
                {
                    _state = State.Monitoring;
                }
                else if (_propagationFrames >= PropagationDelay)
                {
                    if (!IsBurnComplete(fc)
                        && StagingHelpers.HasNextEngineSequence(__instance))
                        ExecuteStaging(__instance, fc, _triggeredMode);
                    else
                        _state = State.Monitoring;
                }
                break;
        }

#if DEBUG
        if (DebugConfig.Performance)
            PerfTracker.Record("StagingDetection.Postfix",
                Stopwatch.GetTimestamp() - perfStart);
#endif
    }

    private static void ExecuteStaging(Vehicle vehicle, FlightComputer fc,
        FlightComputerBurnMode originalBurnMode)
    {
        if (DebugConfig.AutoStage)
        {
            string dvInfo = fc.Burn != null
                ? $"dV remaining = {fc.Burn.DeltaVToGoCci.Length():F1} m/s"
                : "no burn planned";
            DefaultCategory.Log.Debug(
                $"[AutoStage] Staging ({originalBurnMode} mode): {dvInfo}");
        }

        vehicle.Parts.SequenceList.ActivateNextSequence(vehicle);

        _triggeredMode = originalBurnMode;
        if (originalBurnMode == FlightComputerBurnMode.Auto && fc.Burn != null)
            fc.BurnMode = FlightComputerBurnMode.Auto;

        _state = State.AwaitingPropagation;
        _propagationFrames = 0;
    }

    /// <summary>
    /// The worker thread keeps setting BurnMode=Manual because it sees
    /// IsPropellantAvailable=false on the newly activated engines.
    /// Override it back to Auto so the burn continues.
    /// </summary>
    private static void MaintainBurnMode(FlightComputer fc)
    {
        if (_triggeredMode == FlightComputerBurnMode.Auto
            && fc.Burn != null
            && IsBurnIncomplete(fc)
            && fc.BurnMode == FlightComputerBurnMode.Manual)
        {
            fc.BurnMode = FlightComputerBurnMode.Auto;
        }
    }

    // Burn complete = remaining dV reversed direction (dot <= 0). Don't stage after completion.
    private static bool IsBurnComplete(FlightComputer fc)
        => fc.Burn != null
           && float3.Dot(fc.Burn.DeltaVToGoCci, fc.Burn.DeltaVTargetCci) <= 0f;

    private static bool IsBurnIncomplete(FlightComputer fc)
        => fc.Burn != null
           && float3.Dot(fc.Burn.DeltaVToGoCci, fc.Burn.DeltaVTargetCci) > 0f;

    internal static void Reset()
    {
        _state = State.Monitoring;
        _propagationFrames = 0;
        _triggeredMode = FlightComputerBurnMode.Manual;
    }
}
