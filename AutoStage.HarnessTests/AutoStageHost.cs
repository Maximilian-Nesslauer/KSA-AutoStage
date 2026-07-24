using AutoStage.Core;
using HeadlessHarness.Harness;
using KSA;

namespace AutoStage.HarnessTests;

// Boots the real AutoStage lifecycle once per harness process and holds the helpers the tests
// share. StarMap never initializes AutoStage in a headless run (the harness exits before
// Program.Main under a minimal manifest), so the tests drive the mod's own entry points instead:
// OnImmediateLoad injects the gauge enum, OnFullyLoaded validates reflection targets and applies
// the Harmony patches. Like the harness's own patches, they stay installed until the process exits.
internal static class AutoStageHost
{
    public const double SpawnAltitudeM = 500_000.0; // near-vacuum, matches the harness flight test

    private static bool _initRan;

    public static bool CoreOk { get; private set; }

    public static void EnsureInitialized()
    {
        if (_initRan)
            return;
        _initRan = true;

        Mod mod = new Mod();
        mod.OnImmediateLoad(null!); // the KSA.Mod parameter is unused by AutoStage
        mod.OnFullyLoaded();
        // Mirrors the gate Mod.OnFullyLoaded applies before patching, so CoreOk false means the
        // staging patches are genuinely absent.
        CoreOk = GameReflection.ValidateAll() && GaugeEnumInjected();
    }

    public static bool GaugeEnumInjected() =>
        GameReflection.GaugeButton_EnumTypes?.GetValue(null) is List<EnumTypeOption> options
        && options.Any(o => o.Type == typeof(AutoStageToggle));

    // The same spawn the harness flight test uses: the save from the game's Vehicles folder, on a
    // circular orbit above the home body.
    public static Vehicle SpawnFromSave(HeadlessSession session, string saveId, string id, out Astronomical homeBody)
    {
        // GrainGeometryLibrary.LoadAll runs during game bring-up next to SubstanceLibrary.LoadAll,
        // and the headless bring-up only does the latter, so a save with a solid motor would throw
        // "No grain geometries loaded" out of SolidGrainSegment.CreateComponents. Load it here
        // rather than depending on some other consumer's test having run first.
        if (GrainGeometryLibrary.All().IsEmpty)
            GrainGeometryLibrary.LoadAll();

        CelestialSystem system = session.System;
        if (system.HomeBody is not IParentBody home || home is not Astronomical body)
            throw new InvalidOperationException("the loaded system has no home body to orbit.");
        homeBody = body;
        Orbit orbit = VehicleSpawner.CircularCci(home, body.MeanRadius + SpawnAltitudeM, Universe.GetElapsedSimTime());
        return VehicleSpawner.SpawnFromSave(saveId, system, home, id, orbit);
    }

    // Full manual throttle with the flight computer holding prograde (the navball prograde track a
    // player clicks), so the burn only raises the orbit and never turns into a descent.
    public static void HoldProgradeFullThrottle(Vehicle vehicle)
    {
        vehicle.FlightComputer.BurnMode = FlightComputerBurnMode.Manual;
        vehicle.FlightComputer.TrackTarget(FlightComputerAttitudeTrackTarget.Prograde);
        TestSupport.SetManualControlInputs(vehicle, 1f, engineOn: true);
    }

    // AutoStage is edge-triggered on propellant depletion, so the first stage is lit the way a
    // player lights it: one staging-key press. The activation lands via the input queue on the
    // next driver step.
    public static void IgniteFirstStage(Vehicle vehicle, SimDriver driver)
    {
        if (StagingHelpers.HasActiveEngineWithPropellant(vehicle))
            return;
        vehicle.Parts.SequenceList.ActivateNextSequence(vehicle);
        driver.Step(1.0);
    }

    public static bool IsSequenceActivated(Vehicle vehicle, int number)
    {
        foreach (Sequence seq in vehicle.Parts.SequenceList.Sequences)
        {
            if (seq.Number == number)
                return seq.Activated;
        }
        return true; // a sequence that no longer exists cannot be waiting to activate
    }

    // Undo everything a flying test changed globally, so later tests (including the harness's own)
    // see a clean session: control released, mod disabled, state machine and in-memory config back
    // to their loaded state, and every vehicle the test spawned or shed despawned.
    public static void CleanupAfterFlight(CelestialSystem system, HashSet<string> preexisting)
    {
        Program.ControlledVehicle = null;
        Mod.AutoStageEnabled = false;
        StagingDetectionPatch.Reset();
        VehicleUpdateTask._forceOffRails = false;
        Config.LoadGlobalConfig();
        TestSupport.DespawnNewVehicles(system, preexisting);
    }
}
