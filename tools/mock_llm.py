#!/usr/bin/env python3
"""Mock LLM server for RimSage self-test scenarios (tools/run_scenario.sh).

Speaks three wire formats (Anthropic Messages, OpenAI Responses, OpenAI Chat Completions),
streaming (SSE) and non-streaming. Every request body is appended to requests.jsonl.

Behaviour is driven by the latest user text:
  NOTOOL            -> answer directly
  LOOP              -> always call a tool (tests the round limit)
  TOOL:name:{json}  -> call that tool with those args (default: get_colony_overview {})
  ERR429ONCE        -> the first request for this question returns HTTP 429 (retry test)
  ERRSTREAM         -> streaming only: send an error event before any content
  NOSTREAM          -> reject stream=true with HTTP 400 mentioning 'stream'
  IGNORESTREAM      -> ignore stream=true and answer with plain JSON
  SLOW              -> pause between streamed events (for screenshots)
  LINKS             -> the answer contains markdown links and a bare URL with tracking parameters
Answers contain Korean text so UTF-8 splitting across chunks gets exercised.
"""
import json, sys, time, threading
from http.server import ThreadingHTTPServer, BaseHTTPRequestHandler

LOG = sys.argv[2] if len(sys.argv) > 2 else "requests.jsonl"
seen_429 = set()
lock = threading.Lock()


def user_texts_anthropic(msgs):
    out = []
    for m in msgs:
        if m.get("role") != "user":
            continue
        c = m.get("content")
        if isinstance(c, str):
            out.append(c)
        elif isinstance(c, list):
            for b in c:
                if b.get("type") == "text":
                    out.append(b.get("text", ""))
    return out


def tool_plan(question, n_results):
    if "NOTOOL" in question:
        return None
    if "LOOP" in question:
        return ("get_colony_overview", {})
    if n_results > 0:
        return None
    if "TOOL:" in question:
        spec = question.split("TOOL:", 1)[1]
        name, _, args = spec.partition(":")
        args = args.strip()
        # args run until the end of the line
        args = args.splitlines()[0] if args else "{}"
        return (name.strip(), json.loads(args or "{}"))
    return ("get_colony_overview", {})


def answer_text(fmt, question, n_msgs, tool_payloads):
    s = "Answer via %s to [%s] (msgs %d, tool results %d). 한글 답변 테스트: 정착지는 괜찮습니다." % (
        fmt, question[:80].replace("\n", " "), n_msgs, len(tool_payloads))
    if tool_payloads:
        s += " First tool said: " + tool_payloads[-1][:160].replace("\n", " ")
    if "LINKS" in question:
        s += ("\n- 간단한 식사 영양가는 0.9입니다. ([rimworldwiki.com](https://rimworldwiki.com/wiki/Simple_meal?utm_source=openai))"
              "\n- 조리 규칙은 [Cooking - RimWorld Wiki](https://rimworldwiki.com/wiki/Cooking) 참고."
              "\n- 원문: https://www.example.com/guide/a/very/long/path/that/keeps/going/and/going?utm_source=x&id=7.")
    return s


def chunks(text, n=7):
    # split on character count; the byte-level splitting happens in write_sse
    return [text[i:i + n] for i in range(0, len(text), n)] or [""]


class H(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def log_message(self, *a):
        pass

    def _json(self, code, obj, headers=None):
        data = json.dumps(obj).encode("utf-8")
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(data)))
        for k, v in (headers or {}).items():
            self.send_header(k, v)
        self.end_headers()
        self.wfile.write(data)

    def _sse_start(self):
        self.send_response(200)
        self.send_header("Content-Type", "text/event-stream")
        self.send_header("Cache-Control", "no-cache")
        self.send_header("Connection", "close")
        self.end_headers()
        self.close_connection = True

    slow = False

    def write_sse(self, event, obj):
        if self.slow:
            time.sleep(0.12)
        raw = ""
        if event:
            raw += "event: %s\n" % event
        raw += "data: %s\n\n" % (obj if isinstance(obj, str) else json.dumps(obj, ensure_ascii=False))
        b = raw.encode("utf-8")
        # write in two halves, splitting in the middle (often inside a multi-byte char)
        mid = len(b) // 2
        self.wfile.write(b[:mid]); self.wfile.flush()
        time.sleep(0.01)
        self.wfile.write(b[mid:]); self.wfile.flush()

    def do_GET(self):
        if self.path.rstrip("/").endswith("/models"):
            return self._json(200, {"data": [{"id": "mock-model", "display_name": "Mock"}]})
        self._json(404, {"error": {"message": "not found"}})

    def do_POST(self):
        n = int(self.headers.get("Content-Length", "0"))
        body = json.loads(self.rfile.read(n).decode("utf-8"))
        with lock:
            with open(LOG, "a") as f:
                f.write(json.dumps({"path": self.path, "t": time.time(), "body": body}, ensure_ascii=False) + "\n")
        stream = bool(body.get("stream"))
        if self.path.endswith("/messages"):
            return self.anthropic(body, stream)
        if self.path.endswith("/responses"):
            return self.openai_responses(body, stream)
        if self.path.endswith("/chat/completions"):
            return self.chat(body, stream)
        self._json(404, {"error": {"message": "unknown path " + self.path}})

    def common_errors(self, question, stream):
        self.slow = "SLOW" in question
        self.ignore_stream = "IGNORESTREAM" in question
        if "NOSTREAM" in question and stream:
            self._json(400, {"error": {"message": "Unsupported parameter: 'stream' is not supported by this mock"}})
            return True
        if "ERR429ONCE" in question:
            with lock:
                key = (self.path, question)
                if key not in seen_429:
                    seen_429.add(key)
                    self._json(429, {"error": {"message": "rate limited (mock)"}}, {"retry-after": "1"})
                    return True
        return False

    # ------------------------------------------------------------------ Anthropic
    def anthropic(self, body, stream):
        msgs = body.get("messages", [])
        question = (user_texts_anthropic(msgs) or [""])[-1]
        last = msgs[-1] if msgs else {}
        results = []
        if isinstance(last.get("content"), list):
            results = [b.get("content") if isinstance(b.get("content"), str) else json.dumps(b.get("content"))
                       for b in last["content"] if b.get("type") == "tool_result"]
        if self.common_errors(question, stream):
            return
        if self.ignore_stream:
            stream = False
        forbid = (body.get("tool_choice") or {}).get("type") == "none"
        plan = None if forbid else tool_plan(question, len(results))
        thinking = {"type": "thinking", "thinking": "Mock thinking about " + question[:20], "signature": "SIG-" + str(len(msgs))}
        if plan:
            content = [thinking, {"type": "text", "text": "Let me check the colony."},
                       {"type": "tool_use", "id": "toolu_%d" % len(msgs), "name": plan[0], "input": plan[1]}]
            stop = "tool_use"
        else:
            content = [thinking, {"type": "text", "text": answer_text("anthropic", question, len(msgs), results)}]
            stop = "end_turn"
        usage = {"input_tokens": 1000, "output_tokens": 50, "cache_read_input_tokens": 200, "cache_creation_input_tokens": 0}
        if not stream:
            return self._json(200, {"id": "msg_mock", "type": "message", "role": "assistant", "model": body.get("model"),
                                    "content": content, "stop_reason": stop, "usage": usage})
        self._sse_start()
        if "ERRSTREAM" in question:
            self.write_sse("error", {"type": "error", "error": {"type": "overloaded_error", "message": "Overloaded (mock)"}})
            return
        self.write_sse("message_start", {"type": "message_start", "message": {
            "id": "msg_mock", "type": "message", "role": "assistant", "content": [], "model": body.get("model"),
            "stop_reason": None, "usage": {"input_tokens": 1000, "output_tokens": 1, "cache_read_input_tokens": 200, "cache_creation_input_tokens": 0}}})
        self.write_sse("ping", {"type": "ping"})
        for i, block in enumerate(content):
            t = block["type"]
            if t == "text":
                self.write_sse("content_block_start", {"type": "content_block_start", "index": i, "content_block": {"type": "text", "text": ""}})
                for c in chunks(block["text"]):
                    self.write_sse("content_block_delta", {"type": "content_block_delta", "index": i, "delta": {"type": "text_delta", "text": c}})
            elif t == "thinking":
                self.write_sse("content_block_start", {"type": "content_block_start", "index": i, "content_block": {"type": "thinking", "thinking": ""}})
                for c in chunks(block["thinking"]):
                    self.write_sse("content_block_delta", {"type": "content_block_delta", "index": i, "delta": {"type": "thinking_delta", "thinking": c}})
                self.write_sse("content_block_delta", {"type": "content_block_delta", "index": i, "delta": {"type": "signature_delta", "signature": block["signature"]}})
            elif t == "tool_use":
                self.write_sse("content_block_start", {"type": "content_block_start", "index": i, "content_block": {"type": "tool_use", "id": block["id"], "name": block["name"], "input": {}}})
                js = json.dumps(block["input"], ensure_ascii=False)
                for c in chunks(js, 3):
                    self.write_sse("content_block_delta", {"type": "content_block_delta", "index": i, "delta": {"type": "input_json_delta", "partial_json": c}})
            self.write_sse("content_block_stop", {"type": "content_block_stop", "index": i})
        self.write_sse("message_delta", {"type": "message_delta", "delta": {"stop_reason": stop, "stop_sequence": None}, "usage": {"output_tokens": 50}})
        self.write_sse("message_stop", {"type": "message_stop"})

    # ------------------------------------------------------------------ OpenAI Responses
    def openai_responses(self, body, stream):
        items = body.get("input", [])
        users = []
        for it in items:
            if it.get("role") == "user":
                c = it.get("content")
                users.append(c if isinstance(c, str) else json.dumps(c))
        question = (users or [""])[-1]
        # tool results since the last user message
        results = []
        for it in reversed(items):
            if it.get("role") == "user":
                break
            if it.get("type") == "function_call_output":
                results.append(it.get("output", ""))
        if self.common_errors(question, stream):
            return
        if self.ignore_stream:
            stream = False
        forbid = body.get("tool_choice") == "none"
        plan = None if forbid else tool_plan(question, len(results))
        reasoning = {"type": "reasoning", "id": "rs_%d" % len(items), "summary": [], "encrypted_content": "ENC-%d" % len(items)}
        if plan:
            output = [reasoning, {"type": "function_call", "id": "fc_%d" % len(items), "call_id": "call_%d" % len(items),
                                  "name": plan[0], "arguments": json.dumps(plan[1]), "status": "completed"}]
        else:
            text = answer_text("responses", question, len(items), results)
            output = [reasoning, {"type": "message", "id": "msg_%d" % len(items), "role": "assistant", "status": "completed",
                                  "content": [{"type": "output_text", "text": text, "annotations": []}]}]
        resp = {"id": "resp_mock", "object": "response", "status": "completed", "model": body.get("model"), "output": output,
                "usage": {"input_tokens": 1000, "input_tokens_details": {"cached_tokens": 300}, "output_tokens": 60}}
        if not stream:
            return self._json(200, resp)
        self._sse_start()
        if "ERRSTREAM" in question:
            self.write_sse("error", {"type": "error", "code": "server_error", "message": "The server had an error (mock)"})
            return
        created = dict(resp, status="in_progress", output=[])
        self.write_sse("response.created", {"type": "response.created", "sequence_number": 0, "response": created})
        for oi, item in enumerate(output):
            self.write_sse("response.output_item.added", {"type": "response.output_item.added", "output_index": oi, "item": item})
            if item["type"] == "message":
                text = item["content"][0]["text"]
                for c in chunks(text):
                    self.write_sse("response.output_text.delta", {"type": "response.output_text.delta", "item_id": item["id"],
                                                                  "output_index": oi, "content_index": 0, "delta": c})
            self.write_sse("response.output_item.done", {"type": "response.output_item.done", "output_index": oi, "item": item})
        self.write_sse("response.completed", {"type": "response.completed", "response": resp})

    # ------------------------------------------------------------------ Chat Completions
    def chat(self, body, stream):
        msgs = body.get("messages", [])
        users = [m.get("content") for m in msgs if m.get("role") == "user"]
        question = (users or [""])[-1] or ""
        results = []
        for m in reversed(msgs):
            if m.get("role") == "user":
                break
            if m.get("role") == "tool":
                results.append(m.get("content", ""))
        if self.common_errors(question, stream):
            return
        if self.ignore_stream:
            stream = False
        forbid = body.get("tool_choice") == "none"
        plan = None if forbid else tool_plan(question, len(results))
        include_usage = (body.get("stream_options") or {}).get("include_usage")
        usage = {"prompt_tokens": 1000, "completion_tokens": 40, "prompt_tokens_details": {"cached_tokens": 100}}
        if plan:
            call = {"id": "call_%d" % len(msgs), "type": "function", "function": {"name": plan[0], "arguments": json.dumps(plan[1])},
                    "extra_content": {"google": {"thought_signature": "GSIG-%d" % len(msgs)}}}
            msg = {"role": "assistant", "content": None, "tool_calls": [call]}
            finish = "tool_calls"
        else:
            msg = {"role": "assistant", "content": answer_text("chat", question, len(msgs), results)}
            finish = "stop"
        if not stream:
            return self._json(200, {"id": "chatcmpl_mock", "object": "chat.completion", "choices": [{"index": 0, "message": msg, "finish_reason": finish}], "usage": usage})
        self._sse_start()
        self.wfile.write(b": mock keep-alive comment\n\n"); self.wfile.flush()
        if "ERRSTREAM" in question:
            self.write_sse(None, {"error": {"message": "stream failed (mock)", "code": 500}})
            return
        base = {"id": "chatcmpl_mock", "object": "chat.completion.chunk"}
        self.write_sse(None, dict(base, choices=[{"index": 0, "delta": {"role": "assistant", "content": ""}, "finish_reason": None}]))
        if plan:
            call = msg["tool_calls"][0]
            args = call["function"]["arguments"]
            first = {"index": 0, "id": call["id"], "type": "function", "function": {"name": call["function"]["name"], "arguments": ""},
                     "extra_content": call["extra_content"]}
            self.write_sse(None, dict(base, choices=[{"index": 0, "delta": {"tool_calls": [first]}, "finish_reason": None}]))
            for c in chunks(args, 3):
                self.write_sse(None, dict(base, choices=[{"index": 0, "delta": {"tool_calls": [{"index": 0, "function": {"arguments": c}}]}, "finish_reason": None}]))
        else:
            for c in chunks(msg["content"]):
                self.write_sse(None, dict(base, choices=[{"index": 0, "delta": {"content": c}, "finish_reason": None}]))
        self.write_sse(None, dict(base, choices=[{"index": 0, "delta": {}, "finish_reason": finish}]))
        if include_usage:
            self.write_sse(None, dict(base, choices=[], usage=usage))
        self.write_sse(None, "[DONE]")


if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 18999
    ThreadingHTTPServer(("127.0.0.1", port), H).serve_forever()
