using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Brutal.Logging;
using KSA;

namespace AutoStage.Core;

/// <summary>
/// Manages ignition delay configuration with two layers:
/// 1. Global config (autostage.toml) with per-engine-variant delays
/// 2. Per-vehicle overrides (vehicles/{id}.toml) with per-sequence delays
///
/// All files live in the mod's own directory, never touching game saves.
/// </summary>
static class Config
{
    private static string _modDir = string.Empty;
    private static string _vehiclesDir = string.Empty;
    private static string _configPath = string.Empty;

    // Part Template ID -> delay in seconds
    public static Dictionary<string, double> EngineDelays { get; } = new();

    // Vehicle ID -> (Sequence number -> delay in seconds)
    private static readonly Dictionary<string, Dictionary<int, double>> _vehicleOverrides = new();

    public static void Init()
    {
        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        _modDir = Path.Combine(userProfile, "My Games", "Kitten Space Agency", "mods", "AutoStage");
        _vehiclesDir = Path.Combine(_modDir, "vehicles");
        _configPath = Path.Combine(_modDir, "autostage.toml");

        LoadGlobalConfig();
    }

    public static void Reset()
    {
        EngineDelays.Clear();
        _vehicleOverrides.Clear();
        _dirtyVehicles.Clear();
    }

    private static void SetDefaults()
    {
        EngineDelays["CorePropulsionA_Prefab_EngineA1_Dev"] = 2.0;
        EngineDelays["CorePropulsionA_Prefab_EngineA2"] = 2.0;
        EngineDelays["CorePropulsionA_Prefab_EngineA3"] = 3.0;
        EngineDelays["CorePropulsionA_Prefab_EngineA4"] = 1.5;
        EngineDelays["CorePropulsionA_Prefab_EngineA5"] = 3.0;
        EngineDelays["CorePropulsionA_Prefab_EngineA6"] = 3.0;
    }

    #region TOML Parsing

    /// <summary>
    /// Parses a minimal TOML file into sections of key-value pairs.
    /// Supports comments (#), [section] headers, and key = value lines.
    /// The root section (before any header) uses key "".
    /// </summary>
    private static Dictionary<string, Dictionary<string, string>> ParseToml(string path)
    {
        var result = new Dictionary<string, Dictionary<string, string>>();
        string currentSection = "";
        result[currentSection] = new Dictionary<string, string>();

        foreach (string rawLine in File.ReadAllLines(path))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
                continue;

            if (line[0] == '[')
            {
                int end = line.IndexOf(']');
                if (end > 1)
                {
                    currentSection = line.Substring(1, end - 1).Trim();
                    if (!result.ContainsKey(currentSection))
                        result[currentSection] = new Dictionary<string, string>();
                }
                continue;
            }

            int eq = line.IndexOf('=');
            if (eq < 1) continue;

            string key = line.Substring(0, eq).Trim();
            string value = line.Substring(eq + 1).Trim();

            int commentIdx = value.IndexOf('#');
            if (commentIdx >= 0)
                value = value.Substring(0, commentIdx).Trim();

            if (!result.ContainsKey(currentSection))
                result[currentSection] = new Dictionary<string, string>();
            result[currentSection][key] = value;
        }

        return result;
    }

    private static bool TryParseDelay(string value, out double delay)
    {
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out delay))
        {
            delay = Math.Max(0.0, delay);
            return true;
        }
        delay = 0.0;
        return false;
    }

    #endregion

    #region Global Config

    public static void LoadGlobalConfig()
    {
        EngineDelays.Clear();

        if (!File.Exists(_configPath))
        {
            SetDefaults();
            SaveGlobalConfig();
            return;
        }

        try
        {
            var sections = ParseToml(_configPath);

            if (sections.TryGetValue("engine_delays", out var engines))
            {
                foreach (var kvp in engines)
                {
                    if (TryParseDelay(kvp.Value, out double ed))
                        EngineDelays[kvp.Key] = ed;
                }
            }

            if (DebugConfig.IgnitionDelay)
                DefaultCategory.Log.Debug(
                    $"[AutoStage] Config loaded: {EngineDelays.Count} engine delays");
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Error($"[AutoStage] Failed to load config: {ex.Message}");
        }
    }

    public static void SaveGlobalConfig()
    {
        try
        {
            Directory.CreateDirectory(_modDir);
            using var writer = new StreamWriter(_configPath);
            writer.WriteLine("# AutoStage ignition delay configuration.");
            writer.WriteLine("# Per-engine-variant delays keyed by Part Template ID.");
            writer.WriteLine("# The value is seconds to wait after staging before engine ignition.");
            writer.WriteLine("[engine_delays]");

            foreach (var kvp in EngineDelays)
            {
                writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0} = {1:F1}", kvp.Key, kvp.Value));
            }

            if (DebugConfig.IgnitionDelay)
                DefaultCategory.Log.Debug("[AutoStage] Config saved.");
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Error($"[AutoStage] Failed to save config: {ex.Message}");
        }
    }

    #endregion

    #region Delay Lookup

    /// <summary>
    /// Gets the configured ignition delay for a specific engine variant.
    /// Returns 0.0 if no delay is configured.
    /// </summary>
    public static double GetEngineDelay(string partTemplateId)
    {
        if (EngineDelays.TryGetValue(partTemplateId, out double delay))
            return delay;
        return 0.0;
    }

    /// <summary>
    /// Gets the effective ignition delay for a sequence. Priority:
    /// 1. Per-sequence override (from vehicle TOML)
    /// 2. Max of engine-variant delays in the sequence
    /// </summary>
    public static double GetSequenceDelay(Vehicle vehicle, int sequenceNumber)
    {
        if (_vehicleOverrides.TryGetValue(vehicle.Id, out var overrides)
            && overrides.TryGetValue(sequenceNumber, out double overrideDelay))
            return overrideDelay;

        return ComputeSequenceDelayFromEngines(vehicle, sequenceNumber);
    }

    /// <summary>
    /// Computes the delay for a sequence based on its engine variants.
    /// Returns the max delay across all engines (wait for the slowest).
    /// </summary>
    public static double ComputeSequenceDelayFromEngines(Vehicle vehicle, int sequenceNumber)
    {
        double maxDelay = 0.0;
        foreach (Sequence seq in vehicle.Parts.SequenceList.Sequences)
        {
            if (seq.Number != sequenceNumber) continue;
            ReadOnlySpan<Part> parts = seq.Parts;
            for (int i = 0; i < parts.Length; i++)
            {
                if (parts[i].HasAny<EngineController>())
                    maxDelay = Math.Max(maxDelay, GetEngineDelay(parts[i].Template.Id));
            }
            break;
        }
        return maxDelay;
    }

    /// <summary>
    /// Checks whether a per-sequence override exists for a vehicle.
    /// </summary>
    public static bool HasSequenceOverride(Vehicle vehicle, int sequenceNumber)
    {
        return _vehicleOverrides.TryGetValue(vehicle.Id, out var overrides)
               && overrides.ContainsKey(sequenceNumber);
    }

    private static readonly HashSet<string> _dirtyVehicles = new();

    /// <summary>
    /// Sets a per-sequence delay override. Call FlushPendingSaves() to persist.
    /// </summary>
    public static void SetSequenceOverride(Vehicle vehicle, int sequenceNumber, double delay)
    {
        if (!_vehicleOverrides.TryGetValue(vehicle.Id, out var overrides))
        {
            overrides = new Dictionary<int, double>();
            _vehicleOverrides[vehicle.Id] = overrides;
        }
        overrides[sequenceNumber] = Math.Max(0.0, delay);
        _dirtyVehicles.Add(vehicle.Id);
    }

    /// <summary>
    /// Removes a per-sequence delay override, falling back to engine defaults.
    /// </summary>
    public static void ClearSequenceOverride(Vehicle vehicle, int sequenceNumber)
    {
        if (_vehicleOverrides.TryGetValue(vehicle.Id, out var overrides))
        {
            overrides.Remove(sequenceNumber);
            if (overrides.Count == 0)
                _vehicleOverrides.Remove(vehicle.Id);
            _dirtyVehicles.Add(vehicle.Id);
        }
    }

    /// <summary>
    /// Writes any pending vehicle override changes to disk.
    /// </summary>
    public static void FlushPendingSaves()
    {
        foreach (string vehicleId in _dirtyVehicles)
            SaveVehicleOverrides(vehicleId);
        _dirtyVehicles.Clear();
    }

    #endregion

    #region Per-Vehicle Persistence

    public static void LoadVehicleOverrides(string vehicleId)
    {
        if (_vehicleOverrides.ContainsKey(vehicleId))
            return;

        string path = GetVehiclePath(vehicleId);
        if (!File.Exists(path))
            return;

        try
        {
            var sections = ParseToml(path);
            if (!sections.TryGetValue("sequence_delays", out var delays))
                return;

            var overrides = new Dictionary<int, double>();
            foreach (var kvp in delays)
            {
                if (int.TryParse(kvp.Key, out int seqNum)
                    && TryParseDelay(kvp.Value, out double delay))
                {
                    overrides[seqNum] = delay;
                }
            }

            if (overrides.Count > 0)
            {
                _vehicleOverrides[vehicleId] = overrides;
                if (DebugConfig.IgnitionDelay)
                    DefaultCategory.Log.Debug(
                        $"[AutoStage] Loaded {overrides.Count} sequence overrides for {vehicleId}");
            }
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Error(
                $"[AutoStage] Failed to load vehicle overrides for {vehicleId}: {ex.Message}");
        }
    }

    private static void SaveVehicleOverrides(string vehicleId)
    {
        string path = GetVehiclePath(vehicleId);

        if (!_vehicleOverrides.TryGetValue(vehicleId, out var overrides) || overrides.Count == 0)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            return;
        }

        try
        {
            Directory.CreateDirectory(_vehiclesDir);
            using var writer = new StreamWriter(path);
            writer.WriteLine("# Per-sequence ignition delay overrides for this vehicle.");
            writer.WriteLine("[sequence_delays]");
            foreach (var kvp in overrides)
            {
                writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                    "{0} = {1:F1}", kvp.Key, kvp.Value));
            }
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Error(
                $"[AutoStage] Failed to save vehicle overrides for {vehicleId}: {ex.Message}");
        }
    }

    private static string GetVehiclePath(string vehicleId)
    {
        string safeId = vehicleId;
        foreach (char c in Path.GetInvalidFileNameChars())
            safeId = safeId.Replace(c, '_');
        return Path.Combine(_vehiclesDir, safeId + ".toml");
    }

    #endregion
}
