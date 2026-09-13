namespace KeelMatrix.HookReplay;

internal sealed class LimitedMemoryStream : MemoryStream
{
    private readonly int limit;
    private readonly string limitMessage;

    public LimitedMemoryStream(int limit, string? limitMessage = null)
    {
        this.limit = limit;
        this.limitMessage = limitMessage ??
            "The HTTP body exceeds the configured MaxBodyBytes limit of " + limit + " bytes.";
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        EnsureCapacity(count);
        base.Write(buffer, offset, count);
    }

    public override Task WriteAsync(
        byte[] buffer,
        int offset,
        int count,
        CancellationToken cancellationToken)
    {
        EnsureCapacity(count);
        return base.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override void WriteByte(byte value)
    {
        EnsureCapacity(1);
        base.WriteByte(value);
    }

    private void EnsureCapacity(int incoming)
    {
        long required = Length + (long)incoming;
        if (required > limit)
            throw new HookReplaySizeLimitException(limitMessage);

        if (required > Capacity)
        {
            int doubledCapacity = Capacity == 0
                ? 256
                : Capacity > limit / 2
                    ? limit
                    : Capacity * 2;
            Capacity = Math.Max((int)required, Math.Min(limit, doubledCapacity));
        }
    }
}
