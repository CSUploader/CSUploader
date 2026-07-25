// <copyright file="UiRefreshCoalescer.cs" company="CSUploader">
// Copyright (c) CSUploader. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.
// </copyright>

using CSUploader.Lib;

namespace CSUploader.Services;

/// <summary>
/// Coalesces bursts of background-thread refresh requests into a bounded number of UI-thread runs.
/// <para>
/// <see cref="Request"/> is thread-safe and callable from any thread. A storm of N calls yields AT MOST two UI
/// runs — one in-flight, plus a single follow-up that captures anything that arrived while it ran — never N. Exactly
/// one run executes at a time, so the refresh delegate never overlaps itself.
/// </para>
/// <para>
/// Built for view-models that refresh a whole list off a high-frequency domain event (e.g. per-file upload
/// completion). A naïve "reload on every event" posts one full reload per event; a few hundred completions then
/// bury the UI thread under a matching pile of dispatcher jobs (observed: ~2,000 queued <c>DispatcherOperation</c>s
/// and a frozen window). Coalescing caps that pile.
/// </para>
/// </summary>
public sealed class UiRefreshCoalescer(IUiDispatcher dispatcher, Func<Task> refreshAsync, IAppLogger? logger = null)
{
    // 0 = idle; 1 = a run is queued/executing; 2 = executing AND another request arrived (needs exactly one more).
    private int _state;

    /// <summary>
    /// Requests a refresh. Thread-safe. If none is queued, posts one to the UI thread; if one is already in flight,
    /// marks that exactly one more pass must follow so late changes are never dropped. Coalesces to ≤ 2 runs.
    /// </summary>
    public void Request()
    {
        while (true)
        {
            int state = Volatile.Read(ref _state);
            if (state == 0)
            {
                if (Interlocked.CompareExchange(ref _state, 1, 0) == 0)
                {
                    dispatcher.Post(RunAsync);
                    return;
                }
            }
            else if (Interlocked.CompareExchange(ref _state, 2, state) == state)
            {
                return; // a run is already in flight; it will do one more pass for this request
            }

            // Lost the race (state changed under us) — retry the read.
        }
    }

    private async void RunAsync()
    {
        while (true)
        {
            try
            {
                await refreshAsync();
            }
            catch (Exception ex)
            {
                logger?.Log(this, LogType.Error, $"UI refresh failed: {ex.Message}");
            }

            // One pass done. If nothing arrived meanwhile (state still 1) → go idle. If a request landed during the
            // run (state 2) → drop back to 1 and loop for exactly one more pass. Exactly one run is ever in flight,
            // so the delegate never overlaps itself.
            if (Interlocked.CompareExchange(ref _state, 0, 1) == 1)
            {
                return;
            }

            Interlocked.Exchange(ref _state, 1);
        }
    }
}
