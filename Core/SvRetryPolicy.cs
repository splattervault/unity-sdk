using System;

namespace SplatterVault.Core
{
    /// <summary>
    /// When the SDK sends a request again by itself. It applies only to requests that are safe to repeat: reads
    /// (GET), store changes (they carry the same Idempotency-Key every time) and player-data writes whose every op
    /// has <c>ifVersion</c> (only after an answer that proves nothing was applied: 408, 429, 503); and a refresh,
    /// only within the API's 30-second resend window (a lost answer would otherwise end the sign-in). Sign-ins, email
    /// codes, new guests, account changes, sign-out, the Meta purchase check and data writes without <c>ifVersion</c>
    /// are never repeated by the SDK.
    /// <para>Only transient failures are repeated (no answer, 408, 429, 500, 502, 503, 504). A
    /// <c>Retry-After</c> is always honoured: the request waits at least that long, and when the wait is longer
    /// than <see cref="MaxRetryAfter"/> it is not repeated at all (the exception carries
    /// <see cref="SvApiException.RetryAfter"/> for the game to schedule).</para>
    /// <para>Every setter checks its value and throws <see cref="ArgumentOutOfRangeException"/> when it is out of range.</para>
    /// </summary>
    public sealed class SvRetryPolicy
    {
        /// <summary>The most sends one request may take (<see cref="MaxAttempts"/>).</summary>
        public const int MaxAttemptsLimit = 10;

        /// <summary>The longest wait any setting may name (2^31 - 1 ms, what timers take).</summary>
        public static readonly TimeSpan MaxWait = TimeSpan.FromMilliseconds(int.MaxValue);

        private readonly object _gate = new object();
        private readonly Random _random = new Random();
        private int _maxAttempts = 3;
        private TimeSpan _baseDelay = TimeSpan.FromMilliseconds(500);
        private TimeSpan _maxBackoff = TimeSpan.FromSeconds(4);
        private TimeSpan _maxRetryAfter = TimeSpan.FromSeconds(10);
        private double _jitter = 1.0;

        /// <summary>Sends in all, the first one included: 1 (retries off) to <see cref="MaxAttemptsLimit"/>.</summary>
        public int MaxAttempts
        {
            get => _maxAttempts;
            set
            {
                if (value < 1 || value > MaxAttemptsLimit)
                    throw new ArgumentOutOfRangeException(nameof(MaxAttempts), value, "MaxAttempts is 1 to " + MaxAttemptsLimit + ".");
                _maxAttempts = value;
            }
        }

        /// <summary>The wait before the first repeat when the answer named none; it doubles each time. 0 to <see cref="MaxWait"/>.</summary>
        public TimeSpan BaseDelay
        {
            get => _baseDelay;
            set => _baseDelay = CheckWait(value, nameof(BaseDelay));
        }

        /// <summary>The longest wait between repeats when the answer named none. 0 to <see cref="MaxWait"/>.</summary>
        public TimeSpan MaxBackoff
        {
            get => _maxBackoff;
            set => _maxBackoff = CheckWait(value, nameof(MaxBackoff));
        }

        /// <summary>The longest <c>Retry-After</c> the SDK waits out by itself. 0 to <see cref="MaxWait"/>.</summary>
        public TimeSpan MaxRetryAfter
        {
            get => _maxRetryAfter;
            set => _maxRetryAfter = CheckWait(value, nameof(MaxRetryAfter));
        }

        /// <summary>
        /// Spreads waits (0 = exact waits, 1 = anywhere from half to all of the wait), 0 to 1. Never shortens a
        /// <c>Retry-After</c>.
        /// </summary>
        public double Jitter
        {
            get => _jitter;
            set
            {
                if (double.IsNaN(value) || value < 0 || value > 1)
                    throw new ArgumentOutOfRangeException(nameof(Jitter), value, "Jitter is a number from 0 to 1.");
                _jitter = value;
            }
        }

        private static TimeSpan CheckWait(TimeSpan value, string name)
        {
            if (value < TimeSpan.Zero || value > MaxWait)
                throw new ArgumentOutOfRangeException(name, value, name + " is 0 to " + MaxWait.TotalMilliseconds + " ms.");
            return value;
        }

        /// <summary>No retries at all.</summary>
        public static SvRetryPolicy None => new SvRetryPolicy { MaxAttempts = 1 };

        /// <summary>
        /// Whether a request that failed with <paramref name="error"/> on send number <paramref name="attempt"/>
        /// (1 for the first) should be sent again, and after how long.
        /// </summary>
        public bool ShouldRetry(SvApiException error, int attempt, out TimeSpan delay)
        {
            delay = TimeSpan.Zero;
            if (error == null || attempt >= MaxAttempts || !error.IsTransient) return false;
            if (error.RetryAfter.HasValue)
            {
                if (error.RetryAfter.Value > MaxRetryAfter) return false;
                delay = error.RetryAfter.Value;
                return true;
            }
            delay = Backoff(attempt);
            return true;
        }

        /// <summary>The wait after send number <paramref name="attempt"/> when the answer named none.</summary>
        public TimeSpan Backoff(int attempt)
        {
            var ms = BaseDelay.TotalMilliseconds * Math.Pow(2, Math.Max(0, attempt - 1));
            ms = Math.Min(ms, MaxBackoff.TotalMilliseconds);
            var jitter = Math.Max(0, Math.Min(1, Jitter));
            if (jitter > 0)
            {
                double r;
                lock (_gate) r = _random.NextDouble();
                ms = ms * (1 - jitter / 2) + ms * (jitter / 2) * r;
            }
            return TimeSpan.FromMilliseconds(ms);
        }
    }
}
