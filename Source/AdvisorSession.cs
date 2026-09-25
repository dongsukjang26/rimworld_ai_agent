using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using RimWorld;
using Verse;

namespace AIAdvisor
{
    public enum EntryKind
    {
        User,
        Assistant,
        Tool,
        Error,
        Info,
    }

    public class ChatEntry
    {
        public EntryKind kind;
        public string text;
    }

    /// <summary>
    /// 대화 상태와 에이전트 루프. 루프는 백그라운드 Task 에서 돌고,
    /// 게임 데이터 읽기와 UI 상태 변경은 MainThread 로 넘긴다.
    /// </summary>
    public static class AdvisorSession
    {
        public static readonly List<ChatEntry> Entries = new List<ChatEntry>();
        public static bool Busy { get; private set; }
        public static string Status = "";
        public static double SessionCost;
        public static double LastAnswerCost;
        public static bool SessionCostUnknown;
        public static int Version; // UI 가 새 메시지를 감지해 스크롤할 때 사용

        static readonly List<object> history = new List<object>();
        static ProviderKind historyProvider = ProviderKind.None;
        static CancellationTokenSource cts;
        static TimeSpeed speedBeforePause = TimeSpeed.Paused;
        static string systemPrompt;

        public static void Clear()
        {
            if (Busy) return;
            Entries.Clear();
            history.Clear();
            SessionCost = 0;
            LastAnswerCost = 0;
            SessionCostUnknown = false;
            Version++;
        }

        public static void Cancel()
        {
            cts?.Cancel();
        }

        static void AddEntry(EntryKind kind, string text)
        {
            Entries.Add(new ChatEntry { kind = kind, text = text });
            Version++;
        }

        /// <summary>메인 스레드에서 호출.</summary>
        public static void Send(string text)
        {
            text = text?.Trim();
            if (string.IsNullOrEmpty(text) || Busy) return;
            var s = AdvisorMod.Settings;
            var provider = s.Provider;
            if (provider == ProviderKind.None)
            {
                AddEntry(EntryKind.Error, "AIAdvisor_NoKey".Translate());
                return;
            }
            if (s.OverLimit)
            {
                AddEntry(EntryKind.Error, "AIAdvisor_LimitReached".Translate(AdvisorMod.FormatCost(s.spendLimitUsd)));
                return;
            }
            string model = s.EffectiveModel();
            if (string.IsNullOrEmpty(model))
            {
                AddEntry(EntryKind.Error, "AIAdvisor_NoModel".Translate());
                return;
            }

            if (historyProvider != provider)
            {
                // 회사마다 메시지 형식이 달라서 대화 기록을 이어갈 수 없다
                if (history.Count > 0) AddEntry(EntryKind.Info, "AIAdvisor_ProviderChanged".Translate());
                history.Clear();
                historyProvider = provider;
            }

            var tm = Find.TickManager;
            speedBeforePause = tm.CurTimeSpeed;
            if (s.pauseOnSend && !tm.Paused) tm.Pause();

            systemPrompt = systemPrompt ?? BuildSystemPrompt();
            var client = LlmClient.Create(provider, s.apiKey, model);
            client.webSearch = s.webSearch;
            var specs = GameTools.Specs;
            AddEntry(EntryKind.User, text);
            int checkpoint = history.Count;
            history.Add(client.UserMessage(text));

            Busy = true;
            Status = "AIAdvisor_Thinking".Translate();
            LlmClient.RetryNotice = (wait, attempt, max, reason) =>
                SetStatus("AIAdvisor_Retrying".TranslateSafe().Replace("{0}", wait.ToString()).Replace("{1}", attempt.ToString()).Replace("{2}", max.ToString()).Replace("{3}", reason));
            LastAnswerCost = 0;
            cts = new CancellationTokenSource();
            var token = cts.Token;
            int maxRounds = s.maxToolRounds;
            Task.Run(() => RunLoop(client, provider, model, specs, checkpoint, maxRounds, token));
        }

        static async Task RunLoop(LlmClient client, ProviderKind provider, string model, List<ToolSpec> specs, int checkpoint, int maxRounds, CancellationToken ct)
        {
            bool success = false;
            var sources = new List<KeyValuePair<string, string>>();
            try
            {
                for (int round = 0; ; round++)
                {
                    bool lastRound = round >= maxRounds;
                    string system = systemPrompt;
                    if (client.WebSearchActive) system += WebSearchHint;
                    if (lastRound) system += "\n\nYou have used all tool calls for this question. Answer now with the information you already have.";
                    var reply = await client.Complete(system, new List<object>(history), specs, ct).ConfigureAwait(false);
                    RecordUsage(provider, model, reply.usage);
                    if (reply.webSearchRejected != null)
                        Post(EntryKind.Info, "AIAdvisor_WebSearchRejected".TranslateSafe().Replace("{0}", reply.webSearchRejected));

                    if (reply.refused)
                    {
                        Post(EntryKind.Error, "AIAdvisor_Refused".TranslateSafe() + (reply.text.Length > 0 ? "\n" + reply.text : ""));
                        return;
                    }

                    history.AddRange(reply.historyItems);
                    if (reply.webSearchQueries.Count > 0)
                        Post(EntryKind.Tool, "AIAdvisor_WebSearchUse".TranslateSafe() + " " + string.Join(" / ", reply.webSearchQueries));
                    foreach (var src in reply.sources)
                        if (!sources.Any(x => x.Value == src.Value)) sources.Add(src);

                    if (reply.toolCalls.Count == 0 || reply.truncated || lastRound)
                    {
                        string answer = reply.text.Length > 0 ? reply.text : "AIAdvisor_EmptyAnswer".TranslateSafe();
                        answer += FormatSources(sources, answer);
                        if (reply.truncated || reply.unfinished) answer += "\n\n" + "AIAdvisor_Truncated".TranslateSafe();
                        Post(EntryKind.Assistant, answer);
                        if (reply.toolCalls.Count > 0 || reply.unfinished)
                        {
                            // 도구 호출(또는 끝나지 않은 웹 검색)로 끝난 assistant 메시지는 다음 요청에서 거부되므로 이번 턴은 기록에서 뺀다
                            RollbackTo(checkpoint, keepUserTurn: false);
                            Post(EntryKind.Info, "AIAdvisor_HistoryTrimmed".TranslateSafe());
                        }
                        success = true;
                        return;
                    }

                    if (reply.text.Length > 0) Post(EntryKind.Info, reply.text);
                    string names = string.Join(", ", reply.toolCalls.Select(c => c.name));
                    Post(EntryKind.Tool, "AIAdvisor_ToolUse".TranslateSafe() + " " + names);
                    SetStatus("AIAdvisor_ReadingData".TranslateSafe() + " " + names);

                    var outputs = await MainThread.Invoke(() => reply.toolCalls.Select(GameTools.Execute).ToList()).ConfigureAwait(false);
                    history.AddRange(client.ToolResultMessages(outputs));
                    SetStatus("AIAdvisor_Thinking".TranslateSafe());
                }
            }
            catch (OperationCanceledException)
            {
                Post(EntryKind.Info, "AIAdvisor_Cancelled".TranslateSafe());
            }
            catch (Exception e)
            {
                Post(EntryKind.Error, "AIAdvisor_Error".TranslateSafe() + " " + e.Message);
                if (!(e is LlmException)) Log.Warning("[AI Advisor] " + e);
            }
            finally
            {
                // 실패한 턴은 기록에서 지워야 다음 질문 때 메시지 순서가 깨지지 않는다
                if (!success) RollbackTo(checkpoint, keepUserTurn: false);
                MainThread.Post(() =>
                {
                    Busy = false;
                    Status = "";
                    var s = AdvisorMod.Settings;
                    if (s.resumeAfterAnswer && s.pauseOnSend && speedBeforePause != TimeSpeed.Paused && Find.TickManager != null && Find.TickManager.Paused)
                        Find.TickManager.CurTimeSpeed = speedBeforePause;
                    AdvisorMod.Save();
                });
            }
        }

        static void RollbackTo(int checkpoint, bool keepUserTurn)
        {
            int keep = keepUserTurn ? checkpoint + 1 : checkpoint;
            if (history.Count > keep) history.RemoveRange(keep, history.Count - keep);
        }

        const string WebSearchHint =
            "\n- You also have a web_search tool. Use it when you are not sure about RimWorld game mechanics, exact numbers, 1.6/DLC-specific changes or mods, " +
            "preferring the RimWorld Wiki (rimworldwiki.com). Do not search for things the game tools can tell you about this colony. Keep it to 1-3 searches per question.";

        /// <summary>답변 본문에 링크가 없는 출처만 끝에 붙인다 (인용 출처 표시).</summary>
        static string FormatSources(List<KeyValuePair<string, string>> sources, string answer)
        {
            var missing = sources.Where(s => answer.IndexOf(s.Value, StringComparison.OrdinalIgnoreCase) < 0).Take(6).ToList();
            if (missing.Count == 0) return "";
            var sb = new StringBuilder("\n\n" + "AIAdvisor_Sources".TranslateSafe());
            foreach (var s in missing)
                sb.Append("\n- ").Append(s.Key == s.Value ? s.Value : s.Key + " (" + s.Value + ")");
            return sb.ToString();
        }

        static void RecordUsage(ProviderKind provider, string model, Usage usage)
        {
            var price = Providers.PriceFor(provider, model);
            double? cost = usage.Cost(price, Providers.WebSearchCostPerCall(provider, model));
            MainThread.Post(() =>
            {
                AdvisorMod.Settings.RecordUsage(usage, cost);
                if (cost.HasValue)
                {
                    SessionCost += cost.Value;
                    LastAnswerCost += cost.Value;
                }
                else SessionCostUnknown = true;
            });
        }

        static void Post(EntryKind kind, string text) => MainThread.Post(() => AddEntry(kind, text));

        static void SetStatus(string text) => MainThread.Post(() => Status = text);

        static string BuildSystemPrompt()
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are an expert RimWorld strategy advisor embedded in the player's running game (RimWorld " + VersionControl.CurrentVersionString + ").");
            sb.AppendLine("You can read the live game state through the provided tools. Rules:");
            sb.AppendLine("- Before answering questions about the colony, call the relevant tools to read the actual data. Never invent colonist names, numbers or items.");
            sb.AppendLine("- Call several tools in parallel when you need several kinds of data.");
            sb.AppendLine("- Give concrete, prioritized, actionable advice grounded in the data (who should do what, what to build or research next, what the risks are).");
            sb.AppendLine("- Be concise. The answer is shown in a small in-game chat window: plain text, short paragraphs or '-' bullet lines. Do not use markdown tables or headings. **bold** is fine for key words.");
            sb.AppendLine("- Always reply in the same language the player writes in. The game UI language is " + LanguageDatabase.activeLanguage?.FriendlyNameEnglish + "; use the game's in-game names for items and colonists.");
            sb.AppendLine("- If a tool fails or data is missing, say so briefly and answer with general RimWorld knowledge, clearly marked as general.");
            var dlcs = new List<string>();
            if (ModsConfig.RoyaltyActive) dlcs.Add("Royalty");
            if (ModsConfig.IdeologyActive) dlcs.Add("Ideology");
            if (ModsConfig.BiotechActive) dlcs.Add("Biotech");
            if (ModsConfig.AnomalyActive) dlcs.Add("Anomaly");
            if (ModsConfig.OdysseyActive) dlcs.Add("Odyssey");
            sb.AppendLine("Active DLCs: " + (dlcs.Count > 0 ? string.Join(", ", dlcs) : "none (base game only)") + ".");
            var mods = LoadedModManager.RunningModsListForReading
                .Where(m => !m.IsOfficialMod && !m.IsCoreMod && m.PackageId != "dsjang.aiadvisor")
                .Select(m => m.Name).Take(60).ToList();
            if (mods.Count > 0) sb.AppendLine("Other active mods: " + string.Join(", ", mods) + ".");
            return sb.ToString();
        }
    }
}
