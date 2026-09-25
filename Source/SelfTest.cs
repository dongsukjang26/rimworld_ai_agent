#if SELFTEST
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using RimWorld;
using Verse;

namespace AIAdvisor
{
    /// <summary>
    /// 개발용 자체 점검. `dotnet build -p:SelfTest=true` 로 빌드했을 때만 포함되고(배포판에는 없음),
    /// 환경변수 AIADVISOR_SELFTEST=1 로 게임을 실행했을 때만 동작한다.
    /// 모든 도구를 실제 맵에서 실행해 보고, HTTP/JSON 경로를 키 없이 확인한 결과를 Player.log 에 남긴다.
    /// </summary>
    public static class SelfTest
    {
        static bool done;

        public static bool Enabled => Environment.GetEnvironmentVariable("AIADVISOR_SELFTEST") == "1";

        public static void TryRun()
        {
            if (done || !Enabled || Current.Game == null || Find.CurrentMap == null || Find.TickManager.TicksGame < 120) return;
            done = true;
            Log.Message("[AI Advisor selftest] start");
            foreach (var def in DefDatabase<AIModelDef>.AllDefsListForReading)
                Log.Message("[AI Advisor selftest] efforts " + def.modelId + " = [" + string.Join(",", Providers.EffortsFor(def.provider, def.modelId).Skip(1)) + "]");

            string pawn = Find.CurrentMap.mapPawns.FreeColonistsSpawned.FirstOrDefault()?.LabelShort ?? "nobody";
            int failures = 0;
            foreach (var spec in GameTools.Specs)
            {
                var args = new Dictionary<string, object>();
                if (spec.name == "get_colonist_details" || spec.name == "focus_camera") args["name"] = pawn;
                if (spec.name == "get_resources") args["filter"] = "";
                var output = GameTools.Execute(new ToolCall { id = "t", name = spec.name, args = args });
                if (output.isError) failures++;
                string body = output.content.Length > 1500 ? output.content.Substring(0, 1500) + "…" : output.content;
                Log.Message("[AI Advisor selftest] " + spec.name + (output.isError ? " ERROR: " : " OK: ") + body);
                try { Json.Parse(output.content); }
                catch (Exception e) { if (!output.isError) { failures++; Log.Warning("[AI Advisor selftest] invalid JSON from " + spec.name + ": " + e.Message); } }
            }
            Log.Message("[AI Advisor selftest] tools done, failures=" + failures);
            // 세이브에 모드 흔적이 남지 않는지 확인용
            GameDataSaveLoader.SaveGame("AIAdvisorSelfTest");

            string mcpPort = Environment.GetEnvironmentVariable("AIADVISOR_SELFTEST_MCP");
            if (!string.IsNullOrEmpty(mcpPort))
            {
                AdvisorMod.Settings.mcpEnabled = true;
                AdvisorMod.Settings.mcpPort = int.Parse(mcpPort);
                McpServer.Start(AdvisorMod.Settings.mcpPort);
            }

            Find.WindowStack.Add(new Window_Advisor());
            // 가짜 키로 전송 경로 확인: 자동 일시정지 + 오류 메시지 표시
            Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
            string localUrl = Environment.GetEnvironmentVariable("AIADVISOR_SELFTEST_LOCAL");
            if (!string.IsNullOrEmpty(localUrl))
            {
                // 로컬 모델 모드로 전체 에이전트 루프 확인 (도구 호출 → 결과 → 최종 답변)
                AdvisorMod.Settings.useLocal = true;
                AdvisorMod.Settings.localBaseUrl = localUrl;
                AdvisorMod.Settings.localModel = "qwen3:8b";
            }
            else if (Environment.GetEnvironmentVariable("AIADVISOR_SELFTEST_FAKEKEY") != null)
            {
                AdvisorMod.Settings.useLocal = false;
                AdvisorMod.Settings.apiKey = Environment.GetEnvironmentVariable("AIADVISOR_SELFTEST_FAKEKEY");
            }
            else if (Environment.GetEnvironmentVariable("AIADVISOR_SELFTEST_KEEPKEY") != "1")
                AdvisorMod.Settings.apiKey = "sk-ant-selftest-invalid";
            else
            {
                // 설정 파일에 저장된 실제 키로 촬영할 때: 모델과 추론 강도만 지정
                AdvisorMod.Settings.useLocal = false;
                string m = Environment.GetEnvironmentVariable("AIADVISOR_SELFTEST_MODEL");
                if (!string.IsNullOrEmpty(m)) { AdvisorMod.Settings.model = m; AdvisorMod.Settings.modelProvider = AdvisorMod.Settings.Provider; }
                string e = Environment.GetEnvironmentVariable("AIADVISOR_SELFTEST_EFFORT");
                if (e != null) AdvisorMod.Settings.SetEffort(AdvisorMod.Settings.Provider, e);
            }
            AdvisorSession.Send(Environment.GetEnvironmentVariable("AIADVISOR_SELFTEST_QUESTION") ?? "정착지 상태 어때?");
            Log.Message("[AI Advisor selftest] paused after send=" + Find.TickManager.Paused);
            Task.Run(HttpChecks);
        }

        static readonly CancellationTokenSource cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        static async Task HttpChecks()
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            {
                // OpenRouter 모델 목록은 키 없이 조회 가능: TLS + JSON 파싱 + 가격 변환 확인
                try
                {
                    var models = await new OpenAICompatClient(ProviderKind.OpenRouter, "sk-or-selftest", "x", "https://openrouter.ai/api/v1/").ListModels(cts.Token).ConfigureAwait(false);
                    var sample = models.FirstOrDefault(m => m.Known);
                    Log.Message("[AI Advisor selftest] openrouter models=" + models.Count + " sample=" + (sample == null ? "-" : sample.modelId + " $" + sample.inputPerM + "/$" + sample.outputPerM));
                }
                catch (Exception e) { Log.Warning("[AI Advisor selftest] openrouter list failed: " + e.Message); }

                // 잘못된 키: 오류 메시지가 사람이 읽을 수 있게 나오는지 확인
                foreach (var p in new[] { ProviderKind.Anthropic, ProviderKind.OpenAI, ProviderKind.Gemini })
                {
                    try
                    {
                        var client = LlmClient.Create(p, "invalid-selftest-key", Providers.BuiltinModels(p).First().modelId);
                        var history = new List<object> { client.UserMessage("ping") };
                        await client.Complete("test", history, GameTools.Specs, cts.Token).ConfigureAwait(false);
                        Log.Warning("[AI Advisor selftest] " + p + " unexpectedly succeeded");
                    }
                    catch (LlmException e) { Log.Message("[AI Advisor selftest] " + p + " invalid-key error OK: " + e.Message); }
                    catch (Exception e) { Log.Warning("[AI Advisor selftest] " + p + " unexpected exception: " + e); }
                }
            }
            if (AdvisorMod.Settings.useLocal)
            {
                try
                {
                    var models = await LlmClient.Create(ProviderKind.Local, "", "x").ListModels(cts2.Token).ConfigureAwait(false);
                    Log.Message("[AI Advisor selftest] local models=" + string.Join(",", models.Select(m => m.modelId)));
                }
                catch (Exception e) { Log.Warning("[AI Advisor selftest] local list failed: " + e.Message); }
            }
            // 실제 모델 답변은 오래 걸릴 수 있으니 답변이 끝날 때까지 기다렸다가 캡처
            for (int i = 0; i < 360 && await MainThread.Invoke(() => AdvisorSession.Busy).ConfigureAwait(false); i++)
                await Task.Delay(500).ConfigureAwait(false);
            await Task.Delay(1500).ConfigureAwait(false);
            string shot = Environment.GetEnvironmentVariable("AIADVISOR_SELFTEST_SHOT");
            if (Environment.GetEnvironmentVariable("AIADVISOR_SELFTEST_FONT") == "1")
            {
                // 글꼴 한계 재현용: 실제 게임 한글 이름들로 긴 답변을 여러 개 채운다
                MainThread.Post(() =>
                {
                    var labels = DefDatabase<ThingDef>.AllDefsListForReading.Select(d => d.label).Where(l => !string.IsNullOrEmpty(l)).ToList();
                    for (int i = 0; i < 4; i++)
                        AdvisorSession.Entries.Add(new ChatEntry { kind = EntryKind.Assistant, text = string.Join(", ", labels.Skip(i * 150).Take(150)) });
                    AdvisorSession.Version++;
                });
                await Task.Delay(1000).ConfigureAwait(false);
            }
            if (Environment.GetEnvironmentVariable("AIADVISOR_SELFTEST_SETTINGS") == "1")
            {
                MainThread.Post(() => Find.WindowStack.Add(new Dialog_ModSettings(AdvisorMod.Instance)));
                await Task.Delay(1000).ConfigureAwait(false);
                if (Environment.GetEnvironmentVariable("AIADVISOR_SELFTEST_TOGGLE") == "1")
                {
                    // 사용자 제보 재현: 로컬 모델을 눌렀다가 API 키로 돌아오기
                    bool wasLocal = AdvisorMod.Settings.useLocal;
                    MainThread.Post(() => AdvisorMod.Settings.useLocal = !wasLocal);
                    await Task.Delay(1000).ConfigureAwait(false);
                    MainThread.Post(() => AdvisorMod.Settings.useLocal = wasLocal);
                    await Task.Delay(1500).ConfigureAwait(false);
                }
            }
            if (!string.IsNullOrEmpty(shot)) MainThread.Post(() => UnityEngine.ScreenCapture.CaptureScreenshot(shot));
            await Task.Delay(1500).ConfigureAwait(false);
            MainThread.Post(() => Log.Message("[AI Advisor selftest] entries=" + string.Join(" | ", AdvisorSession.Entries.Select(e => e.kind + ":" + e.text))));
            Log.Message("[AI Advisor selftest] http done");
        }
    }
}
#endif
