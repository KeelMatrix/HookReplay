using System.Runtime.CompilerServices;

namespace KeelMatrix.HookReplay.Tests;

internal static class TestTelemetrySuppression
{
    [ModuleInitializer]
    internal static void DisableProductionTelemetry()
    {
        Environment.SetEnvironmentVariable("KEELMATRIX_NO_TELEMETRY", "1");
    }
}
