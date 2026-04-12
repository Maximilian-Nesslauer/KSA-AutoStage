using System.Reflection;
using Brutal.Logging;
using HarmonyLib;
using KSA;

namespace AutoStage.Core;

static class GameReflection
{
    public static readonly FieldInfo? GaugeButton_enumLookup =
        AccessTools.Field(typeof(GaugeButtonFlightComputer), "_enumLookup");

    public static readonly MethodInfo? Vehicle_UpdateFromTaskResults =
        AccessTools.Method(typeof(Vehicle), "UpdateFromTaskResults");

    public static bool ValidateAll()
    {
        var targets = new (string name, object? target)[]
        {
            ("GaugeButtonFlightComputer._enumLookup", GaugeButton_enumLookup),
            ("Vehicle.UpdateFromTaskResults", Vehicle_UpdateFromTaskResults),
        };

        bool allOk = true;
        foreach (var (name, target) in targets)
        {
            if (target == null)
            {
                DefaultCategory.Log.Error(
                    $"[AutoStage] {name} not found - game version may have changed.");
                allOk = false;
            }
        }
        return allOk;
    }
}
