using AutoStage.Core;
using HarmonyLib;
using KSA;

namespace AutoStage;

// Dispose(bool), not Dispose(): the parameterless overload only delegates here, and the
// EVA-boarding path calls Dispose(endMission: false) directly, so patching the delegate
// would miss a kitten vehicle absorbed by boarding. Targeting the name alone is also
// ambiguous across the two overloads and throws at patch time.
[HarmonyPatch(typeof(Vehicle), nameof(Vehicle.Dispose), new[] { typeof(bool) })]
static class Patch_Vehicle_Dispose
{
    // Prefix so Vehicle.Id is still valid.
    static void Prefix(Vehicle __instance) => Config.RemoveVehicle(__instance.Id);
}
