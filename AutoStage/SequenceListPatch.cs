using HarmonyLib;
using KSA;

namespace AutoStage;

[HarmonyPatch(typeof(SequenceList), nameof(SequenceList.ActivateNextSequence))]
static class Patch_SequenceList_ActivateNextSequence
{
    static void Postfix() => StagingHelpers.InvalidateSequenceCache();
}
