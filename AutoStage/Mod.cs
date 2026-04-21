using AutoStage.Core;
using Brutal.Logging;
using HarmonyLib;
using KSA;
using StarMap.API;

namespace AutoStage;

[StarMapMod]
public class Mod
{
    private static Harmony? _harmony;

    private const string TestedGameVersion = "v2026.4.16.4170";

    public static bool AutoStageEnabled;
    public static bool IgnitionDelayAvailable;

    /// <summary>
    /// Injects our enum into the gauge button lookup before the game processes
    /// Gauges.xml, so BurnControlPatch.xml can resolve Action="AutoStageToggle".
    /// </summary>
    [StarMapImmediateLoad]
    public void OnImmediateLoad(KSA.Mod mod)
    {
        if (DebugConfig.AutoStage)
            DefaultCategory.Log.Debug("[AutoStage] ImmediateLoad: injecting enum...");

        InjectEnumLookup();
    }

    [StarMapAllModsLoaded]
    public void OnFullyLoaded()
    {
        string gameVersion = VersionInfo.Current.VersionString;
        DefaultCategory.Log.Info($"[AutoStage] Game version: {gameVersion}");
        if (gameVersion != TestedGameVersion)
            DefaultCategory.Log.Warning(
                $"[AutoStage] Tested against {TestedGameVersion}, current is {gameVersion}. " +
                "Some features may not work correctly.");

        Config.Init();

        _harmony = new Harmony("com.maxi.autostage");

        bool coreOk = GameReflection.ValidateAll();
        if (coreOk)
        {
            _harmony.CreateClassProcessor(typeof(Patch_ToggleEnum)).Patch();
            _harmony.CreateClassProcessor(typeof(Patch_IsSet)).Patch();
            _harmony.CreateClassProcessor(typeof(Patch_IsFlightComputerDisabled)).Patch();
            _harmony.Patch(GameReflection.Vehicle_UpdateFromTaskResults,
                prefix: new HarmonyMethod(typeof(StagingDetectionPatch), nameof(StagingDetectionPatch.Prefix)),
                postfix: new HarmonyMethod(typeof(StagingDetectionPatch), nameof(StagingDetectionPatch.Postfix)));

            if (DebugConfig.AutoStage)
                DefaultCategory.Log.Debug("[AutoStage] Core patches applied.");
        }
        else
        {
            DefaultCategory.Log.Warning("[AutoStage] Disabled - reflection targets not found.");
        }

        if (coreOk && GameReflection.ValidateIgnitionDelay())
        {
            IgnitionDelayAvailable = true;
            _harmony.CreateClassProcessor(typeof(PartWindowPatch)).Patch();
            _harmony.CreateClassProcessor(typeof(SettingsTabPatch)).Patch();

            if (DebugConfig.IgnitionDelay)
                DefaultCategory.Log.Debug("[AutoStage] IgnitionDelay patches applied.");
        }
        else if (coreOk)
        {
            IgnitionDelayAvailable = false;
            DefaultCategory.Log.Warning(
                "[AutoStage] IgnitionDelay disabled - reflection targets not found.");
        }

        DefaultCategory.Log.Info("[AutoStage] Loaded.");
    }

    [StarMapUnload]
    public void Unload()
    {
        _harmony?.UnpatchAll(_harmony.Id);
        _harmony = null;
        AutoStageEnabled = false;
        IgnitionDelayAvailable = false;
        StagingDetectionPatch.Reset();
        SettingsTabPatch.Reset();
        Config.Reset();
#if DEBUG
        PerfTracker.Reset();
#endif
        DefaultCategory.Log.Info("[AutoStage] Unloaded.");
    }

    private static void InjectEnumLookup()
    {
        if (GameReflection.GaugeButton_enumLookup == null)
        {
            DefaultCategory.Log.Error(
                "[AutoStage] GaugeButtonFlightComputer._enumLookup not found.");
            return;
        }

        if (GameReflection.GaugeButton_enumLookup.GetValue(null) is not Dictionary<string, Type> dict)
        {
            DefaultCategory.Log.Error("[AutoStage] _enumLookup is null or unexpected type.");
            return;
        }

        dict["AutoStageToggle"] = typeof(AutoStageToggle);

        if (DebugConfig.AutoStage)
            DefaultCategory.Log.Debug(
                $"[AutoStage] Injected AutoStageToggle into _enumLookup ({dict.Count} entries).");
    }
}
