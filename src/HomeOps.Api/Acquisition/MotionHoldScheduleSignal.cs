using System.Threading.Channels;

namespace HomeOps.Api.Acquisition;

public sealed class MotionHoldScheduleSignal
{
    private readonly Channel<bool> _channel = Channel.CreateBounded<bool>(
        new BoundedChannelOptions(1)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.DropWrite
        });

    public void Notify() => _channel.Writer.TryWrite(true);

    public ValueTask<bool> WaitAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAsync(cancellationToken);
}
