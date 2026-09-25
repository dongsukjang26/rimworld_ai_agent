using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using Verse;

namespace AIAdvisor
{
    /// <summary>
    /// 외부 AI 도구(Claude Code, Codex CLI, Gemini CLI 등)가 게임 데이터 도구를 쓸 수 있게 하는
    /// 로컬 MCP 서버 (Streamable HTTP, JSON 응답만 사용). 127.0.0.1 에만 열린다.
    /// 사용자는 본인 구독으로 쓰는 공식 앱에 이 서버를 등록하므로 이 모드가 API 를 호출하지 않는다.
    /// </summary>
    public static class McpServer
    {
        public const string Path = "/mcp";
        const string ServerName = "rimworld-ai-advisor";
        const string ServerVersion = "1.0.0";
        const string DefaultProtocolVersion = "2025-06-18";

        static HttpListener listener;
        static Thread thread;
        static int requestCount;

        public static bool Running => listener != null && listener.IsListening;
        public static int Port { get; private set; }
        public static string LastError { get; private set; }
        public static int RequestCount => requestCount;

        public static string Url(int port) => "http://127.0.0.1:" + port + Path;

        public static void Start(int port)
        {
            Stop();
            try
            {
                var l = new HttpListener();
                l.Prefixes.Add("http://127.0.0.1:" + port + "/");
                l.Start();
                listener = l;
                Port = port;
                LastError = null;
                thread = new Thread(() => Loop(l)) { IsBackground = true, Name = "AIAdvisor MCP" };
                thread.Start();
                KeepRunningInBackground();
                Log.Message("[AI Advisor] MCP server listening on " + Url(port));
            }
            catch (Exception e)
            {
                listener = null;
                LastError = e.Message;
                Log.Warning("[AI Advisor] MCP server failed to start on port " + port + ": " + e.Message);
            }
        }

        /// <summary>
        /// 사용자가 터미널(AI 앱)로 가면 게임 창이 포커스를 잃는다. "백그라운드에서 실행" 옵션이 꺼져 있으면
        /// Unity 가 프레임을 멈춰서 도구 요청을 처리할 수 없으므로, MCP 서버가 켜져 있는 동안은 계속 돌게 한다.
        /// 메인 스레드에서 호출해야 한다.
        /// </summary>
        public static void KeepRunningInBackground()
        {
            if (Running && !UnityEngine.Application.runInBackground) UnityEngine.Application.runInBackground = true;
        }

        public static void Stop()
        {
            var l = listener;
            listener = null;
            if (l == null) return;
            // 원래 게임 옵션으로 되돌린다
            try { UnityEngine.Application.runInBackground = Prefs.RunInBackground; }
            catch
            {
                // 종료 중에는 무시
            }
            try { l.Stop(); l.Close(); }
            catch
            {
                // 이미 닫힌 경우
            }
        }

        static void Loop(HttpListener l)
        {
            while (l.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = l.GetContext(); }
                catch { break; } // Stop() 하면 여기서 빠져나온다
                ThreadPool.QueueUserWorkItem(_ => SafeHandle(ctx));
            }
        }

        static void SafeHandle(HttpListenerContext ctx)
        {
            try { Handle(ctx); }
            catch (Exception e)
            {
                try { Respond(ctx, 500, RpcError(null, -32603, e.Message)); }
                catch
                {
                    // 연결이 이미 끊긴 경우
                }
            }
        }

        static void Handle(HttpListenerContext ctx)
        {
            var req = ctx.Request;

            // DNS rebinding 방지: 브라우저가 보낸 다른 사이트의 요청은 거절
            string origin = req.Headers["Origin"];
            if (!string.IsNullOrEmpty(origin) && !IsLocalOrigin(origin))
            {
                Respond(ctx, 403, RpcError(null, -32600, "Forbidden origin"));
                return;
            }
            if (req.Url.AbsolutePath.TrimEnd('/') != Path)
            {
                Respond(ctx, 404, null);
                return;
            }
            if (req.HttpMethod == "GET")
            {
                // 서버에서 먼저 보내는 SSE 스트림은 제공하지 않는다
                Respond(ctx, 405, null);
                return;
            }
            if (req.HttpMethod == "DELETE")
            {
                Respond(ctx, 200, null);
                return;
            }
            if (req.HttpMethod != "POST")
            {
                Respond(ctx, 405, null);
                return;
            }

            string body;
            using (var reader = new StreamReader(req.InputStream, Encoding.UTF8)) body = reader.ReadToEnd();
            object msg;
            try { msg = Json.Parse(body); }
            catch (Exception e)
            {
                Respond(ctx, 400, RpcError(null, -32700, "Parse error: " + e.Message));
                return;
            }

            Interlocked.Increment(ref requestCount);
            if (msg is List<object> batch)
            {
                var responses = batch.Select(HandleMessage).Where(r => r != null).ToList();
                if (responses.Count == 0) Respond(ctx, 202, null);
                else Respond(ctx, 200, responses);
                return;
            }
            var response = HandleMessage(msg);
            if (response == null) Respond(ctx, 202, null); // 알림(notification)에는 본문 없이 202
            else Respond(ctx, 200, response);
        }

        static bool IsLocalOrigin(string origin)
        {
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
            return uri.Host == "127.0.0.1" || uri.Host == "localhost" || uri.Host == "[::1]" || uri.Host == "::1";
        }

        /// <summary>JSON-RPC 메시지 하나 처리. 알림이면 null.</summary>
        static object HandleMessage(object msg)
        {
            object id = msg.Get("id");
            string method = msg.Str("method");
            if (method == null) return null; // 클라이언트가 보낸 응답 등은 무시
            bool isNotification = !(msg is Dictionary<string, object> d && d.ContainsKey("id"));
            if (isNotification) return null;

            try
            {
                switch (method)
                {
                    case "initialize":
                        return RpcResult(id, Initialize(msg.Get("params")));
                    case "ping":
                        return RpcResult(id, new Dictionary<string, object>());
                    case "tools/list":
                        return RpcResult(id, new Dictionary<string, object> { { "tools", ToolList() } });
                    case "tools/call":
                        return RpcResult(id, CallTool(msg.Get("params")));
                    default:
                        return RpcError(id, -32601, "Method not found: " + method);
                }
            }
            catch (Exception e)
            {
                return RpcError(id, -32603, e.Message);
            }
        }

        static object Initialize(object prms)
        {
            string requested = prms.Str("protocolVersion");
            return new Dictionary<string, object>
            {
                { "protocolVersion", string.IsNullOrEmpty(requested) ? DefaultProtocolVersion : requested },
                { "capabilities", new Dictionary<string, object> { { "tools", new Dictionary<string, object> { { "listChanged", false } } } } },
                { "serverInfo", new Dictionary<string, object> { { "name", ServerName }, { "version", ServerVersion } } },
                { "instructions", Instructions },
            };
        }

        const string Instructions =
            "Live data from the player's running RimWorld game. Use these tools to read the actual colony state before giving advice; " +
            "never invent colonist names or numbers. Call get_colony_overview first for general questions, and call several tools when needed. " +
            "Give concrete, prioritized advice and reply in the language the player uses. Data is only available while a map is loaded in the game.";

        static List<object> ToolList()
        {
            return GameTools.Specs.Select(t => (object)new Dictionary<string, object>
            {
                { "name", t.name },
                { "description", t.description },
                { "inputSchema", t.schema },
                { "annotations", new Dictionary<string, object> { { "readOnlyHint", t.name != "focus_camera" } } },
            }).ToList();
        }

        static object CallTool(object prms)
        {
            string name = prms.Str("name");
            var args = prms.Get("arguments") as Dictionary<string, object> ?? new Dictionary<string, object>();
            ToolOutput output;
            if (Current.ProgramState != ProgramState.Playing || Current.Game == null)
            {
                output = new ToolOutput { isError = true, content = "RimWorld has no game loaded right now. Ask the player to load a save or start a colony first." };
            }
            else
            {
                // 게임 객체는 메인 스레드에서만 읽을 수 있다
                var call = new ToolCall { id = "mcp", name = name, args = args };
                var task = MainThread.Invoke(() => GameTools.Execute(call));
                output = task.Wait(TimeSpan.FromSeconds(30))
                    ? task.Result
                    : new ToolOutput { isError = true, content = "The game did not respond in time (it may be loading or minimized)." };
            }
            return new Dictionary<string, object>
            {
                { "content", new List<object> { new Dictionary<string, object> { { "type", "text" }, { "text", output.content } } } },
                { "isError", output.isError },
            };
        }

        static Dictionary<string, object> RpcResult(object id, object result) =>
            new Dictionary<string, object> { { "jsonrpc", "2.0" }, { "id", id }, { "result", result } };

        static Dictionary<string, object> RpcError(object id, int code, string message) =>
            new Dictionary<string, object>
            {
                { "jsonrpc", "2.0" },
                { "id", id },
                { "error", new Dictionary<string, object> { { "code", code }, { "message", message } } },
            };

        static void Respond(HttpListenerContext ctx, int status, object json)
        {
            var resp = ctx.Response;
            resp.StatusCode = status;
            if (json != null)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(Json.Serialize(json));
                resp.ContentType = "application/json; charset=utf-8";
                resp.ContentLength64 = bytes.Length;
                resp.OutputStream.Write(bytes, 0, bytes.Length);
            }
            resp.Close();
        }
    }
}
