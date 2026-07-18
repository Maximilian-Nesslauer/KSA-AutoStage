using AutoStage.Core;
using HeadlessHarness.Core;
using HeadlessHarness.Harness;

namespace AutoStage.HarnessTests;

// Verifies AutoStage's grip on the game build without flying anything: every reflection target the
// mod validates at load must resolve, and the gauge enum injection must land in
// GaugeButtonFlightComputer.EnumTypes. This is the fast drift alarm on a game update.
public sealed class ApiDriftTest : IHarnessTest
{
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
        bool ok = coreOk && delayOk && enumOk;
        HarnessLog.Line($"[autostage-api-drift] {TestSupport.Verdict(ok)}");
        return ok ? 0 : 1;
    }
}
