using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SplatterVault.Core
{
    /// <summary>
    /// One HTTP request, as the SDK hands it to an <see cref="ISvHttpTransport"/>. <see cref="Body"/> is JSON text
    /// (sent as <c>application/json</c>) or null for no body.
    /// </summary>
    public sealed class SvHttpRequest
    {
        public string Method { get; set; }
        public string Url { get; set; }
        public Dictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Body { get; set; }
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
    }

    /// <summary>An HTTP answer, whatever its status (a 4xx or 5xx is an answer, not a transport failure).</summary>
    public sealed class SvHttpResponse
    {
        private readonly Dictionary<string, string> _headers;

        public SvHttpResponse(int status, string body, IDictionary<string, string> headers = null)
        {
            Status = status;
            Body = body;
            _headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (headers != null)
                foreach (var kv in headers)
                    if (kv.Key != null) _headers[kv.Key] = kv.Value;
        }

        public int Status { get; }
        public string Body { get; }
        public IReadOnlyDictionary<string, string> Headers => _headers;

        /// <summary>The header's value, or null (names are case-insensitive).</summary>
        public string GetHeader(string name)
        {
            return name != null && _headers.TryGetValue(name, out var v) ? v : null;
        }

        public bool IsSuccess => Status >= 200 && Status < 300;
    }

    /// <summary>
    /// Sends HTTP requests. The Unity package provides one built on UnityWebRequest; tests and other hosts plug in
    /// their own.
    /// <para>Contract: return an <see cref="SvHttpResponse"/> for every HTTP answer (any status). Throw
    /// <see cref="SvTransportException"/> when no answer arrived (network failure, timeout) and
    /// <see cref="OperationCanceledException"/> when the cancellation token was cancelled.</para>
    /// </summary>
    public interface ISvHttpTransport
    {
        Task<SvHttpResponse> SendAsync(SvHttpRequest request, CancellationToken cancellationToken);
    }

    public enum SvTransportFailure
    {
        /// <summary>No connection, DNS failure, connection reset, TLS failure, or an answer that could not be read.</summary>
        Network,
        /// <summary>No answer within the request's timeout.</summary>
        Timeout
    }

    /// <summary>No HTTP answer arrived. The request may or may not have reached the server.</summary>
    public sealed class SvTransportException : Exception
    {
        public SvTransportException(SvTransportFailure failure, string message, Exception inner = null)
            : base(message, inner)
        {
            Failure = failure;
        }

        public SvTransportFailure Failure { get; }
    }
}
