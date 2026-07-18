using AutoStage.Core;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace AutoStage.HarnessTests;

// Verifies the delayed split staging: a decoupler-only sequence must fire its decoupler
// DecouplerDelayS after the sequence activates, and the following engine sequence must ignite
// EngineDelayS after ITS activation, with the burn resuming afterwards. The delays are injected
// into the in-memory global config (per part variant); CleanupAfterFlight reloads the user's file.
//
// Needs a save shaped like: engine sequence, then a decoupler-only sequence, then another engine
// sequence (the default "Test Vehicle 1" layout); anything else skips. The vehicle comes from
// KSA_HEADLESS_VEHICLE, shared with the harness flight test.
public sealed class DelayTest : IHarnessTest
{
    private const double DecouplerDelayS = 3.0;
    private const double EngineDelayS = 5.0;
    // The countdown ticks once per solver step and the fired activation lands via the input queue
    // on the following step, so the observed delay runs up to about one coarse step late.
    private const double DelayTolS = 1.5;
    private const double BurnDt = 1.0;
    private const double MeasureDt = 0.5;
    private const double MaxBurnSeconds = 900.0;
    private const double MaxPhaseSeconds = 30.0;

    public string Name => "autostage-delays";

    public int Run(HeadlessSession session)
    {
        string? saveId = Environment.GetEnvironmentVariable(TestSupport.VehicleEnvVar);
        if (string.IsNullOrEmpty(saveId))
        {
            HarnessLog.Line($"[autostage-delays] SKIP: {TestSupport.VehicleEnvVar} not set.");
            return 0;
        }

        AutoStageHost.EnsureInitialized();
        if (!AutoStageHost.CoreOk || !Mod.IgnitionDelayAvailable)
        {
            HarnessLog.Line("[autostage-delays] FAIL: AutoStage delay patches are not active (see autostage-api-drift).");
            return 1;
        }

        CelestialSystem system = session.System;
        HashSet<string> preexisting = TestSupport.CollectVehicleIds(system);
        Vehicle vehicle;
        try
        {
            vehicle = AutoStageHost.SpawnFromSave(session, saveId, "AutoStageDelayTest", out _);
        }
        catch (InvalidOperationException e)
        {
            HarnessLog.Line($"[autostage-delays] FAIL: {e.Message}");
            return 1;
        }

        SimDriver driver = session.CreateDriver();
        double t = 0.0;
        bool ok = true;
        try
        {
            VehicleUpdateTask._forceOffRails = true;
            Program.ControlledVehicle = vehicle;

            if (!TryFindDelaySequences(vehicle, out int decouplerSeq, out int engineSeq))
            {
                HarnessLog.Line("[autostage-delays] SKIP: the save has no decoupler-only sequence " +
                                "followed by an engine sequence.");
                return 0;
            }
            ConfigureDelays(vehicle, decouplerSeq, engineSeq);
            HarnessLog.Line($"[autostage-delays] decoupler sequence {decouplerSeq} delayed {DecouplerDelayS:F1}s, " +
                            $"engine sequence {engineSeq} delayed {EngineDelayS:F1}s.");

            vehicle.ToggleEnum(AutoStageToggle.Enabled);
            if (!Mod.AutoStageEnabled)
            {
                HarnessLog.Line("[autostage-delays] FAIL: ToggleEnum(AutoStageToggle) did not enable the mod.");
                return 1;
            }
            AutoStageHost.HoldProgradeFullThrottle(vehicle);
            AutoStageHost.IgniteFirstStage(vehicle, driver);

            bool StepUntil(Func<bool> condition, double dt, double capSeconds, string what)
            {
                double deadline = t + capSeconds;
                while (!condition())
                {
                    if (t >= deadline)
                    {
                        HarnessLog.Line($"[autostage-delays] FAIL: timed out after {capSeconds:F0}s waiting for {what}.");
                        return false;
                    }
                    driver.Step(dt);
                    t += dt;
                }
                return true;
            }

            int vehiclesBefore = TestSupport.CountVehicles(system);

            if (!StepUntil(() => !StagingHelpers.HasActiveEngineWithPropellant(vehicle),
                    BurnDt, MaxBurnSeconds, "first-stage burnout"))
                return 1;
            if (!StepUntil(() => AutoStageHost.IsSequenceActivated(vehicle, decouplerSeq),
                    MeasureDt, MaxPhaseSeconds, $"sequence {decouplerSeq} activation"))
                return 1;
            double tDecouplerSeq = t;

            if (!StepUntil(() => TestSupport.CountVehicles(system) > vehiclesBefore,
                    MeasureDt, MaxPhaseSeconds, "the decoupler split"))
                return 1;
            double splitDelay = t - tDecouplerSeq;

            if (!StepUntil(() => AutoStageHost.IsSequenceActivated(vehicle, engineSeq),
                    MeasureDt, MaxPhaseSeconds, $"sequence {engineSeq} activation"))
                return 1;
            double tEngineSeq = t;

            if (!StepUntil(() => StagingHelpers.HasActiveEngineWithPropellant(vehicle),
                    MeasureDt, MaxPhaseSeconds, "upper-stage ignition"))
                return 1;
            double igniteDelay = t - tEngineSeq;

            bool splitOk = Math.Abs(splitDelay - DecouplerDelayS) <= DelayTolS;
            bool igniteOk = Math.Abs(igniteDelay - EngineDelayS) <= DelayTolS;
            HarnessLog.Line($"[autostage-delays] decoupler fired {splitDelay:F1}s after sequence activation " +
                            $"(configured {DecouplerDelayS:F1}s, tol {DelayTolS:F1}s) => {(splitOk ? "ok" : "FAIL")}");
            HarnessLog.Line($"[autostage-delays] engine ignited {igniteDelay:F1}s after sequence activation " +
                            $"(configured {EngineDelayS:F1}s, tol {DelayTolS:F1}s) => {(igniteOk ? "ok" : "FAIL")}");
            ok = splitOk && igniteOk;
        }
        finally
        {
            AutoStageHost.CleanupAfterFlight(system, preexisting);
        }

        HarnessLog.Line($"[autostage-delays] {TestSupport.Verdict(ok)}");
        return ok ? 0 : 1;
    }

    // The measurement needs a stage layout of: engines (the launch stage), a decoupler-only
    // sequence, then an engine sequence, so each delay is observable in isolation.
    private static bool TryFindDelaySequences(Vehicle vehicle, out int decouplerSeq, out int engineSeq)
    {
        decouplerSeq = -1;
        engineSeq = -1;
        bool sawLaunchEngines = false;
        foreach (Sequence seq in vehicle.Parts.SequenceList.Sequences)
        {
            if (seq.Activated || seq.Parts.IsEmpty)
                continue;
            bool hasEngine = false;
            bool hasDecoupler = false;
            ReadOnlySpan<Part> parts = seq.Parts;
            for (int i = 0; i < parts.Length; i++)
            {
                hasEngine |= parts[i].HasAny<EngineController>();
                hasDecoupler |= parts[i].HasAny<Decoupler>();
            }

            if (!sawLaunchEngines)
            {
                sawLaunchEngines = hasEngine;
            }
            else if (decouplerSeq < 0)
            {
                if (hasDecoupler && !hasEngine)
                    decouplerSeq = seq.Number;
            }
            else if (hasEngine)
            {
                engineSeq = seq.Number;
                return true;
            }
        }
        return false;
    }

    private static void ConfigureDelays(Vehicle vehicle, int decouplerSeq, int engineSeq)
    {
        Config.EngineDelays.Clear();
        Config.DecouplerDelays.Clear();
        foreach (Sequence seq in vehicle.Parts.SequenceList.Sequences)
        {
            ReadOnlySpan<Part> parts = seq.Parts;
            for (int i = 0; i < parts.Length; i++)
            {
                if (seq.Number == decouplerSeq && parts[i].HasAny<Decoupler>())
                    Config.DecouplerDelays[parts[i].Template.Id] = DecouplerDelayS;
                if (seq.Number == engineSeq && parts[i].HasAny<EngineController>())
                    Config.EngineDelays[parts[i].Template.Id] = EngineDelayS;
            }
        }
    }
}
