using KeelMatrix.Telemetry;

namespace KeelMatrix.HookReplay;

internal interface IHookReplayTelemetry
{
    void TrackActivation();

    void TrackHeartbeat();
}

internal sealed class HookReplayTelemetry : IHookReplayTelemetry
{
    private readonly Client client;

    public HookReplayTelemetry()
    {
        client = new Client("HookReplay", typeof(HookReplayHandler));
    }

    public void TrackActivation()
    {
        client.TrackActivation();
    }

    public void TrackHeartbeat()
    {
        client.TrackHeartbeat();
    }
}
