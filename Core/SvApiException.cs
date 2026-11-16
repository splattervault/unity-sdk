using System;
using System.Globalization;
using Newtonsoft.Json.Linq;

namespace SplatterVault.Core
{
    /// <summary>
    /// A refusal from the SplatterVault API, or a request that got no answer.
    /// <para>Act on <see cref="ErrorCode"/> (the API's stable <c>error</c> code, such as <c>token_expired</c> or
    /// <c>insufficient_funds</c>), never on <see cref="Exception.Message"/>: the message is a plain sentence to log
    /// or show and may change.</para>
    /// <para>Two answer shapes are read: the studio services' <c>{ "error", "message", "details"? }</c>, and the
    /// framework's <c>{ "name", "message", "status", "errors" }</c> (an unknown route, for example), which has no
    /// code: <see cref="ErrorCode"/> is null and <see cref="ErrorName"/> holds <c>name</c>.</para>
    /// <para><see cref="Status"/> 0 means no HTTP answer came with this exception: either nothing was sent (the SDK
    /// refused the call itself) or no answer arrived. Codes made by the SDK itself are
    /// <see cref="NetworkErrorCode"/>, <see cref="TimeoutCode"/>, <see cref="NotSignedInCode"/> and
    /// <see cref="StorageUnreadableCode"/>; <c>token_expired</c> with status 0 is the SDK repeating the API's earlier
    /// answer for a token it already refused (nothing was sent). <see cref="InvalidResponseCode"/> keeps the HTTP
    /// status of an answer the SDK could not read.</para>
    /// </summary>
    public class SvApiException : Exception
    {
        /// <summary>No answer: no connection, a reset, a TLS failure. The request may or may not have arrived.</summary>
        public const string NetworkErrorCode = "network_error";
        /// <summary>No answer in time. The request may or may not have arrived.</summary>
        public const string TimeoutCode = "timeout";
        /// <summary>A player route was called with no signed-in player.</summary>
        public const string NotSignedInCode = "not_signed_in";
        /// <summary>The answer was not the JSON the SDK expected.</summary>
        public const string InvalidResponseCode = "invalid_response";
        /// <summary>
        /// The device's secure storage could not be read (a locked keychain, a value sealed on another device or with
        /// another key, a damaged file). Nothing was changed: in particular no new guest was made over the old one.
        /// Try again later; to give up on the old guest, call <c>CreateGuestAsync(replaceExistingGuest: true)</c>.
        /// </summary>
        public const string StorageUnreadableCode = "storage_unreadable";
        /// <summary>
        /// A store change was not sent because another player is signed in now than the one who made it (status 0,
        /// nothing sent). It stays pending for its own player.
        /// </summary>
        public const string PlayerChangedCode = "player_changed";
        /// <summary>
        /// A data write with <c>ifVersion</c> that the SDK had already sent once was answered <c>version_conflict</c>
        /// (status 409): an earlier send may have been the one that applied it. <see cref="IsOutcomeUnknown"/> is true;
        /// <see cref="Details"/> holds the current versions. Read the data again before doing anything else.
        /// </summary>
        public const string OutcomeUnknownCode = "outcome_unknown";
        /// <summary>
        /// A store change with this Idempotency-Key and player is already being sent, with a different request (status 0,
        /// nothing sent). <see cref="IsOutcomeUnknown"/> is true: that send may still apply it. Wait for it, then check.
        /// </summary>
        public const string ChangeInProgressCode = "change_in_progress";
        /// <summary>
        /// A sign-in the game asked for was answered, but a later sign-in or a sign-out happened while it was being saved
        /// (status 0): it was not kept (its refresh token was ended). The later one stands.
        /// </summary>
        public const string SupersededCode = "superseded";

        public SvApiException(int status, string errorCode, string message, JToken details = null,
            TimeSpan? retryAfter = null, string errorName = null, JToken errors = null, Exception inner = null)
            : base(string.IsNullOrEmpty(message) ? DefaultMessage(status, errorCode) : message, inner)
        {
            Status = status;
            ErrorCode = errorCode;
            Details = details;
            RetryAfter = retryAfter;
            ErrorName = errorName;
            Errors = errors;
        }

        /// <summary>HTTP status; 0 when no answer arrived or the SDK refused the call itself.</summary>
        public int Status { get; }

        /// <summary>The API's <c>error</c> code, an SDK code (status 0), or null (framework-shaped answer).</summary>
        public string ErrorCode { get; }

        /// <summary><c>details</c> of the refusal (for example the current versions of a <c>version_conflict</c>), or null.</summary>
        public JToken Details { get; }

        /// <summary>The framework answer's <c>name</c> (such as <c>NOT_FOUND</c>), or null.</summary>
        public string ErrorName { get; }

        /// <summary>The framework answer's <c>errors</c>, or null.</summary>
        public JToken Errors { get; }

        /// <summary>The answer's <c>Retry-After</c>: do not send this request again sooner.</summary>
        public TimeSpan? RetryAfter { get; }

        /// <summary>No answer arrived (network failure or timeout).</summary>
        public bool IsNetworkError => Status == 0 && (ErrorCode == NetworkErrorCode || ErrorCode == TimeoutCode);

        /// <summary>
        /// The same request may succeed later: no answer arrived, or the API answered 408, 429, 500, 502, 503 or
        /// 504. Whether the SDK sends it again by itself depends on the request (see the README's retry rules).
        /// </summary>
        public bool IsTransient => IsNetworkError || IsTransientStatus(Status);

        /// <summary>True when <see cref="ErrorCode"/> is <paramref name="code"/>.</summary>
        public bool Is(string code) => ErrorCode != null && string.Equals(ErrorCode, code, StringComparison.Ordinal);

        /// <summary>
        /// The change may or may not have been applied: a write got no answer (network error, timeout) or a 500, 502
        /// or 504, or a repeat of it got a conflict an earlier send may have caused. Do not just send it again. A data
        /// write: read the data again (when the key's version moved and the value is the one you wrote, it applied).
        /// A store change: it stays pending; finish it with <c>ResumePendingAsync</c>, or send it again with
        /// <see cref="IdempotencyKey"/>. Always false for reads and for refusals that changed nothing.
        /// </summary>
        public bool IsOutcomeUnknown { get; internal set; }

        /// <summary>
        /// A store change (purchase, consume) that failed but is still kept on this device to be finished: by
        /// <c>ResumePendingAsync</c>, or by sending it again with <see cref="IdempotencyKey"/>. True after no answer, after
        /// 408, 429, 500, 502, 503, 504, after token problems (401) and when another player signed in first; false when it
        /// was refused (nothing changed) or could not be kept. While it is true, do not offer to buy again: offer "finish".
        /// </summary>
        public bool IsPending { get; internal set; }

        /// <summary>
        /// The Idempotency-Key of the store change that failed (purchases and consumes), else null. The same change sent
        /// again with this key (and the same arguments) is applied at most once; a new key is a new change.
        /// </summary>
        public string IdempotencyKey { get; internal set; }

        /// <summary><see cref="Details"/> as <typeparamref name="T"/>, or default when there are none.</summary>
        public T DetailsAs<T>() => SvJson.ToObject<T>(Details);

        public static bool IsTransientStatus(int status)
        {
            return status == 408 || status == 429 || status == 500 || status == 502 || status == 503 || status == 504;
        }

        private static string DefaultMessage(int status, string code)
        {
            if (code == NetworkErrorCode) return "No answer from SplatterVault (network error).";
            if (code == TimeoutCode) return "No answer from SplatterVault in time.";
            if (code == NotSignedInCode) return "No player is signed in.";
            if (code == StorageUnreadableCode) return "The device's secure storage could not be read. Nothing was changed.";
            if (code == PlayerChangedCode) return "Another player is signed in now; the change was not sent and stays pending for its player.";
            if (code == OutcomeUnknownCode) return "The write may have been applied by an earlier send. Read the data again.";
            if (code == SupersededCode) return "A later sign-in or a sign-out happened while this sign-in was saved; it was not kept.";
            if (code == ChangeInProgressCode) return "A change with this Idempotency-Key is already being sent; nothing was sent again.";
            if (!string.IsNullOrEmpty(code)) return code;
            return status > 0 ? "HTTP " + status.ToString(CultureInfo.InvariantCulture) : "Request failed.";
        }

        /// <summary>The exception for a non-success answer. Reads both answer shapes and <c>Retry-After</c>.</summary>
        public static SvApiException FromResponse(SvHttpResponse response, DateTimeOffset now)
        {
            if (response == null) throw new ArgumentNullException(nameof(response));
            var retryAfter = ParseRetryAfter(response.GetHeader("Retry-After"), now);
            string code = null, message = null, name = null;
            JToken details = null, errors = null;
            JObject body = null;
            if (!string.IsNullOrWhiteSpace(response.Body))
            {
                try
                {
                    body = SvJson.Parse(response.Body) as JObject;
                }
                catch (Exception)
                {
                    body = null; // an HTML error page from a proxy, for example: never quoted into the message
                }
            }

            if (body != null)
            {
                if (body["error"] is JValue e && e.Type == JTokenType.String) code = (string)e;
                if (body["message"] is JValue m && m.Type == JTokenType.String) message = (string)m;
                var d = body["details"];
                if (d != null && d.Type != JTokenType.Null) details = d;
                if (code == null)
                {
                    if (body["name"] is JValue n && n.Type == JTokenType.String) name = (string)n;
                    var errs = body["errors"];
                    if (errs != null && errs.Type != JTokenType.Null) errors = errs;
                }
            }

            if (code == null && response.Status >= 300 && response.Status < 400)
                message = "The API answered with a redirect (HTTP " + response.Status.ToString(CultureInfo.InvariantCulture) +
                          "). The SDK never follows redirects (a player token or secret must not travel to another address): check BaseUrl.";
            return new SvApiException(response.Status, code, message, details, retryAfter, name, errors);
        }

        /// <summary>The SDK's exception for a transport failure (no answer).</summary>
        public static SvApiException FromTransport(SvTransportException failure)
        {
            var code = failure.Failure == SvTransportFailure.Timeout ? TimeoutCode : NetworkErrorCode;
            return new SvApiException(0, code, null, inner: failure);
        }

        /// <summary><c>Retry-After</c> as a wait: whole seconds, or an HTTP date. Null when absent or unreadable.</summary>
        public static TimeSpan? ParseRetryAfter(string header, DateTimeOffset now)
        {
            if (string.IsNullOrWhiteSpace(header)) return null;
            var text = header.Trim();
            if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds))
                return TimeSpan.FromSeconds(Math.Min(seconds, 86400));
            if (DateTimeOffset.TryParseExact(text, "r", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AllowWhiteSpaces, out var at))
            {
                var wait = at - now;
                return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
            }
            return null;
        }
    }

    /// <summary>
    /// The caller stopped waiting for a store change (its cancellation token fired). The change itself was not stopped:
    /// its send runs to the end and is kept until answered, so it may still be applied. It is an
    /// <see cref="OperationCanceledException"/> (catch that as usual) that also says which change it was.
    /// </summary>
    public sealed class SvChangeCanceledException : OperationCanceledException
    {
        public SvChangeCanceledException(string idempotencyKey, bool isPending, System.Threading.CancellationToken token)
            : base("Stopped waiting for the store change; it is still being sent and may be applied.", token)
        {
            IdempotencyKey = idempotencyKey;
            IsPending = isPending;
        }

        /// <summary>The change's Idempotency-Key (send it again only with this key, or resume it).</summary>
        public string IdempotencyKey { get; }

        /// <summary>The change is kept to be finished (by its send still running, or by <c>ResumePendingAsync</c>).</summary>
        public bool IsPending { get; }
    }
}
