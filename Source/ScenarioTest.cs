#if SELFTEST
using System;
using System.Linq;
using System.Threading.Tasks;
using RimWorld;
using Verse;

namespace AIAdvisor
{
    /// <summary>
    /// 개발용 시나리오 점검 (자체 점검 빌드에서만). AIADVISOR_SELFTEST_SCENARIO 로 고른다.
    /// 모의 서버(AIADVISOR_SELFTEST_BASE)에 질문을 보내 대화 기록/저장/세이브 전환을 확인하고 결과를 Player.log 에 남긴다.
    /// </summary>
    public static class ScenarioTest
    {
        public static string Name => Environment.GetEnvironmentVariable("AIADVISOR_SELFTEST_SCENARIO");

        static void L(string msg) => Log.Message("[RimSage scenario] " + msg);

        public static void Start()
        {
            Task.Run(async () =>
            {
                try
                {
                    switch (Name)
                    {
                        case "session": await Session().ConfigureAwait(false); break;
                        case "memory": await Memory().ConfigureAwait(false); break;
                        case "stream": await Stream().ConfigureAwait(false); break;
                        case "tools": await Tools().ConfigureAwait(false); break;
                        case "real": await Real().ConfigureAwait(false); break;
                        case "realreload": await RealReload().ConfigureAwait(false); break;
                        case "links": await Links().ConfigureAwait(false); break;
                        case "tools2": await Tools2().ConfigureAwait(false); break;
                        default: L("unknown scenario " + Name); break;
                    }
                }
                catch (Exception e) { Log.Error("[RimSage scenario] FAILED: " + e); }
                L("done");
            });
        }

        static Task<T> Main<T>(Func<T> f) => MainThread.Invoke(f);
        static Task Main(Action a) => MainThread.Invoke(() => { a(); return true; });

        /// <summary>모의 서버로 보낼 회사를 고른다. local = Chat Completions, anthropic = Messages, openai = Responses.</summary>
        static Task Use(string format) => Main(() =>
        {
            var s = AdvisorMod.Settings;
            string mock = Environment.GetEnvironmentVariable("AIADVISOR_SELFTEST_BASE");
            switch (format)
            {
                case "local":
                    s.useLocal = true;
                    s.localBaseUrl = mock;
                    s.localModel = "mock-model";
                    break;
                case "anthropic":
                    s.useLocal = false;
                    s.apiKey = "sk-ant-mock";
                    s.model = "claude-sonnet-5";
                    s.modelProvider = ProviderKind.Anthropic;
                    break;
                case "openai":
                    s.useLocal = false;
                    s.apiKey = "sk-mock";
                    s.model = Providers.BuiltinModels(ProviderKind.OpenAI).First().modelId;
                    s.modelProvider = ProviderKind.OpenAI;
                    break;
            }
        });

        static async Task Ask(string question)
        {
            await Main(() => AdvisorSession.Send(question)).ConfigureAwait(false);
            for (int i = 0; i < 240 && await Main(() => AdvisorSession.Busy).ConfigureAwait(false); i++)
                await Task.Delay(250).ConfigureAwait(false);
            string last = await Main(() => string.Join(" | ", AdvisorSession.Entries.Skip(Math.Max(0, AdvisorSession.Entries.Count - 4)).Select(e => e.kind + ":" + e.text))).ConfigureAwait(false);
            L("asked [" + question + "] turns=" + await Main(() => AdvisorSession.TurnCount).ConfigureAwait(false) + " last: " + last);
        }

        static async Task WaitBound(int previousBinds)
        {
            for (int i = 0; i < 600; i++)
            {
                if (await Main(() => AdvisorSession.BindCount > previousBinds && Find.TickManager.TicksGame > 60).ConfigureAwait(false)) return;
                await Task.Delay(250).ConfigureAwait(false);
            }
            throw new TimeoutException("session was not bound");
        }

        static async Task Setup()
        {
            await WaitBound(0).ConfigureAwait(false);
            await Main(() =>
            {
                UnityEngine.Application.runInBackground = true;
                AdvisorMod.Settings.pauseOnSend = true;
                AdvisorMod.Settings.memoryTurns = 10;
                AdvisorMod.Settings.maxToolRounds = 8;
                AdvisorMod.Settings.stream = true;
                AdvisorSession.Clear();
                Find.WindowStack.Add(new Window_Advisor());
            }).ConfigureAwait(false);
        }

        static string Run(string tool, string argsJson)
        {
            var args = (System.Collections.Generic.Dictionary<string, object>)Json.Parse(argsJson);
            var o = GameTools.Execute(new ToolCall { id = "t", name = tool, args = args });
            return (o.isError ? "ERROR " : "OK ") + o.content;
        }

        /// <summary>도구 인자/이름 찾기/결과 길이 한도, 마지막 라운드 tool_choice 확인.</summary>
        static async Task Tools()
        {
            await Setup().ConfigureAwait(false);
            string first = null;
            await Main(() =>
            {
                var names = Find.CurrentMap.mapPawns.FreeColonistsSpawned.Select(p => p.LabelShort).ToList();
                first = names[0];
                L("colonists: " + string.Join(", ", names));
                string R(string label, string tool, string args)
                {
                    string r = Run(tool, args);
                    L(label + " => " + (r.Length > 700 ? r.Substring(0, 700) + "..." : r));
                    return r;
                }
                R("legacy name", "get_colonist_details", "{\"name\":\"" + first + "\"}");
                R("all+sections", "get_colonist_details", "{\"names\":[\"all\"],\"sections\":[\"traits\",\"work\"]}");
                R("one found one missing", "get_colonist_details", "{\"names\":[\"" + first + "\",\"zzz-nobody\"],\"sections\":[\"skills\"]}");
                R("only missing", "get_colonist_details", "{\"names\":[\"zzz-nobody\"]}");
                R("bad section", "get_colonist_details", "{\"names\":[\"all\"],\"sections\":[\"skillz\"]}");
                R("comma string", "get_colonist_details", "{\"names\":\"" + names[0] + ", " + names[1] + "\",\"sections\":[\"mood\"]}");
                // 두 명 이상의 이름에 들어 있는 글자 하나로 모호한 경우 만들기
                string common = "abcdefghijklmnopqrstuvwxyz".Select(c => c.ToString())
                    .FirstOrDefault(c => names.Count(n => n.IndexOf(c, StringComparison.OrdinalIgnoreCase) >= 0) >= 2 && !names.Any(n => string.Equals(n, c, StringComparison.OrdinalIgnoreCase)));
                if (common != null) R("ambiguous '" + common + "'", "focus_camera", "{\"name\":\"" + common + "\"}");
                GameTools.MaxResultChars = 300;
                R("cap 300", "get_colonist_details", "{\"names\":[\"all\"]}");
                GameTools.MaxResultChars = 20000;
            }).ConfigureAwait(false);

            // 스트리밍으로 조각나서 오는 도구 인자가 제대로 합쳐지는지 (세 형식)
            foreach (var fmt in new[] { "anthropic", "local", "openai" })
            {
                await Use(fmt).ConfigureAwait(false);
                await Ask("T1 " + fmt + " TOOL:get_colonist_details:{\"names\":[\"" + first + "\"],\"sections\":[\"skills\"]}").ConfigureAwait(false);
            }
            // 마지막 라운드에는 tool_choice: none → 모의 서버가 도구 대신 답한다
            await Main(() => AdvisorMod.Settings.maxToolRounds = 2).ConfigureAwait(false);
            foreach (var fmt in new[] { "anthropic", "local", "openai" })
            {
                await Use(fmt).ConfigureAwait(false);
                await Ask("T2 " + fmt + " LOOP").ConfigureAwait(false);
            }
            await Main(() => AdvisorMod.Settings.maxToolRounds = 8).ConfigureAwait(false);

            // MCP 로도 새 스키마(배열 인자)가 보이고 호출되는지
            await Main(() => McpServer.Start(18766)).ConfigureAwait(false);
            var headers = new System.Collections.Generic.Dictionary<string, string> { { "Accept", "application/json, text/event-stream" } };
            var list = await Http.Send("POST", McpServer.Url(18766), headers, "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}", System.Threading.CancellationToken.None).ConfigureAwait(false);
            var tool = Json.Parse(list.body).Get("result").Arr("tools").FirstOrDefault(t => t.Str("name") == "get_colonist_details");
            L("mcp tools/list status=" + list.status + " get_colonist_details schema=" + Json.Serialize(tool.Get("inputSchema")));
            var call = await Http.Send("POST", McpServer.Url(18766), headers,
                "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"get_colonist_details\",\"arguments\":{\"names\":[\"" + first + "\"],\"sections\":[\"gear\"]}}}",
                System.Threading.CancellationToken.None).ConfigureAwait(false);
            L("mcp tools/call status=" + call.status + " body=" + (call.body.Length > 400 ? call.body.Substring(0, 400) : call.body));
            await Main(() => McpServer.Stop()).ConfigureAwait(false);

            // 설정 화면: 새 옵션(스트리밍, 기억할 문답 수)이 겹치지 않고 그려지는지
            await Main(() => Find.WindowStack.Add(new Dialog_ModSettings(AdvisorMod.Instance))).ConfigureAwait(false);
            await Task.Delay(1200).ConfigureAwait(false);
            await Capture("settings").ConfigureAwait(false);
        }

        static Task Advance(int ticks) => Main(() => Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + ticks));

        /// <summary>이전 문답은 설정한 개수만, 게임 시각을 붙여서 보내는지.</summary>
        static async Task Memory()
        {
            await Setup().ConfigureAwait(false);
            await Use("anthropic").ConfigureAwait(false);
            await Main(() => AdvisorMod.Settings.memoryTurns = 2).ConfigureAwait(false);
            for (int i = 1; i <= 4; i++)
            {
                await Ask("M" + i + " NOTOOL").ConfigureAwait(false);
                await Advance(2500 * 5).ConfigureAwait(false); // 5시간
            }
            await Main(() => AdvisorMod.Settings.memoryTurns = 0).ConfigureAwait(false);
            await Ask("M5 NOTOOL alone").ConfigureAwait(false);
            await Main(() => AdvisorMod.Settings.memoryTurns = 10).ConfigureAwait(false);
        }

        static string Shot(string name)
        {
            string dir = Environment.GetEnvironmentVariable("AIADVISOR_SELFTEST_SHOTDIR");
            return string.IsNullOrEmpty(dir) ? null : System.IO.Path.Combine(dir, name + ".png");
        }

        static async Task Capture(string name)
        {
            string path = Shot(name);
            if (path == null) return;
            await Main(() => UnityEngine.ScreenCapture.CaptureScreenshot(path)).ConfigureAwait(false);
            await Task.Delay(600).ConfigureAwait(false);
            L("screenshot " + path);
        }

        /// <summary>세 가지 API 형식을 스트리밍으로 받는지, 재시도/거부/스트림 오류를 처리하는지.</summary>
        static async Task Stream()
        {
            await Setup().ConfigureAwait(false);
            await Main(() => AdvisorMod.Settings.stream = true).ConfigureAwait(false);
            foreach (var fmt in new[] { "local", "anthropic", "openai" })
            {
                await Use(fmt).ConfigureAwait(false);
                await Ask("S1 " + fmt + " 도구 사용").ConfigureAwait(false);
                await Ask("S2 " + fmt + " NOTOOL").ConfigureAwait(false);
            }
            await Use("anthropic").ConfigureAwait(false);
            await Ask("S3 anthropic ERR429ONCE NOTOOL").ConfigureAwait(false);
            await Use("local").ConfigureAwait(false);
            await Ask("S4 local NOSTREAM NOTOOL").ConfigureAwait(false);
            await Ask("S5 local NOTOOL after fallback").ConfigureAwait(false);
            foreach (var fmt in new[] { "anthropic", "local", "openai" })
            {
                await Use(fmt).ConfigureAwait(false);
                await Ask("S5b " + fmt + " IGNORESTREAM NOTOOL").ConfigureAwait(false);
            }
            await Use("openai").ConfigureAwait(false);
            await Ask("S6 openai ERRSTREAM NOTOOL").ConfigureAwait(false);

            // 느린 스트림 중간과 끝을 촬영
            await Use("anthropic").ConfigureAwait(false);
            await Main(() => AdvisorSession.Send("S7 SLOW NOTOOL 천천히 답해줘")).ConfigureAwait(false);
            await Task.Delay(2500).ConfigureAwait(false);
            L("mid-stream live=" + await Main(() => AdvisorSession.LiveText.Length).ConfigureAwait(false) + " status=" + await Main(() => AdvisorSession.Status).ConfigureAwait(false));
            await Capture("stream_mid").ConfigureAwait(false);
            for (int i = 0; i < 120 && await Main(() => AdvisorSession.Busy).ConfigureAwait(false); i++) await Task.Delay(250).ConfigureAwait(false);
            await Task.Delay(500).ConfigureAwait(false);
            await Capture("stream_done").ConfigureAwait(false);

            await Main(() =>
            {
                var answers = AdvisorSession.Entries.Where(e => e.kind == EntryKind.Assistant).ToList();
                L("assistant answers=" + answers.Count + " replacementChars=" + answers.Count(e => e.text.IndexOf('\uFFFD') >= 0)
                  + " intactKorean=" + answers.Count(e => e.text.Contains("한글 답변 테스트: 정착지는 괜찮습니다.")) + " liveLeft=" + AdvisorSession.LiveText.Length
                  + " sessionCost=" + AdvisorSession.SessionCost + " unknown=" + AdvisorSession.SessionCostUnknown);
            }).ConfigureAwait(false);
        }

        /// <summary>실제 API 시나리오에서 이번 대화에 쓸 수 있는 최대 금액 (USD).</summary>
        static double Budget => double.TryParse(Environment.GetEnvironmentVariable("AIADVISOR_SELFTEST_BUDGET"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var b) ? b : 0.30;

        /// <summary>실제 API 로 질문하고, 스트리밍이 실제로 조금씩 왔는지(첫 글자까지 걸린 시간, 화면 갱신 횟수)와 비용을 남긴다.</summary>
        static async Task<string> AskReal(string label, string question)
        {
            double spent = await Main(() => AdvisorSession.SessionCost).ConfigureAwait(false);
            if (spent > Budget) throw new Exception("budget reached: $" + spent);
            int before = await Main(() => AdvisorSession.Entries.Count).ConfigureAwait(false);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await Main(() => AdvisorSession.Send(question)).ConfigureAwait(false);
            long firstText = -1;
            int updates = 0, lastLen = 0;
            while (await Main(() => AdvisorSession.Busy).ConfigureAwait(false))
            {
                int len = await Main(() => AdvisorSession.LiveText.Length).ConfigureAwait(false);
                if (len > 0 && firstText < 0) firstText = sw.ElapsedMilliseconds;
                if (len != lastLen) { updates++; lastLen = len; }
                if (sw.Elapsed.TotalSeconds > 240) await Main(() => AdvisorSession.Cancel()).ConfigureAwait(false);
                await Task.Delay(40).ConfigureAwait(false);
            }
            var entries = await Main(() => AdvisorSession.Entries.Skip(before).ToList()).ConfigureAwait(false);
            double cost = await Main(() => AdvisorSession.LastAnswerCost).ConfigureAwait(false);
            int turns = await Main(() => AdvisorSession.TurnCount).ConfigureAwait(false);
            L(label + " time=" + sw.ElapsedMilliseconds + "ms firstText=" + firstText + "ms liveUpdates=" + updates
              + " cost=$" + cost.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture) + " turns=" + turns);
            foreach (var e in entries) L(label + "   " + e.kind + ": " + e.text.Replace("\n", " ⏎ "));
            return entries.LastOrDefault(e => e.kind == EntryKind.Assistant)?.text ?? "";
        }

        static Task Model(string id, string effort = "") => Main(() =>
        {
            var s = AdvisorMod.Settings;
            s.model = id;
            s.modelProvider = ProviderKind.OpenAI;
            s.SetEffort(ProviderKind.OpenAI, effort);
            L("model -> " + s.EffectiveModel() + " effort=" + (s.EffectiveEffort(ProviderKind.OpenAI, id) is string e && e.Length > 0 ? e : "default"));
        });

        /// <summary>
        /// 실제 OpenAI API 로 확인 (tools/run_scenario.sh 를 REAL=1 로 실행: 실제 설정 폴더의 복사본을 쓴다).
        /// 모의 서버로는 알 수 없는 것: 실제 스트리밍 간격, 실제 모델이 도구 인자/tool_choice 를 따르는지, 웹 검색, 비용 집계.
        /// </summary>
        static async Task Real()
        {
            await WaitBound(0).ConfigureAwait(false);
            string info = await Main(() =>
            {
                UnityEngine.Application.runInBackground = true;
                var s = AdvisorMod.Settings;
                s.useLocal = false;
                s.pauseOnSend = true;
                s.resumeAfterAnswer = false;
                s.memoryTurns = 10;
                s.maxToolRounds = 8;
                s.stream = true;
                s.webSearch = true;
                s.model = "gpt-6-luna";
                s.modelProvider = ProviderKind.OpenAI;
                s.SetEffort(ProviderKind.OpenAI, "");
                AdvisorSession.Clear();
                Find.WindowStack.Add(new Window_Advisor());
                return "provider=" + s.Provider + " model=" + s.EffectiveModel() + " totalBefore=$" + s.totalCostUsd.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
            }).ConfigureAwait(false);
            L(info + " budget=$" + Budget);
            if (!info.Contains("provider=OpenAI")) throw new Exception("no OpenAI key in the copied settings");
            double totalBefore = await Main(() => AdvisorMod.Settings.totalCostUsd).ConfigureAwait(false);

            await AskReal("R1", "지금 정착지 상태를 보고 가장 급한 일 3가지만 짧게 알려줘.").ConfigureAwait(false);
            await Capture("real_r1").ConfigureAwait(false);
            await AskReal("R2", "그중 첫 번째 일은 누가 맡는 게 제일 좋아? 이유도 한 줄로.").ConfigureAwait(false);
            await AskReal("R3", "정착민 전원의 기술과 특성만 보고, 각자 무엇을 맡기면 좋을지 한 줄씩 정리해줘.").ConfigureAwait(false);

            // 같은 회사 안에서 모델과 추론 강도를 바꿔도 대화가 이어지는지
            await Model("gpt-5.6-luna").ConfigureAwait(false);
            await AskReal("R4", "방금 정리한 역할 분담 기준으로, 연구는 무엇부터 하는 게 좋을까? 두 개만.").ConfigureAwait(false);
            await Model("gpt-6-luna", "low").ConfigureAwait(false);
            await AskReal("R4b", "지금 식량은 며칠치 남았어? 숫자 위주로 간단히.").ConfigureAwait(false);
            await Model("gpt-6-luna").ConfigureAwait(false);

            // 웹 검색과 출처, 웹 검색을 거부하는 모델에서의 자동 전환
            await AskReal("R5", "림월드 위키에서 '간단한 식사(Simple meal)'의 영양가와 조리 작업량을 찾아서 알려줘. 출처도 같이.").ConfigureAwait(false);
            await Capture("real_r5").ConfigureAwait(false);
            await Model("gpt-4.1-nano").ConfigureAwait(false);
            await AskReal("R5b", "한 문장으로, 지금 계절이랑 바깥 온도 알려줘.").ConfigureAwait(false);
            await Model("gpt-6-luna").ConfigureAwait(false);

            // 조회 한도 1: 첫 라운드에 도구를 부르고, 마지막 라운드(tool_choice: none)에는 답해야 한다
            await Main(() => AdvisorMod.Settings.maxToolRounds = 1).ConfigureAwait(false);
            await AskReal("R6", "정착민 전원 상세, 자원, 위협, 연구, 기반 시설을 전부 조회해서 종합 보고서를 써줘.").ConfigureAwait(false);
            await Main(() => AdvisorMod.Settings.maxToolRounds = 8).ConfigureAwait(false);

            // 스트리밍 없이도 여전히 되는지
            await Main(() => AdvisorMod.Settings.stream = false).ConfigureAwait(false);
            await AskReal("R7", "한 문장으로 지금 날씨 알려줘.").ConfigureAwait(false);
            await Main(() => AdvisorMod.Settings.stream = true).ConfigureAwait(false);

            // 스트리밍 도중 취소
            int turnsBefore = await Main(() => AdvisorSession.TurnCount).ConfigureAwait(false);
            await Main(() => AdvisorSession.Send("정착지를 오래 운영하기 위한 가이드를 아주 길고 자세하게 써줘. 최소 20개 항목으로.")).ConfigureAwait(false);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.Elapsed.TotalSeconds < 90 && await Main(() => AdvisorSession.LiveText.Length < 120 && AdvisorSession.Busy).ConfigureAwait(false))
                await Task.Delay(50).ConfigureAwait(false);
            int liveAtCancel = await Main(() => AdvisorSession.LiveText.Length).ConfigureAwait(false);
            await Main(() => AdvisorSession.Cancel()).ConfigureAwait(false);
            var cancelWatch = System.Diagnostics.Stopwatch.StartNew();
            while (await Main(() => AdvisorSession.Busy).ConfigureAwait(false)) await Task.Delay(50).ConfigureAwait(false);
            L("R8 cancel: liveAtCancel=" + liveAtCancel + " stoppedIn=" + cancelWatch.ElapsedMilliseconds + "ms liveAfter=" + await Main(() => AdvisorSession.LiveText.Length).ConfigureAwait(false)
              + " turns " + turnsBefore + "->" + await Main(() => AdvisorSession.TurnCount).ConfigureAwait(false)
              + " last=" + await Main(() => AdvisorSession.Entries.Last().kind + ":" + AdvisorSession.Entries.Last().text).ConfigureAwait(false));

            // 세이브를 다시 불러와도 대화가 이어지는지
            await Main(() => GameDataSaveLoader.SaveGame("RimSageReal")).ConfigureAwait(false);
            int binds = await Main(() => AdvisorSession.BindCount).ConfigureAwait(false);
            await Main(() => GameDataSaveLoader.LoadGame("RimSageReal")).ConfigureAwait(false);
            await WaitBound(binds).ConfigureAwait(false);
            await Main(() =>
            {
                UnityEngine.Application.runInBackground = true;
                Find.WindowStack.Add(new Window_Advisor());
                L("R9 reloaded entries=" + AdvisorSession.Entries.Count + " turns=" + AdvisorSession.TurnCount);
            }).ConfigureAwait(false);
            await AskReal("R9", "내가 처음에 물어본 게 뭐였고, 너는 뭐라고 답했지? 두 문장으로.").ConfigureAwait(false);
            await Capture("real_end").ConfigureAwait(false);

            await Main(() =>
            {
                var s = AdvisorMod.Settings;
                L("SUMMARY sessionCost=$" + AdvisorSession.SessionCost.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture)
                  + " unknown=" + AdvisorSession.SessionCostUnknown
                  + " totalDelta=$" + (s.totalCostUsd - totalBefore).ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture)
                  + " requests=" + s.totalRequests + " tokensIn=" + s.totalInputTokens + " tokensOut=" + s.totalOutputTokens);
            }).ConfigureAwait(false);
        }

        /// <summary>세이브를 불러온 직후 질문이 늦게 오는 현상 재현용 (실제 API, 짧은 질문 세 개).</summary>
        static async Task RealReload()
        {
            await WaitBound(0).ConfigureAwait(false);
            await Main(() =>
            {
                UnityEngine.Application.runInBackground = true;
                var s = AdvisorMod.Settings;
                s.useLocal = false;
                s.stream = true;
                s.webSearch = true;
                s.model = "gpt-6-luna";
                s.modelProvider = ProviderKind.OpenAI;
                s.SetEffort(ProviderKind.OpenAI, "");
                AdvisorSession.Clear();
                Find.WindowStack.Add(new Window_Advisor());
            }).ConfigureAwait(false);
            await AskReal("A1", "도구 쓰지 말고 한 문장으로 인사만 해줘.").ConfigureAwait(false);
            await Main(() => GameDataSaveLoader.SaveGame("RimSageReload")).ConfigureAwait(false);
            int binds = await Main(() => AdvisorSession.BindCount).ConfigureAwait(false);
            await Main(() => GameDataSaveLoader.LoadGame("RimSageReload")).ConfigureAwait(false);
            await WaitBound(binds).ConfigureAwait(false);
            await Main(() => { UnityEngine.Application.runInBackground = true; Find.WindowStack.Add(new Window_Advisor()); }).ConfigureAwait(false);
            L("A reloaded sessionCost=$" + await Main(() => AdvisorSession.SessionCost.ToString("R", System.Globalization.CultureInfo.InvariantCulture)).ConfigureAwait(false));
            await AskReal("A2", "도구 쓰지 말고, 방금 내가 뭐라고 했는지 한 문장으로.").ConfigureAwait(false);
            await AskReal("A3", "도구 쓰지 말고 한 단어로 대답해줘: 좋아?").ConfigureAwait(false);
        }

        /// <summary>답변 속 링크를 짧게 보여 주고 링크 버튼으로 모으는지, 복사 아이콘이 글자를 가리지 않는지.</summary>
        static async Task Links()
        {
            await Setup().ConfigureAwait(false);
            await Use("anthropic").ConfigureAwait(false);
            await Ask("L1 LINKS NOTOOL 출처 보여줘").ConfigureAwait(false);
            await Main(() =>
            {
                var e = AdvisorSession.Entries.Last(x => x.kind == EntryKind.Assistant);
                L("display: " + ChatFormat.Display(e).Replace("\n", " ⏎ "));
                L("urls: " + string.Join(" | ", ChatFormat.Urls(e)));
            }).ConfigureAwait(false);
            await Task.Delay(500).ConfigureAwait(false);
            await Capture("links").ConfigureAwait(false);
        }

        /// <summary>한 도구를 여러 인자로 불러 결과를 로그에 남긴다 (2번 도구 확장 확인용).</summary>
        static void Probe(string tool, string argsJson, int max = 2500)
        {
            string r = Run(tool, argsJson);
            L("probe " + tool + " " + argsJson + " => " + r.Length + " chars: " + (r.Length > max ? r.Substring(0, max) + "..." : r));
        }

        /// <summary>
        /// 2번에서 추가한 도구들을 실제 게임 상태로 불러 본다. AIADVISOR_SELFTEST_PROBES 로 도구를 고를 수 있다 (쉼표로 구분, 기본 전부).
        /// </summary>
        static async Task Tools2()
        {
            await WaitBound(0).ConfigureAwait(false);
            string only = Environment.GetEnvironmentVariable("AIADVISOR_SELFTEST_PROBES") ?? "";
            bool Want(string name) => only.Length == 0 || only.Split(',').Contains(name);
            await Main(() =>
            {
                UnityEngine.Application.runInBackground = true;
                var specs = GameTools.Specs;
                L("tool definitions: " + specs.Count + " tools, " + Json.Serialize(specs.Select(t => new System.Collections.Generic.Dictionary<string, object> { { "name", t.name }, { "description", t.description }, { "parameters", t.schema } }).ToList()).Length + " chars: " + string.Join(", ", specs.Select(t => t.name)));
                L("map=" + Find.CurrentMap?.Parent?.LabelCap + " colonists=" + Find.CurrentMap?.mapPawns.FreeColonistsSpawnedCount + " tools=" + GameTools.Specs.Count
                  + " dlc: royalty=" + ModsConfig.RoyaltyActive + " ideology=" + ModsConfig.IdeologyActive + " biotech=" + ModsConfig.BiotechActive + " anomaly=" + ModsConfig.AnomalyActive + " odyssey=" + ModsConfig.OdysseyActive);
                if (Want("lookup_def"))
                {
                    foreach (var q in new[]
                    {
                        "{\"query\":\"simple meal\"}", "{\"query\":\"assault rifle\",\"limit\":2}", "{\"query\":\"Gun_AssaultRifle\"}",
                        "{\"query\":\"plague\",\"kind\":\"condition\",\"limit\":1}", "{\"query\":\"tough\",\"kind\":\"trait\"}",
                        "{\"query\":\"electricity\",\"kind\":\"research\",\"limit\":1}", "{\"query\":\"potato\",\"kind\":\"plant\",\"limit\":1}",
                        "{\"query\":\"muffalo\",\"limit\":1}", "{\"query\":\"meal\",\"kind\":\"recipe\",\"limit\":2}", "{\"query\":\"steel\",\"limit\":1}",
                        "{\"query\":\"solar\",\"kind\":\"building\",\"limit\":1}", "{\"query\":\"flak vest\",\"limit\":1}", "{\"query\":\"robust\",\"kind\":\"gene\",\"limit\":1}",
                        "{\"query\":\"zzqqxx\"}", "{\"query\":\"meal\",\"kind\":\"spaceship\"}",
                    }) Probe("lookup_def", q);
                    // 한국어 게임에서 한국어 이름으로도 찾는지: 실제 라벨을 하나 골라서 검색
                    string ko = DefDatabase<ThingDef>.GetNamedSilentFail("MealSimple")?.label;
                    if (ko != null) Probe("lookup_def", "{\"query\":\"" + ko + "\",\"limit\":1}", 600);
                }
                if (Environment.GetEnvironmentVariable("AIADVISOR_SELFTEST_SETUP") == "1") SetupTestState();
                foreach (var t in new[] { "get_quests", "get_world", "get_production", "get_farming", "get_medical", "get_social", "get_trade",
                                          "get_ideology", "get_biotech", "get_royalty", "get_anomaly", "get_gravship" })
                    if (Want(t) && GameTools.Specs.Any(x => x.name == t)) Probe(t, "{}", 4000);
                if (Want("get_trade") && TradeSession.Active)
                {
                    Probe("get_trade", "{\"filter\":\"" + (DefDatabase<ThingDef>.GetNamedSilentFail("Silver")?.label ?? "silver") + "\"}", 1500);
                    TradeSession.Close();
                }
                if (Want("get_map_layout"))
                {
                    Probe("get_map_layout", "{}", 6000);
                    Probe("get_map_layout", "{\"center\":\"" + Find.CurrentMap.mapPawns.FreeColonistsSpawned.First().LabelShort + "\",\"radius\":8}", 1500);
                    Probe("get_map_layout", "{\"center\":\"5,5\",\"radius\":6}", 800);
                    Probe("get_map_layout", "{\"center\":\"9999,5\"}", 300);
                    // 글자별로 어떤 건물이 들어갔는지 (분류 확인용)
                    var byChar = new System.Collections.Generic.Dictionary<char, System.Collections.Generic.HashSet<string>>();
                    var m = Find.CurrentMap;
                    foreach (var cell in m.AllCells)
                    {
                        var b = cell.GetEdifice(m);
                        if (b == null || cell.Fogged(m)) continue;
                        char ch = GameTools.LayoutCharForTest(cell, m);
                        if (!byChar.TryGetValue(ch, out var set)) byChar[ch] = set = new System.Collections.Generic.HashSet<string>();
                        set.Add(b.def.label);
                    }
                    foreach (var kv in byChar.OrderBy(k => k.Key)) L("layout '" + kv.Key + "' = " + string.Join(", ", kv.Value.Take(15)));
                    // 실제 맵과 비교할 수 있게 같은 가운데를 찍는다
                    var layout = (System.Collections.Generic.Dictionary<string, object>)Json.Parse(Run("get_map_layout", "{}").Substring(3));
                    var xz = ((string)layout["center"]).Split(',');
                    Find.CameraDriver.SetRootPosAndSize(new UnityEngine.Vector3(int.Parse(xz[0]) + 0.5f, 0f, int.Parse(xz[1]) + 0.5f), 24f);
                }
                if (Want("get_trends"))
                {
                    Probe("get_trends", "{\"days\":3}", 4000);
                    Probe("get_trends", "{\"days\":30}", 1500);
                }
                captureMap = Want("get_map_layout");
            }).ConfigureAwait(false);
            if (captureMap)
            {
                await Task.Delay(1500).ConfigureAwait(false);
                await Capture("map_layout").ConfigureAwait(false);
            }
        }

        static bool captureMap;

        /// <summary>
        /// 빠른 테스트 맵에는 없는 상황을 만든다 (테스트용 게임에서만): 흑사병 환자, 궤도 상인과 거래 창, 메카니터, 중력 엔진.
        /// </summary>
        static void SetupTestState()
        {
            var map = Find.CurrentMap;
            var colonists = map.mapPawns.FreeColonistsSpawned.ToList();
            var sick = colonists[0];
            var plague = HediffMaker.MakeHediff(HediffDefOf.Plague, sick);
            plague.Severity = 0.3f;
            sick.health.AddHediff(plague);
            L("setup: plague on " + sick.LabelShort);

            var kind = DefDatabase<TraderKindDef>.AllDefsListForReading.First(d => d.orbital);
            var ship = new TradeShip(kind, null);
            map.passingShipManager.AddShip(ship);
            ship.GenerateThings();
            TradeSession.SetupWith(ship, colonists[1], false);
            L("setup: trade session with " + ship.FullTitle + " active=" + TradeSession.Active);

            if (ModsConfig.BiotechActive)
            {
                colonists[2].health.AddHediff(HediffMaker.MakeHediff(HediffDefOf.MechlinkImplant, colonists[2], colonists[2].health.hediffSet.GetBrain()));
                L("setup: mechlink on " + colonists[2].LabelShort + " mechanitor=" + MechanitorUtility.IsMechanitor(colonists[2]));
            }
            var engine = DefDatabase<ThingDef>.GetNamedSilentFail("GravEngine");
            if (ModsConfig.OdysseyActive && engine != null)
            {
                var cell = CellFinder.RandomClosewalkCellNear(colonists[0].Position, map, 12, c => GenConstruct.CanPlaceBlueprintAt(engine, c, Rot4.North, map).Accepted);
                var thing = GenSpawn.Spawn(ThingMaker.MakeThing(engine), cell, map, WipeMode.Vanish);
                thing.SetFaction(Faction.OfPlayer);
                L("setup: grav engine at " + cell);
            }
        }

        static async Task Session()
        {
            await WaitBound(0).ConfigureAwait(false);
            await Main(() =>
            {
                UnityEngine.Application.runInBackground = true;
                AdvisorMod.Settings.pauseOnSend = true;
                AdvisorSession.Clear();
                Find.WindowStack.Add(new Window_Advisor());
                L("bound key=" + AdvisorSession.SessionKey + " file=" + SessionStore.Folder);
            }).ConfigureAwait(false);

            // 회사를 바꿔 가며 물어도 대화가 이어져야 한다
            await Use("local").ConfigureAwait(false);
            await Ask("Q1 local 정착지 상태?").ConfigureAwait(false);
            await Use("anthropic").ConfigureAwait(false);
            await Ask("Q2 anthropic 식량은?").ConfigureAwait(false);
            await Use("openai").ConfigureAwait(false);
            await Ask("Q3 openai NOTOOL 요약해줘").ConfigureAwait(false);
            // 조회 한도에 걸려도 문답이 기억되는지
            await Main(() => AdvisorMod.Settings.maxToolRounds = 2).ConfigureAwait(false);
            await Ask("Q4 LOOP 한도 테스트").ConfigureAwait(false);
            await Main(() => AdvisorMod.Settings.maxToolRounds = 8).ConfigureAwait(false);

            // 세이브 → 시간을 앞으로 → 질문 → 예전 세이브 불러오기: 그 뒤 문답은 AI 기억에서 빠져야 한다
            await Main(() => GameDataSaveLoader.SaveGame("RimSageScenario")).ConfigureAwait(false);
            await Main(() => Find.TickManager.DebugSetTicksGame(Find.TickManager.TicksGame + 30000)).ConfigureAwait(false);
            await Use("local").ConfigureAwait(false);
            await Ask("Q5 local FUTURE NOTOOL").ConfigureAwait(false);
            int binds = await Main(() => AdvisorSession.BindCount).ConfigureAwait(false);
            await Main(() => GameDataSaveLoader.LoadGame("RimSageScenario")).ConfigureAwait(false);
            await WaitBound(binds).ConfigureAwait(false);
            await Main(() =>
            {
                UnityEngine.Application.runInBackground = true;
                Find.WindowStack.Add(new Window_Advisor());
                L("reloaded key=" + AdvisorSession.SessionKey + " turns=" + AdvisorSession.TurnCount + " entries=" + AdvisorSession.Entries.Count
                  + " last: " + AdvisorSession.Entries.Last().kind + ":" + AdvisorSession.Entries.Last().text);
            }).ConfigureAwait(false);
            await Ask("Q6 local after reload NOTOOL").ConfigureAwait(false);
        }
    }
}
#endif
