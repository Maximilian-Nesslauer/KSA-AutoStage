using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Brutal.Logging;
using KSA;

namespace AutoStage.Core;

/// <summary>
/// Manages ignition / decoupler delay configuration with two layers:
/// 1. Global config (autostage.toml) with per-part-variant delays
/// 2. Per-vehicle overrides (vehicles/{id}.toml) with per-sequence delays
///
/// Engine and decoupler delays are tracked independently. Decoupler delays
/// default to 0 so stock staging timing is preserved unless the user sets one.
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
    public static Dictionary<string, double> DecouplerDelays { get; } = new();

    // Vehicle ID -> (Sequence number -> delay in seconds)
    private static readonly Dictionary<string, Dictionary<int, double>> _vehicleEngineOverrides = new();
    private static readonly Dictionary<string, Dictionary<int, double>> _vehicleDecouplerOverrides = new();

    private static readonly HashSet<string> _dirtyVehicles = new();

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
        DecouplerDelays.Clear();
        _vehicleEngineOverrides.Clear();
        _vehicleDecouplerOverrides.Clear();
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
        DecouplerDelays.Clear();

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
                LoadDelaySection(engines, EngineDelays);

            if (sections.TryGetValue("decoupler_delays", out var decouplers))
                LoadDelaySection(decouplers, DecouplerDelays);

            if (DebugConfig.IgnitionDelay)
                DefaultCategory.Log.Debug(
                    $"[AutoStage] Config loaded: {EngineDelays.Count} engine delays, " +
                    $"{DecouplerDelays.Count} decoupler delays");
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Error($"[AutoStage] Failed to load config: {ex.Message}");
        }
    }

    private static void LoadDelaySection(Dictionary<string, string> raw,
        Dictionary<string, double> target)
    {
        foreach (var kvp in raw)
        {
            if (TryParseDelay(kvp.Value, out double d))
                target[kvp.Key] = d;
        }
    }

    public static void SaveGlobalConfig()
    {
        try
        {
            Directory.CreateDirectory(_modDir);
            using var writer = new StreamWriter(_configPath);
            writer.WriteLine("# AutoStage delay configuration.");
            writer.WriteLine("# Per-part-variant delays keyed by Part Template ID.");
            writer.WriteLine("# Values are seconds to wait after staging before the part activates.");
            writer.WriteLine();
            writer.WriteLine("[engine_delays]");
            WriteDelaySection(writer, EngineDelays);
            writer.WriteLine();
            writer.WriteLine("[decoupler_delays]");
            WriteDelaySection(writer, DecouplerDelays);

            if (DebugConfig.IgnitionDelay)
                DefaultCategory.Log.Debug("[AutoStage] Config saved.");
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Error($"[AutoStage] Failed to save config: {ex.Message}");
        }
    }

    private static void WriteDelaySection(StreamWriter writer, Dictionary<string, double> source)
    {
        foreach (var kvp in source)
        {
            writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0} = {1:F1}", kvp.Key, kvp.Value));
        }
    }

    #endregion

    #region Delay Lookup

    public static double GetEngineDelay(string partTemplateId)
        => EngineDelays.TryGetValue(partTemplateId, out double d) ? d : 0.0;

    public static double GetDecouplerDelay(string partTemplateId)
        => DecouplerDelays.TryGetValue(partTemplateId, out double d) ? d : 0.0;

    /// <summary>
    /// Effective engine ignition delay for a sequence. Priority:
    /// 1. Per-sequence vehicle override
    /// 2. Max engine-variant delay across the sequence's engine parts
    /// </summary>
    public static double GetSequenceEngineDelay(Vehicle vehicle, int sequenceNumber)
    {
        if (_vehicleEngineOverrides.TryGetValue(vehicle.Id, out var overrides)
            && overrides.TryGetValue(sequenceNumber, out double overrideDelay))
            return overrideDelay;
        return ComputeSequenceEngineDelay(vehicle, sequenceNumber);
    }

    /// <summary>
    /// Effective decoupler delay for a sequence. Priority:
    /// 1. Per-sequence vehicle override
    /// 2. Max decoupler-variant delay across the sequence's decoupler parts
    /// </summary>
    public static double GetSequenceDecouplerDelay(Vehicle vehicle, int sequenceNumber)
    {
        if (_vehicleDecouplerOverrides.TryGetValue(vehicle.Id, out var overrides)
            && overrides.TryGetValue(sequenceNumber, out double overrideDelay))
            return overrideDelay;
        return ComputeSequenceDecouplerDelay(vehicle, sequenceNumber);
    }

    public static double ComputeSequenceEngineDelay(Vehicle vehicle, int sequenceNumber)
        => ComputeSequenceMaxDelay(vehicle, sequenceNumber,
            hasTargetModule: p => p.HasAny<EngineController>(),
            getDelay: id => GetEngineDelay(id));

    public static double ComputeSequenceDecouplerDelay(Vehicle vehicle, int sequenceNumber)
        => ComputeSequenceMaxDelay(vehicle, sequenceNumber,
            hasTargetModule: p => p.HasAny<Decoupler>(),
            getDelay: id => GetDecouplerDelay(id));

    private static double ComputeSequenceMaxDelay(Vehicle vehicle, int sequenceNumber,
        Func<Part, bool> hasTargetModule, Func<string, double> getDelay)
    {
        double maxDelay = 0.0;
        foreach (Sequence seq in vehicle.Parts.SequenceList.Sequences)
        {
            if (seq.Number != sequenceNumber) continue;
            ReadOnlySpan<Part> parts = seq.Parts;
            for (int i = 0; i < parts.Length; i++)
            {
                if (hasTargetModule(parts[i]))
                    maxDelay = Math.Max(maxDelay, getDelay(parts[i].Template.Id));
            }
            break;
        }
        return maxDelay;
    }

    public static bool HasSequenceEngineOverride(Vehicle vehicle, int sequenceNumber)
        => _vehicleEngineOverrides.TryGetValue(vehicle.Id, out var o)
           && o.ContainsKey(sequenceNumber);

    public static bool HasSequenceDecouplerOverride(Vehicle vehicle, int sequenceNumber)
        => _vehicleDecouplerOverrides.TryGetValue(vehicle.Id, out var o)
           && o.ContainsKey(sequenceNumber);

    public static void SetSequenceEngineOverride(Vehicle vehicle, int sequenceNumber, double delay)
        => SetSequenceOverride(_vehicleEngineOverrides, vehicle.Id, sequenceNumber, delay);

    public static void SetSequenceDecouplerOverride(Vehicle vehicle, int sequenceNumber, double delay)
        => SetSequenceOverride(_vehicleDecouplerOverrides, vehicle.Id, sequenceNumber, delay);

    public static void ClearSequenceEngineOverride(Vehicle vehicle, int sequenceNumber)
        => ClearSequenceOverride(_vehicleEngineOverrides, vehicle.Id, sequenceNumber);

    public static void ClearSequenceDecouplerOverride(Vehicle vehicle, int sequenceNumber)
        => ClearSequenceOverride(_vehicleDecouplerOverrides, vehicle.Id, sequenceNumber);

    private static void SetSequenceOverride(
        Dictionary<string, Dictionary<int, double>> store,
        string vehicleId, int sequenceNumber, double delay)
    {
        if (!store.TryGetValue(vehicleId, out var overrides))
        {
            overrides = new Dictionary<int, double>();
            store[vehicleId] = overrides;
        }
        overrides[sequenceNumber] = Math.Max(0.0, delay);
        _dirtyVehicles.Add(vehicleId);
    }

    private static void ClearSequenceOverride(
        Dictionary<string, Dictionary<int, double>> store,
        string vehicleId, int sequenceNumber)
    {
        if (store.TryGetValue(vehicleId, out var overrides) && overrides.Remove(sequenceNumber))
            _dirtyVehicles.Add(vehicleId);
    }

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
        // Seed both stores so we don't keep re-reading a missing/malformed
        // file on every per-frame call from the part window.
        bool alreadyLoaded = _vehicleEngineOverrides.ContainsKey(vehicleId)
                             && _vehicleDecouplerOverrides.ContainsKey(vehicleId);
        if (alreadyLoaded) return;

        var engine = new Dictionary<int, double>();
        var decoupler = new Dictionary<int, double>();
        _vehicleEngineOverrides[vehicleId] = engine;
        _vehicleDecouplerOverrides[vehicleId] = decoupler;

        string path = GetVehiclePath(vehicleId);
        if (!File.Exists(path)) return;

        try
        {
            var sections = ParseToml(path);
            if (sections.TryGetValue("sequence_delays", out var engineSection))
                LoadSequenceDelays(engineSection, engine);
            if (sections.TryGetValue("decoupler_delays", out var decouplerSection))
                LoadSequenceDelays(decouplerSection, decoupler);

            if ((engine.Count > 0 || decoupler.Count > 0) && DebugConfig.IgnitionDelay)
                DefaultCategory.Log.Debug(
                    $"[AutoStage] Loaded {engine.Count} engine + {decoupler.Count} decoupler " +
                    $"sequence overrides for {vehicleId}");
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Error(
                $"[AutoStage] Failed to load vehicle overrides for {vehicleId}: {ex.Message}");
        }
    }

    private static void LoadSequenceDelays(Dictionary<string, string> raw, Dictionary<int, double> target)
    {
        foreach (var kvp in raw)
        {
            if (int.TryParse(kvp.Key, out int seqNum) && TryParseDelay(kvp.Value, out double d))
                target[seqNum] = d;
        }
    }

    private static void SaveVehicleOverrides(string vehicleId)
    {
        string path = GetVehiclePath(vehicleId);

        try
        {
            _vehicleEngineOverrides.TryGetValue(vehicleId, out var engine);
            _vehicleDecouplerOverrides.TryGetValue(vehicleId, out var decoupler);

            bool hasEngine = engine != null && engine.Count > 0;
            bool hasDecoupler = decoupler != null && decoupler.Count > 0;
            if (!hasEngine && !hasDecoupler)
            {
                if (File.Exists(path))
                    File.Delete(path);
                return;
            }

            Directory.CreateDirectory(_vehiclesDir);
            using var writer = new StreamWriter(path);
            writer.WriteLine("# Per-sequence delay overrides for this vehicle.");
            if (hasEngine)
            {
                writer.WriteLine();
                writer.WriteLine("[sequence_delays]");
                WriteSequenceDelays(writer, engine!);
            }
            if (hasDecoupler)
            {
                writer.WriteLine();
                writer.WriteLine("[decoupler_delays]");
                WriteSequenceDelays(writer, decoupler!);
            }
        }
        catch (Exception ex)
        {
            DefaultCategory.Log.Error(
                $"[AutoStage] Failed to save vehicle overrides for {vehicleId}: {ex.Message}");
        }
    }

    private static void WriteSequenceDelays(StreamWriter writer, Dictionary<int, double> source)
    {
        foreach (var kvp in source)
        {
            writer.WriteLine(string.Format(CultureInfo.InvariantCulture,
                "{0} = {1:F1}", kvp.Key, kvp.Value));
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
