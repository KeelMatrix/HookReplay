using KeelMatrix.Telemetry;

namespace KeelMatrix.HookReplay;

internal interface IHookReplayTelemetry
{
    void TrackActivation();

    void TrackHeartbeat();
}

internal sealed class HookReplayTelemetry : IHookReplayTelemetry
{
    private readonly Client? client;

    public HookReplayTelemetry()
    {
        try
        {
            client = new Client("HookReplay", typeof(HookReplayHandler));
        }
        catch
        {
            client = null;
        }
    }

    public void TrackActivation()
    {
        try
        {
            client?.TrackActivation();
        }
        catch
        {
        }
    }

    public void TrackHeartbeat()
    {
        try
        {
            client?.TrackHeartbeat();
        }
        catch
        {
        }
    }
}
