using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace AutoStage.HarnessTests;

// The end-to-end scenario: spawn a staged save into a circular orbit clear of the home body, light
// the first stage like a player, then hold full manual throttle and let AutoStage perform every
// further staging on its own (including cascading through decoupler-only sequences). Passes when
// the sequence list is spent and the vehicle ends dry without a single manual staging call, still
// on an orbit that does not intersect the home body.
//
// The vehicle comes from KSA_HEADLESS_VEHICLE (shared with the harness flight test); unset skips.
public sealed class AutoStagingFlightTest : IHarnessTest
{
    private const int MaxFlightSeconds = 3600; // same runaway guard as the harness's own flight test
    private const double MinPeriapsisClearanceM = 100_000.0; // periapsis height above the home body's mean radius
    // The last activation's engine ignition lands via the input queue a step later, so a single dry
    // sample right after an activation is not burnout yet. Require it to persist.
    private const int DrySteps = 3;

    public string Name => "autostage-flight";

    public int Run(HeadlessSession session)
    {
        string? saveId = Environment.GetEnvironmentVariable(TestSupport.VehicleEnvVar);
        if (string.IsNullOrEmpty(saveId))
        {
            HarnessLog.Line($"[autostage-flight] SKIP: {TestSupport.VehicleEnvVar} not set.");
            return 0;
        }

        AutoStageHost.EnsureInitialized();
        if (!AutoStageHost.CoreOk)
        {
            HarnessLog.Line("[autostage-flight] FAIL: AutoStage core patches are not active (see autostage-api-drift).");
            return 1;
        }

        CelestialSystem system = session.System;
        HashSet<string> preexisting = TestSupport.CollectVehicleIds(system);
        Vehicle vehicle;
        Astronomical homeBody;
        try
        {
            vehicle = AutoStageHost.SpawnFromSave(session, saveId, "AutoStageFlight", out homeBody);
        }
        catch (InvalidOperationException e)
        {
            HarnessLog.Line($"[autostage-flight] FAIL: {e.Message}");
            return 1;
        }

        SimDriver driver = session.CreateDriver();
        bool ok = true;
        try
        {
            VehicleUpdateTask._forceOffRails = true;
            Program.ControlledVehicle = vehicle;

            // Zero delays: this test asserts plain immediate auto-staging. The delay behaviour has
            // its own test; CleanupAfterFlight reloads the user's config.
            Core.Config.EngineDelays.Clear();
            Core.Config.DecouplerDelays.Clear();

            int stagesLeft = CountUnactivatedSequences(vehicle);
            if (stagesLeft < 2)
            {
                HarnessLog.Line($"[autostage-flight] SKIP: '{saveId}' has {stagesLeft} unactivated sequence(s); " +
                                "auto-staging needs a stage beyond the launch stage.");
                return 0;
            }

            vehicle.ToggleEnum(AutoStageToggle.Enabled);
            if (!Mod.AutoStageEnabled)
            {
                HarnessLog.Line("[autostage-flight] FAIL: ToggleEnum(AutoStageToggle) did not enable the mod.");
                return 1;
            }

            AutoStageHost.HoldProgradeFullThrottle(vehicle);
            AutoStageHost.IgniteFirstStage(vehicle, driver);
            if (!StagingHelpers.HasActiveEngineWithPropellant(vehicle))
            {
                HarnessLog.Line("[autostage-flight] FAIL: the first stage did not ignite.");
                return 1;
            }

            double startMass = vehicle.TotalMass;
            double mu = vehicle.Orbit.Parent.Mu;
            double startEnergy = Orbit.GetOrbitalEnergy(in vehicle.Orbit.StateVectors, mu);
            int lastNext = vehicle.Parts.SequenceList.GetNextSequenceNumber();
            int autoActivations = 0;
            int elapsed = 0;
            int dryStreak = 0;
            while (elapsed < MaxFlightSeconds)
            {
                driver.Step(1.0);
                elapsed++;
                int next = vehicle.Parts.SequenceList.GetNextSequenceNumber();
                if (next != lastNext)
                {
                    autoActivations++;
                    HarnessLog.Line($"[autostage-flight] auto staging {autoActivations} at t={elapsed}s " +
                                    $"(next sequence {next}, mass={vehicle.TotalMass:F1}kg).");
                    lastNext = next;
                }
                dryStreak = next == -1 && !StagingHelpers.HasActiveEngineWithPropellant(vehicle) ? dryStreak + 1 : 0;
                if (dryStreak >= DrySteps)
                    break;
            }

            bool dry = !StagingHelpers.HasActiveEngineWithPropellant(vehicle);
            int remaining = vehicle.Parts.SequenceList.GetNextSequenceNumber();
            if (elapsed >= MaxFlightSeconds)
            {
                HarnessLog.Line($"[autostage-flight] FAIL: guard hit after {MaxFlightSeconds}s " +
                                $"(next sequence {remaining}, {(dry ? "dry" : "still burning")}).");
                ok = false;
            }
            if (remaining != -1)
            {
                HarnessLog.Line($"[autostage-flight] FAIL: flight ended with sequence {remaining} never auto-activated.");
                ok = false;
            }
            if (autoActivations < 1)
            {
                HarnessLog.Line("[autostage-flight] FAIL: no automatic staging was observed.");
                ok = false;
            }
            if (vehicle.TotalMass >= startMass)
            {
                HarnessLog.Line($"[autostage-flight] FAIL: mass did not decrease ({startMass:F1} -> {vehicle.TotalMass:F1}kg).");
                ok = false;
            }
            // A prograde burn must add orbital energy; valid for the elliptical start and a
            // hyperbolic end state alike, unlike an apoapsis comparison.
            double endEnergy = Orbit.GetOrbitalEnergy(in vehicle.Orbit.StateVectors, mu);
            if (endEnergy <= startEnergy)
            {
                HarnessLog.Line($"[autostage-flight] FAIL: orbital energy did not increase " +
                                $"({startEnergy:E3} -> {endEnergy:E3} J/kg); the burn did no prograde work.");
                ok = false;
            }
            double clearance = vehicle.Orbit.Periapsis - homeBody.MeanRadius;
            if (clearance < MinPeriapsisClearanceM)
            {
                HarnessLog.Line($"[autostage-flight] FAIL: periapsis only {clearance / 1000.0:F0}km above " +
                                $"'{homeBody.Id}' (min {MinPeriapsisClearanceM / 1000.0:F0}km), collision trajectory.");
                ok = false;
            }
            HarnessLog.Line($"[autostage-flight] summary: {autoActivations} auto staging(s), {elapsed}s sim time, " +
                            $"mass {startMass:F1} -> {vehicle.TotalMass:F1}kg, energy {startEnergy:E3} -> {endEnergy:E3} J/kg, " +
                            $"periapsis clearance {clearance / 1000.0:F0}km.");
        }
        finally
        {
            AutoStageHost.CleanupAfterFlight(system, preexisting);
        }

        HarnessLog.Line($"[autostage-flight] {TestSupport.Verdict(ok)}");
        return ok ? 0 : 1;
    }

    private static int CountUnactivatedSequences(Vehicle vehicle)
    {
        int count = 0;
        foreach (Sequence seq in vehicle.Parts.SequenceList.Sequences)
        {
            if (!seq.Activated && !seq.Parts.IsEmpty)
                count++;
        }
        return count;
    }
}
