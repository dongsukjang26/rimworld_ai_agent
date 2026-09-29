using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace AIAdvisor
{
    public class HttpResult
    {
        public long status;
        public string body;
        public string error;
        /// <summary>서버가 알려준 재시도 대기 시간(Retry-After 헤더, 초). 없으면 null.</summary>
        public string retryAfter;
        public bool Ok => status >= 200 && status < 300;
    }

    /// <summary>
    /// UnityWebRequest 기반 HTTP. Unity TLS 스택을 써서 Mono HttpClient 의 인증서 문제를 피한다.
    /// UnityWebRequest 는 메인 스레드에서만 다룰 수 있으므로 생성/상태확인을 MainThread 로 넘긴다.
    /// </summary>
    public static class Http
    {
        public const int TimeoutSeconds = 180;

        /// <param name="onChunk">있으면 응답을 받는 대로 조각(UTF-8 로 풀린 글자)을 넘긴다. 스트리밍(SSE) 응답용.</param>
        public static async Task<HttpResult> Send(string method, string url, Dictionary<string, string> headers, string jsonBody, CancellationToken ct, int timeoutSeconds = TimeoutSeconds, Action<string> onChunk = null)
        {
            var req = await MainThread.Invoke(() =>
            {
                var r = new UnityWebRequest(url, method);
                if (jsonBody != null)
                {
                    r.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(jsonBody)) { contentType = "application/json" };
                    r.SetRequestHeader("Content-Type", "application/json");
                }
                r.downloadHandler = onChunk != null ? new StreamingDownloadHandler(onChunk) : (DownloadHandler)new DownloadHandlerBuffer();
                r.timeout = timeoutSeconds;
                if (headers != null)
                    foreach (var kv in headers) r.SetRequestHeader(kv.Key, kv.Value);
                r.SendWebRequest();
                return r;
            }).ConfigureAwait(false);

            try
            {
                while (true)
                {
                    if (ct.IsCancellationRequested)
                    {
                        await MainThread.Invoke(() => { req.Abort(); return true; }).ConfigureAwait(false);
                        throw new OperationCanceledException(ct);
                    }
                    bool done = await MainThread.Invoke(() => req.isDone).ConfigureAwait(false);
                    if (done) break;
                    await Task.Delay(100).ConfigureAwait(false);
                }

                return await MainThread.Invoke(() => new HttpResult
                {
                    status = req.responseCode,
                    body = req.downloadHandler?.text ?? "",
                    error = req.result == UnityWebRequest.Result.Success ? null : req.error,
                    retryAfter = req.GetResponseHeader("retry-after"),
                }).ConfigureAwait(false);
            }
            finally
            {
                MainThread.Post(() => req.Dispose());
            }
        }

        /// <summary>
        /// 받은 바이트를 바로 글자로 풀어 넘기는 다운로드 핸들러. 한글처럼 여러 바이트인 글자가 조각 사이에서
        /// 잘려도 Decoder 가 다음 조각과 이어 붙인다. 전체 본문도 모아 두어 오류 메시지에 쓴다.
        /// </summary>
        class StreamingDownloadHandler : DownloadHandlerScript
        {
            readonly MemoryStream all = new MemoryStream();
            readonly Decoder decoder = Encoding.UTF8.GetDecoder();
            readonly Action<string> onChunk;

            public StreamingDownloadHandler(Action<string> onChunk) { this.onChunk = onChunk; }

            protected override bool ReceiveData(byte[] data, int dataLength)
            {
                if (data == null || dataLength <= 0) return true;
                all.Write(data, 0, dataLength);
                // UTF-8 은 바이트 수보다 글자가 많아질 수 없다 (+ 이전 조각에서 넘어온 최대 3바이트)
                var chars = new char[dataLength + 4];
                int n = decoder.GetChars(data, 0, dataLength, chars, 0);
                if (n > 0)
                {
                    try { onChunk(new string(chars, 0, n)); }
                    catch (Exception e) { Verse.Log.Warning("[RimSage] stream chunk failed: " + e); }
                }
                return true;
            }

            protected override byte[] GetData() => all.ToArray();

            protected override string GetText() => Encoding.UTF8.GetString(all.ToArray());
        }

        /// <summary>API 오류 응답에서 사람이 읽을 메시지를 뽑는다.</summary>
        public static string DescribeError(HttpResult r)
        {
            string msg = null;
            try
            {
                var node = Json.Parse(r.body);
                var err = node.Get("error");
                msg = err is string s ? s : err.Str("message");
                if (msg == null && node is List<object> list && list.Count > 0)
                    msg = list[0].Get("error").Str("message");
            }
            catch
            {
                // 본문이 JSON이 아니면 아래에서 원문 일부를 보여준다
            }
            if (string.IsNullOrEmpty(msg))
            {
                msg = string.IsNullOrEmpty(r.body) ? r.error : r.body;
                if (msg != null && msg.Length > 300) msg = msg.Substring(0, 300) + "…";
            }
            return r.status > 0 ? "HTTP " + r.status + ": " + msg : msg;
        }
    }
}
