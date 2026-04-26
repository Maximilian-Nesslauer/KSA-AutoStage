using System;
using System.Reflection;
using AutoStage.Core;
using Brutal.Logging;
using HarmonyLib;
using KSA;

namespace AutoStage;

// Used as a type marker in GaugeButtonFlightComputer._enumLookup so the
// XML patch can resolve Action="AutoStageToggle". The single value is never
// inspected, only the runtime type identity.
public enum AutoStageToggle { Enabled }

/// <summary>
/// Vehicle.ToggleEnum/IsSet/IsFlightComputerDisabled use cascading type checks
/// that silently ignore unrecognized enum types. These three patches intercept
/// calls for our AutoStageToggle enum so the gauge button works.
/// </summary>
[HarmonyPatch(typeof(Vehicle), nameof(Vehicle.ToggleEnum))]
static class Patch_ToggleEnum
{
    static bool Prefix(Enum? enumValue)
    {
        if (enumValue is not AutoStageToggle) return true;

        Mod.AutoStageEnabled = !Mod.AutoStageEnabled;

        if (DebugConfig.AutoStage)
            DefaultCategory.Log.Debug($"[AutoStage] Enabled = {Mod.AutoStageEnabled}");

        return false;
    }
}

/// <summary>
/// Makes the gauge button light up when auto-staging is enabled.
/// Targets IsSet(Enum) because PackData() passes _enumValue typed as System.Enum.
/// </summary>
[HarmonyPatch]
static class Patch_IsSet
{
    static MethodBase TargetMethod()
    {
        var open = VehicleReflection.FindGenericMethod("IsSet", parameterCount: 2)
            ?? throw new InvalidOperationException(
                "[AutoStage] Vehicle.IsSet<T>(T, bool) not found.");

        return open.MakeGenericMethod(typeof(Enum));
    }

    // The stock signature is IsSet<T>(T value, bool clicked). The clicked
    // arg only matters for FlightComputerAction overloads; for our toggle
    // the result is the persistent flag, independent of click animation.
    static bool Prefix(Enum value, ref bool __result)
    {
        if (value is not AutoStageToggle) return true;

        __result = Mod.AutoStageEnabled;
        return false;
    }
}

/// <summary>
/// Grays out the button when there are no remaining sequences with engines.
/// </summary>
[HarmonyPatch]
static class Patch_IsFlightComputerDisabled
{
    static MethodBase TargetMethod()
    {
        var open = VehicleReflection.FindGenericMethod("IsFlightComputerDisabled", parameterCount: 1)
            ?? throw new InvalidOperationException(
                "[AutoStage] Vehicle.IsFlightComputerDisabled<T>(T) not found.");

        return open.MakeGenericMethod(typeof(Enum));
    }

    static bool Prefix(Vehicle __instance, Enum value, ref bool __result)
    {
        if (value is not AutoStageToggle) return true;

        __result = !StagingHelpers.HasNextEngineSequence(__instance);
        return false;
    }
}

static class VehicleReflection
{
    public static MethodInfo? FindGenericMethod(string name, int parameterCount)
    {
        foreach (var method in typeof(Vehicle).GetMethods())
        {
            if (method.Name == name
                && method.IsGenericMethodDefinition
                && method.GetParameters().Length == parameterCount)
                return method;
        }
        return null;
    }
}
