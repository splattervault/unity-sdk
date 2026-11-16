using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using SplatterVault.Core;

namespace SplatterVault.Players
{
    /// <summary>Whether the SDK may send a request again by itself (see <see cref="SvRetryPolicy"/>).</summary>
    internal enum SvRetry
    {
        /// <summary>Never: a sign-in proof, an email code, a new guest, an account change, a data write without ifVersion.</summary>
        Never,
        /// <summary>Repeating it cannot do anything twice.</summary>
        Safe,
        /// <summary>
        /// A data write whose every op has <c>ifVersion</c> (no Idempotency-Key): sent again only after an answer that
        /// proves nothing was applied (408, 429, 503; the 401 <c>token_expired</c> renewal sends it once more too).
        /// After no answer, 500, 502 or 504 the outcome is unknown and it is never re-sent.
        /// </summary>
        IfNotApplied
    }

    internal sealed class SvCall
    {
        public string Method;
        /// <summary>Path below the base URL, starting with "/", query included.</summary>
        public string Path;
        public JToken Body;
        public SvRetry Retry;
        /// <summary>Stop repeating once this long has passed since the first send (a refresh's resend window).</summary>
        public TimeSpan? RetryWindow;
        public bool Allow304;
        public Dictionary<string, string> Headers;
        /// <summary>This call's timeout per send (default: the client's request timeout).</summary>
        public TimeSpan? Timeout;
        /// <summary>
        /// How many times this call went out, counted across every <see cref="SvPipeline.SendAsync"/> of it (the 401
        /// renewal re-send included): a conflict after more than one send may come from an earlier send.
        /// </summary>
        public int Sends;
    }

    /// <summary>Sends calls: headers, body, timeout, error answers, and the retry rules. Holds no credential.</summary>
    internal sealed class SvPipeline
    {
        private readonly string _baseUrl;
        private readonly ISvHttpTransport _transport;
        private readonly SvRetryPolicy _retry;
        private readonly TimeSpan _timeout;
        private readonly Func<DateTimeOffset> _clock;
        private readonly Func<TimeSpan> _mono;
        private readonly Func<TimeSpan, CancellationToken, Task> _delay;
        private readonly SvLogger _log;

        internal SvPipeline(string baseUrl, ISvHttpTransport transport, SvRetryPolicy retry, TimeSpan timeout,
            Func<DateTimeOffset> clock, Func<TimeSpan> mono, Func<TimeSpan, CancellationToken, Task> delay, SvLogger log)
        {
            _mono = mono;
            _baseUrl = baseUrl;
            _transport = transport;
            _retry = retry ?? new SvRetryPolicy();
            _timeout = timeout;
            _clock = clock;
            _delay = delay;
            _log = log;
        }

        /// <summary>Wall clock: only where it is compared with the API's times (Retry-After dates, Date headers).</summary>
        internal DateTimeOffset Now => _clock();

        /// <summary>Monotonic time (a time sync never moves it): resend windows and elapsed times.</summary>
        internal TimeSpan Mono => _mono();

        internal SvLogger Log => _log;

        /// <summary>
        /// Sends <paramref name="call"/> with <paramref name="bearer"/> (a player token) or no credential, repeating
        /// it under the retry rules. Returns a 2xx answer (or a 304 when allowed); throws <see cref="SvApiException"/>.
        /// </summary>
        internal async Task<SvHttpResponse> SendAsync(SvCall call, string bearer, CancellationToken ct)
        {
            // Monotonic: a device time sync in the middle of a refresh can neither stretch nor cut its resend window.
            var started = _mono();
            var attempt = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                attempt++;
                call.Sends++;
                SvApiException failure;
                try
                {
                    var response = await _transport.SendAsync(BuildRequest(call, bearer), ct);
                    if (response.IsSuccess || (call.Allow304 && response.Status == 304)) return response;
                    failure = SvApiException.FromResponse(response, _clock());
                }
                catch (SvTransportException e)
                {
                    failure = SvApiException.FromTransport(e);
                }

                var mayRepeat = call.Retry == SvRetry.Safe || (call.Retry == SvRetry.IfNotApplied && ProvesNotApplied(failure));
                if (!mayRepeat || !_retry.ShouldRetry(failure, attempt, out var wait)) throw Settle(call, failure);
                if (call.RetryWindow.HasValue && (_mono() - started) + wait >= call.RetryWindow.Value) throw Settle(call, failure);
                SvLog.Write(_log, SvLogLevel.Debug, call.Method + " " + PathOnly(call.Path) + " failed (" +
                                                   (failure.ErrorCode ?? failure.Status.ToString()) + "); sending it again in " +
                                                   Math.Round(wait.TotalSeconds, 1) + " s");
                await _delay(wait, ct);
            }
        }

        /// <summary>An answer that proves the write was not applied: 408, 429, 503.</summary>
        internal static bool ProvesNotApplied(SvApiException failure)
        {
            return failure.Status == 408 || failure.Status == 429 || failure.Status == 503;
        }

        private static bool IsRead(SvCall call)
        {
            return call.Method == "GET" || call.Method == "HEAD";
        }

        /// <summary>
        /// The exception the caller gets: flagged <see cref="SvApiException.IsOutcomeUnknown"/> when a write may have
        /// been applied (no answer, 500, 502, 504), or when a repeated send got a conflict an earlier send may have
        /// caused (an <c>ifVersion</c> write answered <c>version_conflict</c>: reported as <c>outcome_unknown</c>, never as
        /// a plain conflict; a keyed store change answered <c>idempotency_conflict</c>).
        /// </summary>
        internal static SvApiException Settle(SvCall call, SvApiException failure)
        {
            if (IsRead(call)) return failure;
            if (call.Sends > 1 && failure.Status == 409)
            {
                if (call.Retry == SvRetry.IfNotApplied && failure.Is("version_conflict"))
                {
                    return new SvApiException(409, SvApiException.OutcomeUnknownCode,
                        "The write may have been applied by an earlier send (a repeat of it was answered version_conflict). Read the data again.",
                        failure.Details, failure.RetryAfter, inner: failure) { IsOutcomeUnknown = true };
                }
                if (failure.Is("idempotency_conflict") && call.Headers != null && call.Headers.ContainsKey("Idempotency-Key"))
                {
                    failure.IsOutcomeUnknown = true;
                    return failure;
                }
            }
            if (failure.IsNetworkError || failure.Status == 500 || failure.Status == 502 || failure.Status == 504)
                failure.IsOutcomeUnknown = true;
            return failure;
        }

        private SvHttpRequest BuildRequest(SvCall call, string bearer)
        {
            var request = new SvHttpRequest
            {
                Method = call.Method,
                Url = _baseUrl + call.Path,
                Timeout = call.Timeout.HasValue && call.Timeout.Value > TimeSpan.Zero ? call.Timeout.Value : _timeout
            };
            request.Headers["Accept"] = "application/json";
            if (call.Body != null)
            {
                request.Body = SvJson.Serialize(call.Body);
                // The API refuses any body that is not application/json.
                request.Headers["Content-Type"] = "application/json";
            }
            if (bearer != null) request.Headers["Authorization"] = "Bearer " + bearer;
            if (call.Headers != null)
                foreach (var kv in call.Headers)
                    request.Headers[kv.Key] = kv.Value;
            // A player client never sends an API key, whatever a caller put in the headers.
            request.Headers.Remove("X-API-Key");
            return request;
        }

        internal static string PathOnly(string path)
        {
            if (path == null) return "";
            var q = path.IndexOf('?');
            return q < 0 ? path : path.Substring(0, q);
        }

        /// <summary>Reads a 2xx answer's body as <typeparamref name="T"/>; an unreadable body is invalid_response.</summary>
        internal static T Read<T>(SvHttpResponse response) where T : class
        {
            if (string.IsNullOrWhiteSpace(response.Body))
                throw new SvApiException(response.Status, SvApiException.InvalidResponseCode, "The answer had no body.");
            try
            {
                var value = SvJson.Deserialize<T>(response.Body);
                if (value == null) throw new SvApiException(response.Status, SvApiException.InvalidResponseCode, "The answer was empty.");
                return value;
            }
            catch (SvApiException)
            {
                throw;
            }
            catch (Exception e)
            {
                // Only the exception's type: a JSON reader's message can quote a value of the answer (a token).
                throw new SvApiException(response.Status, SvApiException.InvalidResponseCode,
                    "The answer could not be read (" + e.GetType().Name + ").");
            }
        }
    }

    /// <summary>One operation in flight at a time: callers that arrive while it runs share its result.</summary>
    internal sealed class SvSingleFlight<T>
    {
        private readonly object _gate = new object();
        private Task<T> _running;

        internal bool IsRunning
        {
            get
            {
                lock (_gate) return _running != null;
            }
        }

        /// <summary>Runs <paramref name="operation"/> unless one is already running, and awaits it (cancellable per caller).</summary>
        internal Task<T> RunAsync(Func<Task<T>> operation, CancellationToken ct)
        {
            TaskCompletionSource<T> mine = null;
            Task<T> shared;
            lock (_gate)
            {
                if (_running == null)
                {
                    mine = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
                    _running = mine.Task;
                }
                shared = _running;
            }
            if (mine != null) _ = Drive(operation, mine);
            return SvTasks.WithCancellation(shared, ct);
        }

        private async Task Drive(Func<Task<T>> operation, TaskCompletionSource<T> tcs)
        {
            try
            {
                var result = await operation();
                Clear();
                tcs.TrySetResult(result);
            }
            catch (OperationCanceledException e)
            {
                Clear();
                tcs.TrySetCanceled(e.CancellationToken);
            }
            catch (Exception e)
            {
                Clear();
                tcs.TrySetException(e);
            }
        }

        private void Clear()
        {
            lock (_gate) _running = null;
        }
    }

    internal static class SvTasks
    {
        /// <summary>Awaits <paramref name="task"/>, but gives up (OperationCanceledException) when <paramref name="ct"/> fires.</summary>
        internal static async Task<T> WithCancellation<T>(Task<T> task, CancellationToken ct)
        {
            if (!ct.CanBeCanceled || task.IsCompleted) return await task;
            var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(() => cancelled.TrySetResult(true)))
            {
                if (await Task.WhenAny(task, cancelled.Task) != task)
                {
                    ObserveLater(task);
                    throw new OperationCanceledException(ct);
                }
            }
            return await task;
        }

        private static void ObserveLater(Task task)
        {
            task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
        }
    }
}
