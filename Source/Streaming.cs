using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AIAdvisor
{
    /// <summary>
    /// 스트리밍(SSE) 응답을 받아서, 스트리밍을 쓰지 않았을 때와 같은 모양의 응답 JSON 으로 다시 조립한다.
    /// 그래서 응답을 읽는 코드는 스트리밍 여부와 상관없이 하나로 유지된다. 답변 글자는 받는 대로 TextDelta 로 넘긴다.
    /// </summary>
    public abstract class StreamDecoder
    {
        /// <summary>새로 받은 답변 글자. 메인 스레드에서 불릴 수 있으니 가볍게 처리해야 한다.</summary>
        public Action<string> TextDelta;

        /// <summary>스트림 안에서 받은 오류 (HTTP 상태는 200). 없으면 null.</summary>
        public string Error;
        /// <summary>다시 보내면 나아질 수 있는 오류인지 (서버 과부하, 연결 끊김 등).</summary>
        public bool ErrorTransient;
        /// <summary>답변 내용을 하나라도 받았는지. 받은 뒤에 끊기면 같은 답을 두 번 보여 주지 않도록 다시 보내지 않는다.</summary>
        public bool SawOutput;
        /// <summary>받은 SSE 이벤트 수. 0 이면 서버가 stream 옵션을 무시하고 보통 JSON 으로 답한 것이다.</summary>
        public int Events;

        readonly StringBuilder buffer = new StringBuilder();
        readonly StringBuilder data = new StringBuilder();
        string eventName;
        bool hasData;

        public void Reset()
        {
            buffer.Clear();
            data.Clear();
            eventName = null;
            hasData = false;
            Error = null;
            ErrorTransient = false;
            SawOutput = false;
            Events = 0;
            ResetState();
        }

        public void Feed(string chunk)
        {
            buffer.Append(chunk);
            string s = buffer.ToString();
            int start = 0, nl;
            while ((nl = s.IndexOf('\n', start)) >= 0)
            {
                int end = nl > start && s[nl - 1] == '\r' ? nl - 1 : nl;
                Line(s.Substring(start, end - start));
                start = nl + 1;
            }
            buffer.Remove(0, start);
        }

        /// <summary>응답이 끝났을 때 호출. 남은 줄을 처리하고, 끝을 알리는 이벤트 없이 끊겼으면 오류로 둔다.</summary>
        public void Finish()
        {
            if (buffer.Length > 0)
            {
                Line(buffer.ToString());
                buffer.Clear();
            }
            Dispatch();
            if (Error == null && !Complete)
            {
                Error = "The streamed response ended before it was complete.";
                ErrorTransient = true;
            }
        }

        void Line(string line)
        {
            if (line.Length == 0)
            {
                Dispatch();
                return;
            }
            if (line[0] == ':') return; // 주석 (연결 유지용)
            int colon = line.IndexOf(':');
            string field = colon < 0 ? line : line.Substring(0, colon);
            string value = colon < 0 ? "" : line.Substring(colon + 1);
            if (value.StartsWith(" ")) value = value.Substring(1);
            if (field == "event") eventName = value;
            else if (field == "data")
            {
                if (hasData) data.Append('\n');
                data.Append(value);
                hasData = true;
            }
        }

        void Dispatch()
        {
            if (!hasData)
            {
                eventName = null;
                return;
            }
            string ev = eventName, d = data.ToString();
            data.Clear();
            hasData = false;
            eventName = null;
            Events++;
            try
            {
                OnEvent(ev, d);
            }
            catch (Exception e)
            {
                if (Error == null) Error = "Could not read the streamed response: " + e.Message;
            }
        }

        protected void EmitText(string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            SawOutput = true;
            TextDelta?.Invoke(text);
        }

        protected static Dictionary<string, object> Copy(object node)
        {
            return node is Dictionary<string, object> d ? new Dictionary<string, object>(d) : new Dictionary<string, object>();
        }

        protected static void Append(Dictionary<string, object> d, string key, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            d[key] = (d.Get(key) as string ?? "") + text;
        }

        protected abstract void ResetState();
        protected abstract void OnEvent(string ev, string data);
        /// <summary>끝을 알리는 이벤트까지 받았는지.</summary>
        protected abstract bool Complete { get; }
        /// <summary>스트리밍 없이 받았을 때와 같은 모양의 응답.</summary>
        public abstract object Result();
    }

    /// <summary>Anthropic Messages API 스트림 → message 객체 (content / stop_reason / usage).</summary>
    public class AnthropicStreamDecoder : StreamDecoder
    {
        readonly SortedDictionary<int, Dictionary<string, object>> blocks = new SortedDictionary<int, Dictionary<string, object>>();
        readonly Dictionary<int, StringBuilder> inputJson = new Dictionary<int, StringBuilder>();
        Dictionary<string, object> usage;
        string stopReason;
        bool stopped;

        protected override void ResetState()
        {
            blocks.Clear();
            inputJson.Clear();
            usage = null;
            stopReason = null;
            stopped = false;
        }

        protected override bool Complete => stopped;

        protected override void OnEvent(string ev, string data)
        {
            var o = Json.Parse(data);
            int index = (int)o.Num("index", -1);
            blocks.TryGetValue(index, out var block);
            switch (o.Str("type"))
            {
                case "message_start":
                    usage = Copy(o.Get("message").Get("usage"));
                    break;
                case "content_block_start":
                    block = Copy(o.Get("content_block"));
                    blocks[index] = block;
                    // 도구 입력은 JSON 조각으로 나눠서 오므로 모았다가 블록이 끝날 때 파싱한다
                    if (block.ContainsKey("input")) inputJson[index] = new StringBuilder();
                    SawOutput = true;
                    break;
                case "content_block_delta":
                    if (block == null) break;
                    var d = o.Get("delta");
                    switch (d.Str("type"))
                    {
                        case "text_delta":
                            Append(block, "text", d.Str("text"));
                            EmitText(d.Str("text"));
                            break;
                        case "input_json_delta":
                            if (inputJson.TryGetValue(index, out var sb)) sb.Append(d.Str("partial_json"));
                            break;
                        case "thinking_delta":
                            Append(block, "thinking", d.Str("thinking"));
                            break;
                        case "signature_delta":
                            block["signature"] = d.Str("signature");
                            break;
                        case "citations_delta":
                            if (!(block.Get("citations") is List<object> cites)) block["citations"] = cites = new List<object>();
                            cites.Add(d.Get("citation"));
                            break;
                    }
                    break;
                case "content_block_stop":
                    if (block != null && inputJson.TryGetValue(index, out var json))
                    {
                        string js = json.ToString();
                        block["input"] = string.IsNullOrWhiteSpace(js) ? new Dictionary<string, object>() : Json.Parse(js);
                        inputJson.Remove(index);
                    }
                    break;
                case "message_delta":
                    stopReason = o.Get("delta").Str("stop_reason") ?? stopReason;
                    // message_delta 의 usage 는 누적값이라 그대로 덮어쓴다
                    if (o.Get("usage") is Dictionary<string, object> u)
                    {
                        if (usage == null) usage = new Dictionary<string, object>();
                        foreach (var kv in u) usage[kv.Key] = kv.Value;
                    }
                    break;
                case "message_stop":
                    stopped = true;
                    break;
                case "error":
                    var err = o.Get("error");
                    string type = err.Str("type") ?? "";
                    Error = (type.Length > 0 ? type + ": " : "") + (err.Str("message") ?? data);
                    ErrorTransient = type == "overloaded_error" || type == "api_error" || type == "rate_limit_error";
                    break;
            }
        }

        public override object Result()
        {
            return new Dictionary<string, object>
            {
                { "content", blocks.Values.Cast<object>().ToList() },
                { "stop_reason", stopReason },
                { "usage", usage ?? new Dictionary<string, object>() },
            };
        }
    }

    /// <summary>OpenAI Responses API 스트림. 마지막 response.completed 이벤트에 전체 응답이 들어 있다.</summary>
    public class ResponsesStreamDecoder : StreamDecoder
    {
        object final;

        protected override void ResetState() => final = null;

        protected override bool Complete => final != null;

        protected override void OnEvent(string ev, string data)
        {
            var o = Json.Parse(data);
            switch (o.Str("type"))
            {
                case "response.output_text.delta":
                    EmitText(o.Str("delta"));
                    break;
                case "response.output_item.added":
                    SawOutput = true;
                    break;
                case "response.completed":
                case "response.incomplete":
                    final = o.Get("response");
                    break;
                case "response.failed":
                    final = o.Get("response");
                    Error = final.Get("error").Str("message") ?? "The response failed.";
                    ErrorTransient = true;
                    break;
                case "error":
                    Error = o.Str("message") ?? o.Get("error").Str("message") ?? data;
                    ErrorTransient = true;
                    break;
            }
        }

        public override object Result() => final;
    }

    /// <summary>Chat Completions 스트림 (OpenAI 호환: Gemini, OpenRouter, Ollama, LM Studio) → choices[0].message 형태.</summary>
    public class ChatStreamDecoder : StreamDecoder
    {
        readonly StringBuilder text = new StringBuilder();
        readonly StringBuilder refusal = new StringBuilder();
        readonly List<Dictionary<string, object>> calls = new List<Dictionary<string, object>>();
        readonly Dictionary<int, Dictionary<string, object>> callsByIndex = new Dictionary<int, Dictionary<string, object>>();
        string finishReason;
        object usage;
        bool done;

        protected override void ResetState()
        {
            text.Clear();
            refusal.Clear();
            calls.Clear();
            callsByIndex.Clear();
            finishReason = null;
            usage = null;
            done = false;
        }

        protected override bool Complete => done || finishReason != null;

        protected override void OnEvent(string ev, string data)
        {
            if (data.Trim() == "[DONE]")
            {
                done = true;
                return;
            }
            var o = Json.Parse(data);
            if (o.Get("error") != null)
            {
                var err = o.Get("error");
                Error = err as string ?? err.Str("message") ?? data;
                ErrorTransient = true;
                return;
            }
            if (o.Get("usage") is Dictionary<string, object> u) usage = u;
            foreach (var choice in o.Arr("choices"))
            {
                var delta = choice.Get("delta");
                if (delta.Get("content") is string c && c.Length > 0)
                {
                    text.Append(c);
                    EmitText(c);
                }
                if (delta.Get("refusal") is string r) refusal.Append(r);
                foreach (var tc in delta.Arr("tool_calls")) MergeToolCall(tc as Dictionary<string, object>);
                if (choice.Str("finish_reason") is string f && f.Length > 0) finishReason = f;
            }
        }

        /// <summary>
        /// 도구 호출은 index 로 묶여 조각으로 온다 (arguments 는 이어 붙임). Gemini 의 thought signature 같은
        /// 추가 필드는 그대로 남겨야 다음 요청에서 거부되지 않는다.
        /// </summary>
        void MergeToolCall(Dictionary<string, object> tc)
        {
            if (tc == null) return;
            SawOutput = true;
            int index = (int)tc.Num("index", -1);
            string id = tc.Str("id");
            Dictionary<string, object> target = null;
            if (index >= 0) callsByIndex.TryGetValue(index, out target);
            else if (calls.Count > 0 && (string.IsNullOrEmpty(id) || calls[calls.Count - 1].Str("id") == id)) target = calls[calls.Count - 1];
            if (target == null)
            {
                target = new Dictionary<string, object>
                {
                    { "type", "function" },
                    { "function", new Dictionary<string, object> { { "name", "" }, { "arguments", "" } } },
                };
                calls.Add(target);
                if (index >= 0) callsByIndex[index] = target;
            }
            foreach (var kv in tc)
            {
                switch (kv.Key)
                {
                    case "index":
                        break; // 스트리밍용 필드라 기록에 남기지 않는다
                    case "function":
                        var fn = (Dictionary<string, object>)target["function"];
                        string name = kv.Value.Str("name");
                        string current = fn.Str("name") ?? "";
                        // 이름을 조각마다 통째로 다시 보내는 서버도 있다
                        if (!string.IsNullOrEmpty(name) && name != current) fn["name"] = current + name;
                        Append(fn, "arguments", kv.Value.Str("arguments"));
                        break;
                    case "id":
                        if (!string.IsNullOrEmpty(id)) target["id"] = id;
                        break;
                    default:
                        if (kv.Value != null) target[kv.Key] = kv.Value;
                        break;
                }
            }
        }

        public override object Result()
        {
            var msg = new Dictionary<string, object>
            {
                { "role", "assistant" },
                { "content", text.Length > 0 ? text.ToString() : null },
            };
            if (calls.Count > 0) msg["tool_calls"] = calls.Cast<object>().ToList();
            if (refusal.Length > 0) msg["refusal"] = refusal.ToString();
            var resp = new Dictionary<string, object>
            {
                { "choices", new List<object> { new Dictionary<string, object> { { "message", msg }, { "finish_reason", finishReason } } } },
            };
            if (usage != null) resp["usage"] = usage;
            return resp;
        }
    }
}
