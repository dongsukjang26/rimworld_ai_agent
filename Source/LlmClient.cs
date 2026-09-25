using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace AIAdvisor
{
    public class ToolSpec
    {
        public string name;
        public string description;
        public Dictionary<string, object> schema;
    }

    public class ToolCall
    {
        public string id;
        public string name;
        public Dictionary<string, object> args;
        public string parseError;
    }

    public class ToolOutput
    {
        public ToolCall call;
        public string content;
        public bool isError;
    }

    /// <summary>회사별 usage 필드를 하나로 맞춘 토큰 사용량.</summary>
    public class Usage
    {
        public long inputUncached;
        public long cacheRead;
        public long cacheWrite;
        public long output;
        /// <summary>서버에서 실행된 웹 검색 횟수. 토큰과 별도로 과금된다.</summary>
        public int webSearches;
        /// <summary>API가 직접 알려준 비용(OpenRouter). 없으면 null.</summary>
        public double? reportedCost;

        public long TotalInput => inputUncached + cacheRead + cacheWrite;

        public void Add(Usage u)
        {
            inputUncached += u.inputUncached;
            cacheRead += u.cacheRead;
            cacheWrite += u.cacheWrite;
            output += u.output;
            webSearches += u.webSearches;
            if (u.reportedCost.HasValue) reportedCost = (reportedCost ?? 0) + u.reportedCost.Value;
        }

        /// <summary>달러 비용. 가격을 모르면 null.</summary>
        public double? Cost(ModelPrice price, double webSearchPerCall = 0)
        {
            if (reportedCost.HasValue) return reportedCost;
            if (price == null || !price.Known) return null;
            double cached = price.cachedInputPerM >= 0 ? price.cachedInputPerM : price.inputPerM;
            return (inputUncached * (double)price.inputPerM
                    + cacheRead * cached
                    + cacheWrite * price.inputPerM * 1.25
                    + output * (double)price.outputPerM) / 1_000_000.0
                   + webSearches * webSearchPerCall;
        }
    }

    public class LlmReply
    {
        /// <summary>대화 기록에 그대로 다시 붙일 회사별 assistant 메시지(들).</summary>
        public List<object> historyItems = new List<object>();
        public string text = "";
        public List<ToolCall> toolCalls = new List<ToolCall>();
        public Usage usage = new Usage();
        public string stopReason;
        public bool refused;
        public bool truncated;
        /// <summary>서버 도구 실행이 끝나지 않은 채 멈춘 턴. 기록에 남기면 다음 요청이 거부된다.</summary>
        public bool unfinished;
        /// <summary>이번 응답에서 실행된 웹 검색어.</summary>
        public List<string> webSearchQueries = new List<string>();
        /// <summary>답변이 인용한 웹 출처 (제목, URL).</summary>
        public List<KeyValuePair<string, string>> sources = new List<KeyValuePair<string, string>>();
        /// <summary>이 모델/계정에서 웹 검색이 거부돼서 검색 없이 다시 보냈을 때의 오류 메시지.</summary>
        public string webSearchRejected;
    }

    public class LlmException : Exception
    {
        public LlmException(string msg) : base(msg) { }
    }

    public abstract class LlmClient
    {
        protected readonly string apiKey;
        protected readonly string model;

        /// <summary>true 면 회사 서버의 웹 검색 도구를 같이 보낸다 (Anthropic / OpenAI 만 지원).</summary>
        public bool webSearch;

        public virtual bool SupportsWebSearch => false;

        public bool WebSearchActive => webSearch && SupportsWebSearch && !webSearchRejected.Contains(model ?? "");

        /// <summary>웹 검색 도구를 거부한 모델 (게임을 끌 때까지 기억). 계정에서 꺼져 있거나 모델이 지원하지 않는 경우.</summary>
        static readonly HashSet<string> webSearchRejected = new HashSet<string>();

        protected LlmClient(string apiKey, string model)
        {
            this.apiKey = (apiKey ?? "").Trim();
            this.model = model;
        }

        /// <summary>
        /// 웹 검색을 켠 요청을 보내고, 웹 검색 도구 때문에 400 이 나면 검색 없이 한 번 더 보낸다.
        /// (예: gpt-4.1-nano, gpt-5 의 minimal 추론, 조직 설정에서 웹 검색을 끈 Anthropic 계정)
        /// </summary>
        protected async Task<object> PostWithWebSearchFallback(string url, Dictionary<string, string> headers, Func<bool, object> buildBody, LlmReply reply, CancellationToken ct, int timeoutSeconds = Http.TimeoutSeconds)
        {
            bool search = WebSearchActive;
            try
            {
                return await PostJson(url, headers, buildBody(search), ct, timeoutSeconds).ConfigureAwait(false);
            }
            catch (LlmException e) when (search && IsWebSearchRejection(e.Message))
            {
                lock (webSearchRejected) webSearchRejected.Add(model ?? "");
                reply.webSearchRejected = e.Message;
                return await PostJson(url, headers, buildBody(false), ct, timeoutSeconds).ConfigureAwait(false);
            }
        }

        static bool IsWebSearchRejection(string msg)
        {
            if (msg == null || !msg.StartsWith("HTTP 400")) return false;
            return msg.IndexOf("web_search", StringComparison.OrdinalIgnoreCase) >= 0
                   || msg.IndexOf("web search", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        protected static void AddSource(LlmReply reply, string title, string url)
        {
            if (string.IsNullOrEmpty(url) || reply.sources.Any(s => s.Value == url)) return;
            reply.sources.Add(new KeyValuePair<string, string>(string.IsNullOrEmpty(title) ? url : title, url));
        }

        public abstract object UserMessage(string text);
        public abstract IEnumerable<object> ToolResultMessages(List<ToolOutput> outputs);
        public abstract Task<LlmReply> Complete(string system, List<object> history, List<ToolSpec> tools, CancellationToken ct);
        public abstract Task<List<ModelPrice>> ListModels(CancellationToken ct);

        public static LlmClient Create(ProviderKind provider, string apiKey, string model)
        {
            switch (provider)
            {
                case ProviderKind.Anthropic: return new AnthropicClient(apiKey, model);
                case ProviderKind.OpenAI:
                    return new OpenAIResponsesClient(apiKey, model);
                case ProviderKind.Gemini:
                    return new OpenAICompatClient(provider, apiKey, model, "https://generativelanguage.googleapis.com/v1beta/openai/");
                case ProviderKind.OpenRouter:
                    return new OpenAICompatClient(provider, apiKey, model, "https://openrouter.ai/api/v1/");
                case ProviderKind.Local:
                    return new OpenAICompatClient(provider, "", model, NormalizeBaseUrl(AdvisorMod.Settings.localBaseUrl));
                default: throw new LlmException("AIAdvisor_UnknownKey".TranslateSafe());
            }
        }

        /// <summary>"localhost:11434" 처럼 입력해도 되도록 http:// 와 끝의 / 를 채워 준다.</summary>
        public static string NormalizeBaseUrl(string url)
        {
            url = (url ?? "").Trim();
            if (url.Length == 0) url = AdvisorSettings.OllamaUrl;
            if (!url.StartsWith("http://") && !url.StartsWith("https://")) url = "http://" + url;
            if (!url.EndsWith("/")) url += "/";
            return url;
        }

        /// <summary>재시도할 때 UI 에 알릴 메시지 (대기 초, 시도 번호, 최대 횟수, 이유).</summary>
        public static Action<int, int, int, string> RetryNotice;

        const int MaxRetries = 3;
        static readonly int[] BackoffSeconds = { 3, 8, 20 };

        protected static async Task<object> PostJson(string url, Dictionary<string, string> headers, object body, CancellationToken ct, int timeoutSeconds = Http.TimeoutSeconds)
        {
            string json = Json.Serialize(body);
            for (int attempt = 0; ; attempt++)
            {
                var r = await Http.Send("POST", url, headers, json, ct, timeoutSeconds).ConfigureAwait(false);
                if (r.Ok) return Json.Parse(r.body);
                if (attempt >= MaxRetries || !IsTransient(r)) throw new LlmException(Http.DescribeError(r));

                // 요청이 몰리거나(429) 서버가 잠깐 불안정할 때(5xx)는 조금 기다렸다가 다시 보낸다
                int wait = BackoffSeconds[attempt];
                if (int.TryParse(r.retryAfter, out int serverWait) && serverWait > 0) wait = Math.Min(serverWait, 60);
                RetryNotice?.Invoke(wait, attempt + 1, MaxRetries, r.status > 0 ? "HTTP " + r.status : (r.error ?? "network"));
                await Task.Delay(TimeSpan.FromSeconds(wait), ct).ConfigureAwait(false);
            }
        }

        static bool IsTransient(HttpResult r)
        {
            // 크레딧 부족은 기다려도 해결되지 않는다
            if (r.body != null && (r.body.Contains("insufficient_quota") || r.body.Contains("credit balance"))) return false;
            switch (r.status)
            {
                case 408:
                case 429:
                case 500:
                case 502:
                case 503:
                case 504:
                case 529: // Anthropic overloaded
                    return true;
                case 0:
                    // 연결 실패는 재시도하되, 이미 오래 기다린 시간 초과는 다시 기다리지 않는다
                    return r.error == null || r.error.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) < 0;
                default:
                    return false;
            }
        }

        protected static async Task<object> GetJson(string url, Dictionary<string, string> headers, CancellationToken ct, int timeoutSeconds = Http.TimeoutSeconds)
        {
            var r = await Http.Send("GET", url, headers, null, ct, timeoutSeconds).ConfigureAwait(false);
            if (!r.Ok) throw new LlmException(Http.DescribeError(r));
            return Json.Parse(r.body);
        }

        protected static Dictionary<string, object> ParseArgs(object raw, out string error)
        {
            error = null;
            if (raw is Dictionary<string, object> d) return d;
            if (raw is string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return new Dictionary<string, object>();
                try
                {
                    if (Json.Parse(s) is Dictionary<string, object> parsed) return parsed;
                }
                catch (Exception e)
                {
                    error = "Invalid JSON arguments: " + e.Message;
                }
            }
            return new Dictionary<string, object>();
        }
    }

    /// <summary>Anthropic Messages API (POST /v1/messages).</summary>
    public class AnthropicClient : LlmClient
    {
        const string Base = "https://api.anthropic.com/v1/";

        public AnthropicClient(string apiKey, string model) : base(apiKey, model) { }

        Dictionary<string, string> Headers(bool fallback)
        {
            var h = new Dictionary<string, string>
            {
                { "x-api-key", apiKey },
                { "anthropic-version", "2023-06-01" },
            };
            if (fallback) h["anthropic-beta"] = "server-side-fallback-2026-07-01";
            return h;
        }

        public override object UserMessage(string text)
        {
            return new Dictionary<string, object> { { "role", "user" }, { "content", text } };
        }

        public override IEnumerable<object> ToolResultMessages(List<ToolOutput> outputs)
        {
            // 병렬 도구 호출 결과는 반드시 하나의 user 메시지에 모아서 보낸다
            var blocks = outputs.Select(o =>
            {
                var b = new Dictionary<string, object>
                {
                    { "type", "tool_result" },
                    { "tool_use_id", o.call.id },
                    { "content", o.content },
                };
                if (o.isError) b["is_error"] = true;
                return (object)b;
            }).ToList();
            yield return new Dictionary<string, object> { { "role", "user" }, { "content", blocks } };
        }

        public override bool SupportsWebSearch => true;

        /// <summary>질문 하나(요청 하나)에서 허용할 서버 웹 검색 횟수.</summary>
        const int MaxWebSearchesPerRequest = 5;
        /// <summary>서버 도구 루프가 pause_turn 으로 멈췄을 때 이어서 보낼 최대 횟수.</summary>
        const int MaxPauseContinuations = 4;

        public override async Task<LlmReply> Complete(string system, List<object> history, List<ToolSpec> tools, CancellationToken ct)
        {
            var def = Providers.FindDef(ProviderKind.Anthropic, model);
            bool fallback = def != null && def.serverFallback;
            string effort = AdvisorMod.Settings.EffectiveEffort(ProviderKind.Anthropic, model);

            var reply = new LlmReply();
            // pause_turn 이면 멈춘 assistant 내용을 그대로 붙여 다시 보내고, 이어진 내용은 같은 assistant 메시지에 합친다
            var content = new List<object>();
            for (int attempt = 0; ; attempt++)
            {
                var messages = new List<object>(history);
                if (content.Count > 0)
                    messages.Add(new Dictionary<string, object> { { "role", "assistant" }, { "content", new List<object>(content) } });

                var resp = await PostWithWebSearchFallback(Base + "messages", Headers(fallback),
                    search => BuildBody(system, messages, tools, search, effort, fallback), reply, ct).ConfigureAwait(false);

                content.AddRange(resp.Arr("content"));
                reply.stopReason = resp.Str("stop_reason");
                var u = resp.Get("usage");
                reply.usage.Add(new Usage
                {
                    inputUncached = (long)u.Num("input_tokens"),
                    cacheRead = (long)u.Num("cache_read_input_tokens"),
                    cacheWrite = (long)u.Num("cache_creation_input_tokens"),
                    output = (long)u.Num("output_tokens"),
                    webSearches = (int)u.Get("server_tool_use").Num("web_search_requests"),
                });
                if (reply.stopReason != "pause_turn" || attempt >= MaxPauseContinuations) break;
            }

            reply.historyItems.Add(new Dictionary<string, object> { { "role", "assistant" }, { "content", content } });
            var text = new System.Text.StringBuilder();
            bool prevWasText = false;
            foreach (var block in content)
            {
                string type = block.Str("type");
                if (type == "text")
                {
                    // 인용이 있으면 문장 중간에서 text 블록이 나뉘므로 붙어 있는 블록은 그대로 이어 붙인다
                    if (!prevWasText && text.Length > 0) text.Append("\n\n");
                    text.Append(block.Str("text"));
                    if (block.Get("citations") is List<object> cites)
                        foreach (var c in cites) AddSource(reply, c.Str("title"), c.Str("url"));
                    prevWasText = true;
                    continue;
                }
                prevWasText = false;
                if (type == "tool_use")
                {
                    var args = ParseArgs(block.Get("input"), out var err);
                    reply.toolCalls.Add(new ToolCall { id = block.Str("id"), name = block.Str("name"), args = args, parseError = err });
                }
                else if (type == "server_tool_use" && block.Str("name") == "web_search")
                {
                    string q = block.Get("input").Str("query");
                    if (!string.IsNullOrEmpty(q)) reply.webSearchQueries.Add(q);
                }
            }
            reply.text = text.ToString().Trim();
            reply.refused = reply.stopReason == "refusal";
            reply.truncated = reply.stopReason == "max_tokens";
            reply.unfinished = reply.stopReason == "pause_turn";
            return reply;
        }

        Dictionary<string, object> BuildBody(string system, List<object> messages, List<ToolSpec> tools, bool webSearch, string effort, bool fallback)
        {
            var body = new Dictionary<string, object>
            {
                { "model", model },
                { "max_tokens", 16000 },
                { "system", system },
                { "messages", messages },
                // 자동 prompt caching: 도구 루프에서 매번 다시 보내는 앞부분을 캐시해서 비용을 줄인다
                { "cache_control", new Dictionary<string, object> { { "type", "ephemeral" } } },
            };
            var toolList = tools.Select(t => (object)new Dictionary<string, object>
            {
                { "name", t.name },
                { "description", t.description },
                { "input_schema", t.schema },
            }).ToList();
            if (webSearch)
            {
                // Anthropic 서버에서 실행되는 웹 검색. 기본 버전은 모든 Claude 모델(Haiku 4.5 포함)에서 쓸 수 있다.
                toolList.Add(new Dictionary<string, object>
                {
                    { "type", "web_search_20250305" },
                    { "name", "web_search" },
                    { "max_uses", MaxWebSearchesPerRequest },
                });
            }
            if (toolList.Count > 0) body["tools"] = toolList;
            if (!string.IsNullOrEmpty(effort))
                body["output_config"] = new Dictionary<string, object> { { "effort", effort } };
            if (fallback) body["fallbacks"] = "default";
            return body;
        }

        public override async Task<List<ModelPrice>> ListModels(CancellationToken ct)
        {
            var resp = await GetJson(Base + "models?limit=100", Headers(false), ct).ConfigureAwait(false);
            return resp.Arr("data").Select(m => new ModelPrice
            {
                modelId = m.Str("id"),
                label = m.Str("display_name") ?? m.Str("id"),
            }).Where(m => !string.IsNullOrEmpty(m.modelId)).ToList();
        }
    }

    /// <summary>OpenAI Chat Completions 형식. OpenAI / Gemini(호환 엔드포인트) / OpenRouter 가 같이 쓴다.</summary>
    public class OpenAICompatClient : LlmClient
    {
        protected readonly ProviderKind provider;
        protected readonly string baseUrl;

        public OpenAICompatClient(ProviderKind provider, string apiKey, string model, string baseUrl) : base(apiKey, model)
        {
            this.provider = provider;
            this.baseUrl = baseUrl;
        }

        protected Dictionary<string, string> Headers()
        {
            var h = new Dictionary<string, string> { { "Authorization", "Bearer " + (apiKey.Length > 0 ? apiKey : "local") } };
            if (provider == ProviderKind.OpenRouter) h["X-Title"] = "RimWorld AI Advisor";
            return h;
        }

        public override object UserMessage(string text)
        {
            return new Dictionary<string, object> { { "role", "user" }, { "content", text } };
        }

        public override IEnumerable<object> ToolResultMessages(List<ToolOutput> outputs)
        {
            foreach (var o in outputs)
            {
                yield return new Dictionary<string, object>
                {
                    { "role", "tool" },
                    { "tool_call_id", o.call.id },
                    { "content", o.isError ? "ERROR: " + o.content : o.content },
                };
            }
        }

        public override async Task<LlmReply> Complete(string system, List<object> history, List<ToolSpec> tools, CancellationToken ct)
        {
            var messages = new List<object> { new Dictionary<string, object> { { "role", "system" }, { "content", system } } };
            messages.AddRange(history);
            var body = new Dictionary<string, object>
            {
                { "model", model },
                { "messages", messages },
            };
            if (tools.Count > 0)
            {
                body["tools"] = tools.Select(t => (object)new Dictionary<string, object>
                {
                    { "type", "function" },
                    { "function", new Dictionary<string, object>
                        {
                            { "name", t.name },
                            { "description", t.description },
                            { "parameters", t.schema },
                        }
                    },
                }).ToList();
            }
            if (provider == ProviderKind.OpenRouter)
                body["usage"] = new Dictionary<string, object> { { "include", true } };
            string effort = AdvisorMod.Settings.EffectiveEffort(provider, model);
            if (!string.IsNullOrEmpty(effort))
            {
                if (provider == ProviderKind.OpenRouter) body["reasoning"] = new Dictionary<string, object> { { "effort", effort } };
                else body["reasoning_effort"] = effort;
            }

            // 로컬 모델은 PC 사양에 따라 매우 느릴 수 있어서 시간 제한을 넉넉히 준다
            int timeout = provider == ProviderKind.Local ? 900 : Http.TimeoutSeconds;
            var resp = await PostJson(baseUrl + "chat/completions", Headers(), body, ct, timeout).ConfigureAwait(false);

            var choice = resp.Arr("choices").FirstOrDefault();
            if (choice == null) throw new LlmException("Empty response (no choices)");
            var msg = choice.Get("message");
            var reply = new LlmReply { stopReason = choice.Str("finish_reason") };
            reply.text = (msg.Get("content") as string ?? "").Trim();

            var rawCalls = msg.Get("tool_calls") as List<object>;
            // 기록에 남길 assistant 메시지. tool_calls 는 원본 그대로 두어야
            // Gemini thought signature 같은 추가 필드가 보존된다.
            var assistant = new Dictionary<string, object> { { "role", "assistant" }, { "content", msg.Get("content") as string } };
            if (rawCalls != null && rawCalls.Count > 0)
            {
                int n = 0;
                foreach (var tc in rawCalls)
                {
                    if (tc is Dictionary<string, object> tcd && string.IsNullOrEmpty(tcd.Str("id")))
                        tcd["id"] = "call_" + (n++) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    var fn = tc.Get("function");
                    var args = ParseArgs(fn.Get("arguments"), out var err);
                    reply.toolCalls.Add(new ToolCall { id = tc.Str("id"), name = fn.Str("name"), args = args, parseError = err });
                }
                assistant["tool_calls"] = rawCalls;
            }
            reply.historyItems.Add(assistant);
            reply.refused = msg.Get("refusal") is string r && r.Length > 0;
            if (reply.refused && reply.text.Length == 0) reply.text = (string)msg.Get("refusal");
            reply.truncated = reply.stopReason == "length";

            var u = resp.Get("usage");
            long prompt = (long)u.Num("prompt_tokens");
            long cached = (long)u.Get("prompt_tokens_details").Num("cached_tokens");
            reply.usage = new Usage
            {
                inputUncached = Math.Max(0, prompt - cached),
                cacheRead = cached,
                output = (long)u.Num("completion_tokens"),
            };
            if (u.Get("cost") is double cost) reply.usage.reportedCost = cost;
            return reply;
        }

        public override async Task<List<ModelPrice>> ListModels(CancellationToken ct)
        {
            var resp = await GetJson(baseUrl + "models", Headers(), ct).ConfigureAwait(false);
            var result = new List<ModelPrice>();
            foreach (var m in resp.Arr("data"))
            {
                string id = m.Str("id");
                if (string.IsNullOrEmpty(id)) continue;
                if (id.StartsWith("models/")) id = id.Substring("models/".Length);
                if (!IsChatModel(id, m)) continue;
                var price = new ModelPrice { modelId = id, label = m.Str("name") ?? id };
                var pricing = m.Get("pricing");
                if (pricing != null)
                {
                    // OpenRouter 는 토큰당 달러 가격을 문자열로 준다
                    price.inputPerM = PerMillion(pricing.Str("prompt"));
                    price.outputPerM = PerMillion(pricing.Str("completion"));
                    price.cachedInputPerM = PerMillion(pricing.Str("input_cache_read"));
                }
                // OpenRouter 는 모델별로 받는 추론 강도 값도 알려준다
                if (m.Get("reasoning").Get("supported_efforts") is List<object> efforts)
                {
                    price.efforts = Providers.EffortOrder.Where(e => efforts.Any(x => x as string == e)).ToList();
                }
                result.Add(price);
            }
            return result.OrderBy(p => p.modelId).ToList();
        }

        static float PerMillion(string perToken)
        {
            if (perToken != null && double.TryParse(perToken, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v >= 0)
                return (float)(v * 1_000_000.0);
            return -1f;
        }

        bool IsChatModel(string id, object m)
        {
            string lower = id.ToLowerInvariant();
            string[] excluded = { "embed", "audio", "realtime", "image", "tts", "transcribe", "whisper", "dall-e", "moderation", "search", "instruct", "live", "veo", "imagen", "codex", "sora" };
            if (excluded.Any(lower.Contains)) return false;
            switch (provider)
            {
                case ProviderKind.OpenAI:
                    return lower.StartsWith("gpt-") || lower.StartsWith("chatgpt") || (lower.Length > 1 && lower[0] == 'o' && char.IsDigit(lower[1]));
                case ProviderKind.Gemini:
                    return lower.Contains("gemini");
                case ProviderKind.OpenRouter:
                    // 도구 호출을 지원하는 모델만
                    return m.Arr("supported_parameters").Any(p => p as string == "tools");
                case ProviderKind.Local:
                    return true;
                default:
                    return true;
            }
        }
    }

    /// <summary>
    /// OpenAI Responses API (POST /v1/responses). 최신 추론 모델은 Chat Completions 에서
    /// 도구 + reasoning 을 같이 쓸 수 없어서 OpenAI 는 이쪽을 쓴다.
    /// store=false 로 서버에 대화를 남기지 않고, 추론 항목은 암호화된 채로 받아 다시 보낸다.
    /// </summary>
    public class OpenAIResponsesClient : OpenAICompatClient
    {
        public OpenAIResponsesClient(string apiKey, string model)
            : base(ProviderKind.OpenAI, apiKey, model, "https://api.openai.com/v1/") { }

        public override IEnumerable<object> ToolResultMessages(List<ToolOutput> outputs)
        {
            foreach (var o in outputs)
            {
                yield return new Dictionary<string, object>
                {
                    { "type", "function_call_output" },
                    { "call_id", o.call.id },
                    { "output", o.isError ? "ERROR: " + o.content : o.content },
                };
            }
        }

        public override bool SupportsWebSearch => true;

        public override async Task<LlmReply> Complete(string system, List<object> history, List<ToolSpec> tools, CancellationToken ct)
        {
            string effort = AdvisorMod.Settings.EffectiveEffort(ProviderKind.OpenAI, model);
            Dictionary<string, object> BuildBody(bool webSearch)
            {
                var body = new Dictionary<string, object>
                {
                    { "model", model },
                    { "instructions", system },
                    { "input", history },
                    { "store", false },
                    { "include", new List<object> { "reasoning.encrypted_content" } },
                };
                var toolList = tools.Select(t => (object)new Dictionary<string, object>
                {
                    { "type", "function" },
                    { "name", t.name },
                    { "description", t.description },
                    { "parameters", t.schema },
                }).ToList();
                // OpenAI 서버에서 실행되는 웹 검색. 결과는 web_search_call 항목과 url_citation 주석으로 돌아온다.
                if (webSearch) toolList.Add(new Dictionary<string, object> { { "type", "web_search" } });
                if (toolList.Count > 0) body["tools"] = toolList;
                if (!string.IsNullOrEmpty(effort))
                    body["reasoning"] = new Dictionary<string, object> { { "effort", effort } };
                return body;
            }

            var reply = new LlmReply();
            var resp = await PostWithWebSearchFallback(baseUrl + "responses", Headers(), BuildBody, reply, ct).ConfigureAwait(false);

            reply.stopReason = resp.Str("status");
            var texts = new List<string>();
            int searches = 0;
            foreach (var item in resp.Arr("output"))
            {
                // 추론/메시지/함수 호출/웹 검색 항목을 전부 원본 그대로 다음 요청에 다시 보낸다
                reply.historyItems.Add(item);
                switch (item.Str("type"))
                {
                    case "message":
                        foreach (var part in item.Arr("content"))
                        {
                            string pt = part.Str("type");
                            if (pt == "output_text")
                            {
                                texts.Add(part.Str("text"));
                                foreach (var a in part.Arr("annotations"))
                                    if (a.Str("type") == "url_citation") AddSource(reply, a.Str("title"), a.Str("url"));
                            }
                            else if (pt == "refusal")
                            {
                                reply.refused = true;
                                texts.Add(part.Str("refusal"));
                            }
                        }
                        break;
                    case "function_call":
                        var args = ParseArgs(item.Get("arguments"), out var err);
                        reply.toolCalls.Add(new ToolCall { id = item.Str("call_id"), name = item.Str("name"), args = args, parseError = err });
                        break;
                    case "web_search_call":
                        var action = item.Get("action");
                        // 페이지 열기/페이지 내 찾기는 검색 요금이 붙지 않는다
                        if (action.Str("type") == "search")
                        {
                            searches++;
                            string q = action.Str("query");
                            if (!string.IsNullOrEmpty(q)) reply.webSearchQueries.Add(q);
                        }
                        break;
                }
            }
            reply.text = string.Join("\n", texts).Trim();
            reply.truncated = reply.stopReason == "incomplete";

            var u = resp.Get("usage");
            long input = (long)u.Num("input_tokens");
            long cached = (long)u.Get("input_tokens_details").Num("cached_tokens");
            reply.usage = new Usage
            {
                inputUncached = Math.Max(0, input - cached),
                cacheRead = cached,
                output = (long)u.Num("output_tokens"),
                webSearches = searches,
            };
            return reply;
        }
    }
}
