using System.Collections.Generic;
using Brutal.Logging;

namespace AutoStage.Core;

/// <summary>
/// Log deduplication for per-frame call sites. The UI drawers run every
/// frame; without this a persistent failure would flood the log with the
/// same message several times per second.
/// </summary>
static class LogHelper
{
    private static readonly HashSet<string> _loggedWarnings = new();

    public static void WarnOnce(string key, string message)
    {
        if (_loggedWarnings.Add(key))
            DefaultCategory.Log.Warning(message);
    }

    public static void ErrorOnce(string key, string message)
    {
        if (_loggedWarnings.Add(key))
            DefaultCategory.Log.Error(message);
    }

    public static void Reset() => _loggedWarnings.Clear();
}
