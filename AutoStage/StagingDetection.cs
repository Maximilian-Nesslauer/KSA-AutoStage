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
///   Monitoring -> active engines lose propellant -> stage
///     AwaitingIgnition:   one or both of (decoupler delay, engine delay) still running.
///                         Tick both timers independently, fire parts when they hit 0.
///     AwaitingPropagation: all pending parts fired; wait for worker to reflect
///                          the new propellant state. If it stays dry, cascade stage.
///   AwaitingPropagation -> new engines get propellant -> back to Monitoring
///   AwaitingPropagation -> still dry after propagation delay -> cascade stage
///
/// AwaitingPropagation is needed because IsPropellantAvailable on the new
/// engines is computed by the worker thread and only flips one tick after
/// activation. During that window we force BurnMode=Auto to stop the worker
/// from aborting the burn.
/// </summary>
static class StagingDetectionPatch
{
    enum State { Monitoring, AwaitingIgnition, AwaitingPropagation }

    private static State _state = State.Monitoring;
    private static int _propagationFrames;
    private static FlightComputerBurnMode _triggeredMode;
    private static PendingStaging? _pendingStaging;

    // 1 frame for worker thread to process new engines, +1 margin.
    private const int PropagationDelay = 2;

    internal static void Prefix(Vehicle __instance, SimStep simStep,
        out (FlightComputerBurnMode mode, bool hadPropellant, double deltaTime) __state)
    {
        if (__instance != Program.ControlledVehicle || !Mod.AutoStageEnabled)
        {
            __state = default;
            return;
        }
        __state = (
            __instance.FlightComputer.BurnMode,
            StagingHelpers.HasActiveEngineWithPropellant(__instance),
            simStep.DeltaTime
        );
    }

    internal static void Postfix(Vehicle __instance,
        (FlightComputerBurnMode mode, bool hadPropellant, double deltaTime) __state)
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

            case State.AwaitingIgnition:
                MaintainBurnMode(fc);
                TickPendingStaging(__instance, __state.deltaTime);
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

    private static void TickPendingStaging(Vehicle vehicle, double deltaTime)
    {
        PendingStaging? p = _pendingStaging;
        if (p == null)
        {
            // Should not happen, but recover
            _state = State.Monitoring;
            return;
        }

        if (p.DecouplersPending)
        {
            p.DecouplerRemaining -= deltaTime;
            if (p.DecouplerRemaining <= 0.0)
            {
                StagingExecution.ActivatePendingParts(vehicle, p.DecouplerParts!, "decoupler");
                p.ClearDecouplers();
            }
        }

        if (p.EnginesPending)
        {
            p.EngineRemaining -= deltaTime;
            if (p.EngineRemaining <= 0.0)
            {
                StagingExecution.ActivatePendingParts(vehicle, p.EngineParts!, "engine");
                p.ClearEngines();
            }
        }

        if (!p.AnyPending)
        {
            _pendingStaging = null;
            _state = State.AwaitingPropagation;
            _propagationFrames = 0;
        }
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

        _triggeredMode = originalBurnMode;

        PendingStaging? pending = StagingExecution.ActivateNextSequenceSplit(vehicle);

        if (originalBurnMode == FlightComputerBurnMode.Auto && fc.Burn != null)
            fc.BurnMode = FlightComputerBurnMode.Auto;

        if (pending != null)
        {
            _pendingStaging = pending;
            _state = State.AwaitingIgnition;

            if (pending.DecouplersPending && pending.DecouplerRemaining > 0.0)
                TimedAlert.Create(
                    $"Decouple in {pending.DecouplerRemaining:F1}s",
                    Color.Yellow, pending.DecouplerRemaining);
            if (pending.EnginesPending && pending.EngineRemaining > 0.0)
                TimedAlert.Create(
                    $"Ignition in {pending.EngineRemaining:F1}s",
                    Color.Yellow, pending.EngineRemaining);

            DefaultCategory.Log.Info(
                $"[AutoStage] Staging delay: decouplers={pending.DecouplerRemaining:F1}s " +
                $"({pending.DecouplerParts?.Count ?? 0}), " +
                $"engines={pending.EngineRemaining:F1}s " +
                $"({pending.EngineParts?.Count ?? 0})");
        }
        else
        {
            _state = State.AwaitingPropagation;
            _propagationFrames = 0;
        }
    }

    /// <summary>
    /// Keep the burn on auto while the worker thread thinks we're out of
    /// propellant. FlightComputer.UpdateBurnTarget forces BurnMode=Manual
    /// whenever HasAnyPropellant is false on the new engines.
    ///
    /// The override runs in the main-thread postfix, right after
    /// UpdateFromTaskResults installed the worker's new FlightComputer.
    /// The next worker task copies this FC, so its next ComputeControl
    /// starts from Auto again. Manual wins within a single worker pass
    /// (where HasAnyPropellant is false), Auto wins at the transition.
    /// </summary>
    private static void MaintainBurnMode(FlightComputer fc)
    {
        if (_triggeredMode == FlightComputerBurnMode.Auto
            && fc.Burn != null
            && !IsBurnComplete(fc)
            && fc.BurnMode == FlightComputerBurnMode.Manual)
        {
            fc.BurnMode = FlightComputerBurnMode.Auto;
        }
    }

    // Burn complete = remaining dV reversed direction (dot <= 0). Don't stage after completion.
    private static bool IsBurnComplete(FlightComputer fc)
        => fc.Burn != null
           && float3.Dot(fc.Burn.DeltaVToGoCci, fc.Burn.DeltaVTargetCci) <= 0f;

    internal static void Reset()
    {
        _state = State.Monitoring;
        _propagationFrames = 0;
        _triggeredMode = FlightComputerBurnMode.Manual;
        _pendingStaging = null;
    }
}
