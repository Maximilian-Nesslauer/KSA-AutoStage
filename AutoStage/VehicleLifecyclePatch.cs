using AutoStage.Core;
using HarmonyLib;
using KSA;

namespace AutoStage;

[HarmonyPatch(typeof(Vehicle), nameof(Vehicle.Dispose))]
static class Patch_Vehicle_Dispose
{
    // Prefix so Vehicle.Id is still valid.
    static void Prefix(Vehicle __instance) => Config.RemoveVehicle(__instance.Id);
}
