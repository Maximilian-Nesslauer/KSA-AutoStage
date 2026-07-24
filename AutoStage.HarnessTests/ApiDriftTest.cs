using System.Reflection;
using AutoStage.Core;
using HarmonyLib;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;
using KSA;

namespace AutoStage.HarnessTests;

// Verifies AutoStage's grip on the game build without flying anything: every reflection target the
// mod validates at load must resolve, the gauge enum injection must land in
// GaugeButtonFlightComputer.EnumTypes, and every method the mod patches must actually carry that
// patch. This is the fast drift alarm on a game update.
public sealed class ApiDriftTest : IHarnessTest
{
    private const string HarmonyId = "com.maxi.autostage";

    public string Name => "autostage-api-drift";

    public int Run(HeadlessSession session)
    {
        AutoStageHost.EnsureInitialized();

        bool coreOk = GameReflection.ValidateAll();
        bool delayOk = GameReflection.ValidateIgnitionDelay();
        bool enumOk = AutoStageHost.GaugeEnumInjected();

        HarnessLog.Line($"[autostage-api-drift] core reflection={(coreOk ? "ok" : "MISSING")}, " +
                        $"ignition-delay reflection={(delayOk ? "ok" : "MISSING")}, " +
                        $"gauge enum injected={(enumOk ? "ok" : "MISSING")}");

        // The staging detector's caches are invalidated from postfixes, so a patch that silently
        // fails to apply does not throw: it leaves the mod reading a stale view of the sequence
        // list for the rest of the flight. Assert the hooks are really installed.
        bool patchesOk =
            CheckPatched(AccessTools.Method(typeof(SequenceList), nameof(SequenceList.ActivateNextSequence)),
                "SequenceList.ActivateNextSequence")
            & CheckPatched(AccessTools.Method(typeof(SequenceList), nameof(SequenceList.ResetCaches)),
                "SequenceList.ResetCaches")
            & CheckPatched(GameReflection.Vehicle_UpdateFromTaskResults, "Vehicle.UpdateFromTaskResults");

        bool ok = coreOk && delayOk && enumOk && patchesOk;
        HarnessLog.Line($"[autostage-api-drift] {TestSupport.Verdict(ok)}");
        return ok ? 0 : 1;
    }

    private static bool CheckPatched(MethodBase? target, string name)
    {
        if (target == null)
        {
            HarnessLog.Line($"[autostage-api-drift] patch target '{name}' does not resolve => MISSING");
            return false;
        }

        Patches? info = Harmony.GetPatchInfo(target);
        bool patched = info != null && (Owns(info.Prefixes) || Owns(info.Postfixes) || Owns(info.Transpilers));
        HarnessLog.Line($"[autostage-api-drift] '{name}' patched by {HarmonyId} => {(patched ? "ok" : "MISSING")}");
        return patched;
    }

    private static bool Owns(IEnumerable<Patch> patches)
    {
        foreach (Patch patch in patches)
        {
            if (patch.owner == HarmonyId) return true;
        }
        return false;
    }
}
