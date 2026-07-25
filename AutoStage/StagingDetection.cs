using System.Collections.Generic;
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
/// State machine. Two triggers leave Monitoring, both into the same path:
///   Monitoring -> active engines lose propellant -> stage
///   Monitoring -> the next sequence would shed only spent engines -> stage
///
///   AwaitingIgnition:    one or both of (decoupler delay, engine delay) still running.
///                        Both deadlines are independent; fire each set when sim
///                        time reaches it.
///   AwaitingPropagation: all pending parts fired; wait for worker to reflect
///                        the new propellant state. If it stays dry, cascade stage.
///   AwaitingPropagation -> new engines get propellant -> back to Monitoring
///   AwaitingPropagation -> still dry after propagation delay -> cascade stage
///
/// The second Monitoring trigger is what drops burnt-out boosters while the
/// core stage keeps firing: the all-engines-dry edge never comes for a vehicle
/// whose stack still has thrust, so spent hardware would otherwise ride along
/// until the last engine quits.
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
    private static double _spentJettisonSince = double.NaN;
    // Last reason reported by ReportSpentJettison, so it only speaks on change.
    // A non-null sentinel, because null is the armed state: starting there would
    // suppress the first real arm line.
    private static string? _spentJettisonBlocker = "";
    private static FlightComputerBurnMode _triggeredMode;
    private static PendingStaging? _pendingStaging;
    private static Vehicle? _currentVehicle;

    // 1 frame for worker thread to process new engines, +1 margin.
    private const int PropagationFrames = 2;

    // Sim seconds the spent-jettison condition must hold before staging on it.
    // Unlike the all-dry trigger, which fires on a falling edge, this one is
    // level-triggered and would otherwise act on the first frame it reads true.
    //
    // Sim time rather than a frame count because VehicleUpdateTask only skips
    // the motion and physics passes at DeltaTime <= 0, not ApplyResultsToVehicles,
    // so this postfix keeps running over frozen state while the game is paused.
    // A frame counter would arm and fire there; Universe.GetElapsedSeconds does not move.
    private const double SpentJettisonDwellSeconds = 0.25;

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
        // Bare return for the other vehicles: this postfix runs once per vehicle
        // per frame, so touching the detector's state here would reset it before
        // the controlled vehicle's own timers could ever elapse.
        if (__instance != Program.ControlledVehicle)
            return;

        if (!Mod.AutoStageEnabled)
        {
            // A staging already committed keeps running to completion. Its
            // sequence is marked activated and its parts are only waiting on a
            // delay, so abandoning it here would leave them unfired for good,
            // and switching the mod off means "stop deciding to stage", not
            // "stop mid-separation".
            if (_state == State.AwaitingIgnition)
            {
                TickPendingStaging(__instance, Universe.GetElapsedSeconds());
                return;
            }

            // Everything else is dropped rather than frozen: sim time keeps
            // running while the mod is off, so a dwell left armed here would
            // already be expired on the frame it is switched back on.
            _state = State.Monitoring;
            _propagationFrames = 0;
            _spentJettisonSince = double.NaN;
            _spentJettisonBlocker = "";
            return;
        }

        if (_currentVehicle != __instance)
        {
            _state = State.Monitoring;
            _propagationFrames = 0;
            _spentJettisonSince = double.NaN;
            _spentJettisonBlocker = "";
            _pendingStaging = null;
            _currentVehicle = __instance;
        }

        FlightComputer fc = __instance.FlightComputer;
        IReadOnlySet<Part>? jettison = _state == State.Monitoring && Config.DropSpentStages
            ? JettisonAnalysis.GetPendingJettison(__instance)
            : null;

        // Only the spent-jettison trigger needs the full tally. Without a
        // pending jettison, stay on the early-exiting scan: for a quenched
        // solid motor the per-core answer runs a fixed-point pressure solve.
        StagingHelpers.EngineSurvey survey = default;
        bool hasPropellant;
        if (jettison != null)
        {
            survey = StagingHelpers.SurveyActiveEngines(__instance, jettison);
            hasPropellant = survey.AnyFueled;
        }
        else
        {
            hasPropellant = StagingHelpers.HasActiveEngineWithPropellant(__instance);
        }

        switch (_state)
        {
            case State.Monitoring:
                if (__state.hadPropellant && !hasPropellant
                    && !IsBurnComplete(fc)
                    && StagingHelpers.HasNextEngineSequence(__instance))
                {
                    _spentJettisonSince = double.NaN;
                    ExecuteStaging(__instance, fc, __state.mode);
                }
                else if (Config.DropSpentStages)
                {
                    TickSpentJettison(__instance, fc, __state.mode, in survey, jettison);
                }
                else
                {
                    // Switched off: say nothing rather than report it as the
                    // analysis having declined, which is a different answer.
                    _spentJettisonSince = double.NaN;
                }
                break;

            case State.AwaitingIgnition:
                MaintainBurnMode(fc);
                TickPendingStaging(__instance, Universe.GetElapsedSeconds());
                break;

            case State.AwaitingPropagation:
                _propagationFrames++;
                MaintainBurnMode(fc);

                if (hasPropellant)
                {
                    _state = State.Monitoring;
                }
                else if (_propagationFrames >= PropagationFrames)
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

    /// <summary>
    /// Stages while the vehicle is still under thrust, when the next sequence
    /// would shed nothing but burnt-out engines. Requires thrust to remain
    /// afterwards, so a stack that is simply running out falls to the all-dry
    /// trigger instead and keeps its cascade behaviour.
    /// </summary>
    private static void TickSpentJettison(Vehicle vehicle, FlightComputer fc,
        FlightComputerBurnMode mode, in StagingHelpers.EngineSurvey survey,
        IReadOnlySet<Part>? jettison)
    {
        // SpentInside is only ever counted for parts inside a pending jettison,
        // so a non-zero count already implies there is one.
        string? blocker =
            jettison == null ? "no pending jettison to judge"
            : survey.SpentInside == 0 ? "nothing spent among the parts that would be shed"
            : survey.InactiveInside > 0 ? $"{survey.InactiveInside} engine(s) to be shed have not run"
            : survey.FueledInside > 0 ? $"{survey.FueledInside} engine(s) to be shed still have propellant"
            : survey.BrokenInside > 0 ? $"{survey.BrokenInside} engine(s) to be shed have an unresolved motor stack"
            : !survey.ThrustingOutside ? "nothing staying aboard is thrusting"
            : IsBurnComplete(fc) ? "the planned burn is already complete"
            : null;

        if (blocker != null)
        {
            ReportSpentJettison(vehicle, blocker, in survey, measured: jettison != null);
            _spentJettisonSince = double.NaN;
            return;
        }

        double now = Universe.GetElapsedSeconds();
        if (double.IsNaN(_spentJettisonSince))
        {
            ReportSpentJettison(vehicle, null, in survey, measured: true);
            _spentJettisonSince = now;
            return;
        }
        if (now - _spentJettisonSince < SpentJettisonDwellSeconds)
            return;

        // Last gate, deliberately after the dwell: it walks the jettisoned
        // parts' tanks, and running it here costs a scan a few times per stage
        // instead of one every frame the boosters read spent.
        _spentJettisonSince = double.NaN;
        if (JettisonAnalysis.CarriesOffUsablePropellant(vehicle, jettison!, out string? carried))
        {
            ReportSpentJettison(vehicle, carried, in survey, measured: true);
            return;
        }

        if (DebugConfig.AutoStage)
        {
            DefaultCategory.Log.Debug(
                $"[AutoStage] Shedding spent stage on '{vehicle.Id}': {survey.SpentInside} " +
                $"spent engine(s) jettisoned, {survey.FueledOutside} fueled engine(s) staying.");
            // Named once per drop, so a wrong jettison can be read straight off
            // the log instead of reconstructed from counters.
            foreach (Part part in jettison!)
            {
                Span<EngineController> engines = part.Modules.Get<EngineController>();
                for (int i = 0; i < engines.Length; i++)
                    DefaultCategory.Log.Debug(
                        $"[AutoStage]   shedding engine on '{part.DisplayName}' "
                        + $"(active={engines[i].IsActive}).");
            }
        }

        ExecuteStaging(vehicle, fc, mode);
    }

    /// <summary>
    /// Logs why the spent-stage drop is or is not armed, once per change.
    /// The condition is evaluated every frame, so logging each evaluation would
    /// bury the flight; logging none of them makes "the boosters rode along"
    /// indistinguishable from "the mod correctly refused".
    ///
    /// <paramref name="measured"/> is false when there was no pending jettison
    /// and the survey was skipped: its counters are then all zero by default,
    /// and printing them would read as "nothing is running" rather than
    /// "nothing was counted".
    /// </summary>
    private static void ReportSpentJettison(Vehicle vehicle, string? blocker,
        in StagingHelpers.EngineSurvey survey, bool measured)
    {
        if (!DebugConfig.AutoStage || blocker == _spentJettisonBlocker)
            return;
        _spentJettisonBlocker = blocker;

        // Only read when a line is actually emitted, so the scan stays off the
        // per-frame path.
        int next = vehicle.Parts.SequenceList.GetNextSequenceNumber();
        string detail = measured
            ? $"next sequence {next}, inside: {survey.SpentInside} spent / "
              + $"{survey.FueledInside} fueled / {survey.BrokenInside} broken / "
              + $"{survey.InactiveInside} not run, "
              + $"outside: {survey.FueledOutside} fueled, thrusting={survey.ThrustingOutside}"
            : $"next sequence {next}, engines not surveyed";

        DefaultCategory.Log.Debug(blocker == null
            ? $"[AutoStage] Spent-stage drop armed on '{vehicle.Id}' ({detail})."
            : $"[AutoStage] Spent-stage drop held on '{vehicle.Id}': {blocker} ({detail}).");
    }

    private static void TickPendingStaging(Vehicle vehicle, double now)
    {
        PendingStaging? p = _pendingStaging;
        if (p == null)
        {
            LogHelper.ErrorOnce("StagingDetection.AwaitingIgnition.NoPending",
                "[AutoStage] AwaitingIgnition with no pending staging, "
                + "recovering to Monitoring. Please report.");
            _state = State.Monitoring;
            return;
        }

        if (p.DecouplersPending && now >= p.DecouplerDeadline)
        {
            StagingExecution.ActivatePendingParts(vehicle, p.DecouplerParts!, "decoupler");
            p.ClearDecouplers();
        }

        if (p.EnginesPending && now >= p.EngineDeadline)
        {
            StagingExecution.ActivatePendingParts(vehicle, p.EngineParts!, "engine");
            p.ClearEngines();
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

            if (pending.DecouplersPending && pending.DecouplerDelay > 0.0)
                TimedAlert.Create(
                    $"Decouple in {pending.DecouplerDelay:F1}s",
                    Color.Yellow, pending.DecouplerDelay);
            if (pending.EnginesPending && pending.EngineDelay > 0.0)
                TimedAlert.Create(
                    $"Ignition in {pending.EngineDelay:F1}s",
                    Color.Yellow, pending.EngineDelay);

            DefaultCategory.Log.Info(
                $"[AutoStage] Staging delay on '{vehicle.Id}': " +
                $"decouplers={pending.DecouplerDelay:F1}s " +
                $"({pending.DecouplerParts?.Count ?? 0}), " +
                $"engines={pending.EngineDelay:F1}s " +
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

    /// <summary>
    /// True once the planned burn has delivered its delta-V, i.e. what is left
    /// to go no longer points along the target.
    ///
    /// The zero-target guard is load-bearing. FlightComputer.Burn can outlive
    /// the BurnPlan entry it came from and is serialized into the save, so a
    /// vehicle can load with a BurnTarget whose DeltaVTargetCci is the zero
    /// vector (the stock burn indicator shows for it too). Dot(anything, zero)
    /// is zero, and zero passes a "<= 0" overshoot test, so without this check
    /// a stale target reads as a finished burn forever and both staging
    /// triggers stay disabled for the rest of the flight.
    /// </summary>
    private static bool IsBurnComplete(FlightComputer fc)
    {
        BurnTarget? burn = fc.Burn;
        if (burn == null || burn.DeltaVTargetCci.IsNearlyZero())
            return false;
        return float3.Dot(burn.DeltaVToGoCci, burn.DeltaVTargetCci) <= 0f;
    }

    internal static void Reset()
    {
        _state = State.Monitoring;
        _propagationFrames = 0;
        _spentJettisonSince = double.NaN;
        _spentJettisonBlocker = "";
        _triggeredMode = FlightComputerBurnMode.Manual;
        _pendingStaging = null;
        _currentVehicle = null;
        JettisonAnalysis.Reset();
    }
}
