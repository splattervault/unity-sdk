using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SplatterVault.Core;
using UnityEngine.Networking;

namespace SplatterVault.Players.Unity
{
    /// <summary>
    /// <see cref="ISvHttpTransport"/> on UnityWebRequest. Completion is awaited through the operation's
    /// <c>completed</c> event (no per-frame polling); a timeout uses <c>UnityWebRequest.timeout</c>; cancelling
    /// aborts the request. Every request body is sent as <c>application/json</c> (the API refuses anything
    /// else), for any method, DELETE included. Requests are started on Unity's main thread: a call from another
    /// thread is handed to it. Redirects are never followed (<c>redirectLimit = 0</c>): a redirect would send a body
    /// that may hold a password or a guest secret to another address. A 3xx comes back as an answer.
    /// <para>WebGL builds are not supported: browsers block the SplatterVault API (CORS).</para>
    /// </summary>
    public sealed class UnityWebRequestTransport : ISvHttpTransport
    {
        public UnityWebRequestTransport()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            throw new PlatformNotSupportedException(SvUnityPlatform.WebGlMessage);
#endif
        }

        public Task<SvHttpResponse> SendAsync(SvHttpRequest request, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (cancellationToken.IsCancellationRequested) return Task.FromCanceled<SvHttpResponse>(cancellationToken);
            var tcs = new TaskCompletionSource<SvHttpResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
            SvUnityMainThread.Run(() => Start(request, cancellationToken, tcs));
            return tcs.Task;
        }

        private static void Start(SvHttpRequest request, CancellationToken ct, TaskCompletionSource<SvHttpResponse> tcs)
        {
            if (ct.IsCancellationRequested)
            {
                tcs.TrySetCanceled(ct);
                return;
            }

            UnityWebRequest uwr = null;
            UnityWebRequestAsyncOperation operation;
            try
            {
                uwr = new UnityWebRequest(request.Url, request.Method) { downloadHandler = new DownloadHandlerBuffer() };
                if (request.Body != null)
                {
                    uwr.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(request.Body)) { contentType = "application/json" };
                }
                foreach (var header in request.Headers)
                {
                    // UnityWebRequest manages these itself.
                    if (string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                    uwr.SetRequestHeader(header.Key, header.Value);
                }
                if (request.Body != null) uwr.SetRequestHeader("Content-Type", "application/json");
                uwr.timeout = Math.Max(1, (int)Math.Ceiling(request.Timeout.TotalSeconds));
                uwr.redirectLimit = 0;
                operation = uwr.SendWebRequest();
            }
            catch (Exception e)
            {
                uwr?.Dispose();
                tcs.TrySetException(e);
                return;
            }

            var finished = false;
            var registration = default(CancellationTokenRegistration);
            if (ct.CanBeCanceled)
            {
                // Cancellation callbacks may run on any thread; Abort runs on the main thread.
                registration = ct.Register(() => SvUnityMainThread.Run(() =>
                {
                    if (!finished) uwr.Abort();
                }));
            }

            operation.completed += _ =>
            {
                if (finished) return;
                finished = true;
                registration.Dispose();
                try
                {
                    if (ct.IsCancellationRequested)
                    {
                        tcs.TrySetCanceled(ct);
                        return;
                    }
                    switch (uwr.result)
                    {
                        case UnityWebRequest.Result.Success:
                        case UnityWebRequest.Result.ProtocolError:
                            tcs.TrySetResult(new SvHttpResponse((int)uwr.responseCode, uwr.downloadHandler?.text, uwr.GetResponseHeaders()));
                            break;
                        default:
                            // A redirect stopped by redirectLimit = 0 is an answer (never followed), not a network failure.
                            if (uwr.responseCode >= 300 && uwr.responseCode < 400)
                            {
                                tcs.TrySetResult(new SvHttpResponse((int)uwr.responseCode, "", uwr.GetResponseHeaders()));
                                break;
                            }
                            var error = uwr.error ?? "request failed";
                            var timedOut = error.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                           error.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0;
                            tcs.TrySetException(new SvTransportException(timedOut ? SvTransportFailure.Timeout : SvTransportFailure.Network,
                                "UnityWebRequest: " + error));
                            break;
                    }
                }
                catch (Exception e)
                {
                    tcs.TrySetException(e);
                }
                finally
                {
                    uwr.Dispose();
                }
            };
        }
    }
}
