namespace Backroom.EndToEndTests;

/// <summary>Bounded polling: asks again until the answer is the expected one or the deadline passes. No fixed waits.</summary>
internal static class Eventually
{
    /// <summary>The longest a test waits for something that must happen; far above what it takes, so only a failure waits it out.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan s_interval = TimeSpan.FromMilliseconds(200);

    public static async Task<T> UntilAsync<T>(
        Func<CancellationToken, Task<T>> probe,
        Func<T, bool> done,
        string what,
        CancellationToken cancellationToken,
        TimeSpan? deadline = null)
    {
        var limit = deadline ?? Deadline;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(limit);
        var last = default(T);
        try
        {
            while (true)
            {
                last = await probe(timeout.Token);
                if (done(last))
                {
                    return last;
                }

                await Task.Delay(s_interval, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"{what} did not happen within {limit.TotalSeconds:0} s; last answer: {last}.");
        }
    }

    /// <summary>Waits for <paramref name="task"/> until the deadline.</summary>
    public static async Task<T> WithinDeadlineAsync<T>(Task<T> task, string what, CancellationToken cancellationToken)
    {
        await WithinDeadlineAsync((Task)task, what, cancellationToken);
        return await task;
    }

    /// <summary>Waits for <paramref name="task"/> until the deadline.</summary>
    public static async Task WithinDeadlineAsync(Task task, string what, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        try
        {
            await task.WaitAsync(Deadline, cancellationToken);
        }
        catch (TimeoutException exception)
        {
            throw new TimeoutException($"{what} did not happen within {Deadline.TotalSeconds:0} s.", exception);
        }
    }
}
