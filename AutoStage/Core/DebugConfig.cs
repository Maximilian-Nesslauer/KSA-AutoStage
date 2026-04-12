namespace AutoStage.Core;

static class DebugConfig
{
#if DEBUG
    public static bool AutoStage = true;
    public static bool Performance = true;
#else
    public static bool AutoStage = false;
    public static bool Performance = false;
#endif

    public static bool Any => AutoStage || Performance;
}
