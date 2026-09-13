namespace KeelMatrix.HookReplay;

internal static class TaskExtensions
{
    public static async Task WaitWithCancellationAsync(
        this Task task,
        CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            await task.ConfigureAwait(false);
            return;
        }

        var cancellation = new TaskCompletionSource<bool>();
        using (cancellationToken.Register(
            state => ((TaskCompletionSource<bool>)state!).TrySetResult(true),
            cancellation))
        {
            Task completed = await Task.WhenAny(task, cancellation.Task).ConfigureAwait(false);
            if (completed == cancellation.Task)
                throw new OperationCanceledException(cancellationToken);
            await task.ConfigureAwait(false);
        }
    }
}
