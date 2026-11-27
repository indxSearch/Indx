namespace IndxServer.Services
{
    /// <summary>
    /// The one polling loop the dataset console uses for anything that reports progress
    /// out-of-band (a ProcessMonitor, the shadow-build percent, a dataset's SystemState):
    /// wait <c>interval</c>, run <c>tick</c>, ask the component to re-render, repeat until
    /// <c>tick</c> says it is done or the token is cancelled. Cancellation and a disposed
    /// circuit end the loop quietly — the caller owns nothing but the token.
    /// </summary>
    public static class ProgressPoller
    {
        /// <param name="interval">Delay before each tick.</param>
        /// <param name="tick">Reads the latest progress into component state. Return false to stop.</param>
        /// <param name="render">Typically <c>() => InvokeAsync(StateHasChanged)</c>.</param>
        /// <param name="ct">Stops the loop; cancellation is swallowed.</param>
        public static async Task RunAsync(TimeSpan interval, Func<Task<bool>> tick, Func<Task> render, CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await Task.Delay(interval, ct);
                    var keepGoing = await tick();
                    await render();
                    if (!keepGoing) break;
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }

        public static Task RunAsync(TimeSpan interval, Func<bool> tick, Func<Task> render, CancellationToken ct)
            => RunAsync(interval, () => Task.FromResult(tick()), render, ct);
    }
}
