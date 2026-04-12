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

    // IgnitionDelay: SequenceList.ActiveSequence has a private setter
    public static readonly PropertyInfo? SequenceList_ActiveSequence =
        AccessTools.Property(typeof(SequenceList), "ActiveSequence");

    // IgnitionDelay: SequenceList._updatingSequence guards against re-entrant ResetCaches
    public static readonly FieldInfo? SequenceList_updatingSequence =
        AccessTools.Field(typeof(SequenceList), "_updatingSequence");

    // IgnitionDelay: ModLibrary.AllParts (internal) for enumerating engine templates
    public static readonly FieldInfo? ModLibrary_AllParts =
        AccessTools.Field(typeof(ModLibrary), "AllParts");

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

    public static bool ValidateIgnitionDelay()
    {
        var targets = new (string name, object? target)[]
        {
            ("SequenceList.ActiveSequence", SequenceList_ActiveSequence),
            ("SequenceList._updatingSequence", SequenceList_updatingSequence),
        };

        bool allOk = true;
        foreach (var (name, target) in targets)
        {
            if (target == null)
            {
                DefaultCategory.Log.Error(
                    $"[AutoStage] IgnitionDelay: {name} not found - game version may have changed.");
                allOk = false;
            }
        }
        return allOk;
    }
}
