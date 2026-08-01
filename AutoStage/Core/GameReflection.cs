using System.Reflection;
using Brutal.Logging;
using HarmonyLib;
using KSA;

namespace AutoStage.Core;

static class GameReflection
{
    public static readonly FieldInfo? GaugeButton_EnumTypes =
        AccessTools.Field(typeof(GaugeButtonFlightComputer), "EnumTypes");

    public static readonly MethodInfo? Vehicle_UpdateFromTaskResults =
        AccessTools.Method(typeof(Vehicle), "UpdateFromTaskResults");

    // Closed against System.Enum because PackData calls them on _enumValue
    // typed as Enum.
    public static readonly MethodInfo? Vehicle_IsSet_Enum =
        FindGenericVehicleMethod("IsSet", parameterCount: 2)?.MakeGenericMethod(typeof(System.Enum));

    public static readonly MethodInfo? Vehicle_IsFlightComputerDisabled_Enum =
        FindGenericVehicleMethod("IsFlightComputerDisabled", parameterCount: 1)?.MakeGenericMethod(typeof(System.Enum));

    public static readonly PropertyInfo? SequenceList_ActiveSequence =
        AccessTools.Property(typeof(SequenceList), "ActiveSequence");

    public static readonly FieldInfo? SequenceList_updatingSequence =
        AccessTools.Field(typeof(SequenceList), "_updatingSequence");

    public static readonly FieldInfo? ModLibrary_AllParts =
        AccessTools.Field(typeof(ModLibrary), "AllParts");

    // Pinned to the bool overload: Dispose() only delegates to it, and the EVA-boarding
    // path calls Dispose(endMission: false) directly, so the delegate misses that vehicle.
    public static readonly MethodInfo? Vehicle_Dispose =
        AccessTools.Method(typeof(Vehicle), nameof(Vehicle.Dispose), new[] { typeof(bool) });

    public static bool ValidateAll()
    {
        var targets = new (string name, object? target)[]
        {
            ("GaugeButtonFlightComputer.EnumTypes", GaugeButton_EnumTypes),
            ("Vehicle.UpdateFromTaskResults", Vehicle_UpdateFromTaskResults),
            ("Vehicle.IsSet<Enum>", Vehicle_IsSet_Enum),
            ("Vehicle.IsFlightComputerDisabled<Enum>", Vehicle_IsFlightComputerDisabled_Enum),
            ("SequenceList.ActiveSequence", SequenceList_ActiveSequence),
        };

        bool allOk = true;
        foreach (var (name, target) in targets)
        {
            if (target == null)
            {
                DefaultCategory.Log.Error(
                    $"[AutoStage] {name} not found, game version may have changed.");
                allOk = false;
            }
        }
        return allOk;
    }

    public static bool ValidateIgnitionDelay()
    {
        var targets = new (string name, object? target)[]
        {
            ("SequenceList._updatingSequence", SequenceList_updatingSequence),
            ("ModLibrary.AllParts", ModLibrary_AllParts),
            ("Vehicle.Dispose(bool)", Vehicle_Dispose),
        };

        bool allOk = true;
        foreach (var (name, target) in targets)
        {
            if (target == null)
            {
                DefaultCategory.Log.Error(
                    $"[AutoStage] IgnitionDelay: {name} not found, game version may have changed.");
                allOk = false;
            }
        }
        return allOk;
    }

    private static MethodInfo? FindGenericVehicleMethod(string name, int parameterCount)
    {
        foreach (MethodInfo method in typeof(Vehicle).GetMethods())
        {
            if (method.Name == name
                && method.IsGenericMethodDefinition
                && method.GetParameters().Length == parameterCount)
                return method;
        }
        return null;
    }
}
