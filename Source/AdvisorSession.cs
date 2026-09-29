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

        // 화면에 그릴 모양은 매 프레임 다시 만들지 않도록 기억해 둔다 (저장하지 않음)
        internal string formattedFrom;
        internal string formatted;
        internal List<string> urls;
    }

    /// <summary>
    /// 대화 상태와 에이전트 루프. 루프는 백그라운드 Task 에서 돌고,
    /// 게임 데이터 읽기와 UI 상태 변경은 MainThread 로 넘긴다.
    /// 대화는 불러온 정착지마다 따로 두고 SessionStore 로 저장한다.
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
        /// <summary>스트리밍으로 받고 있는 답변. 응답 하나가 끝나면 비우고 정식 메시지로 옮긴다.</summary>
        public static string LiveText = "";

        /// <summary>
        /// AI 가 기억하는 대화 (질문과 최종 답변만). 요청할 때마다 이걸로 회사별 메시지를 새로 만들기 때문에
        /// 회사나 모델을 바꿔도 대화가 이어진다. 도구 결과는 그 질문 안에서만 쓰고 남기지 않는다.
        /// </summary>
        static readonly List<Turn> turns = new List<Turn>();
        static CancellationTokenSource cts;
        static TimeSpeed speedBeforePause = TimeSpeed.Paused;
        static string systemPrompt;

        /// <summary>지금 대화가 연결된 게임과 저장 키.</summary>
        static Game boundGame;
        static string sessionKey;
        static string colonyName;
        /// <summary>게임이 바뀌거나 새 대화를 시작할 때마다 올라간다. 이전 요청의 늦은 결과가 새 대화에 섞이지 않게 한다.</summary>
        static int generation;

        public static string SessionKey => sessionKey;
        public static int TurnCount => turns.Count;
        /// <summary>게임에 연결된 횟수 (자체 점검용).</summary>
        public static int BindCount { get; private set; }

        /// <summary>매 프레임 메인 스레드에서 호출. 불러온 게임이 바뀌면 그 정착지의 대화로 바꾼다.</summary>
        public static void Tick()
        {
            var game = Current.Game;
            if (game == boundGame) return;
            if (boundGame != null) Unbind();
            // 불러오는 중에는 월드 정보가 아직 없으니 게임이 시작된 뒤에 연결한다
            if (game != null && Current.ProgramState == ProgramState.Playing && Find.World != null) Bind(game);
        }

        static void Bind(Game game)
        {
            BindCount++;
            boundGame = game;
            sessionKey = SessionStore.CurrentKey();
            colonyName = SessionStore.CurrentColonyName();
            var data = sessionKey != null ? SessionStore.Load(sessionKey) : new SessionStore.Data();
            ResetState();
            Entries.AddRange(data.entries);
            SessionCost = data.cost;
            SessionCostUnknown = data.costUnknown;

            // 더 예전 세이브를 불러왔다면 그 뒤에 나눈 문답은 지금 게임에서 일어나지 않은 일이라 AI 기억에서 뺀다
            int now = Find.TickManager.TicksGame;
            int future = data.turns.Count(t => t.tick > now);
            turns.AddRange(data.turns.Where(t => t.tick <= now));
            if (future > 0)
            {
                AddEntry(EntryKind.Info, "AIAdvisor_RewoundSave".Translate(future));
                SaveNow();
            }
        }

        static void Unbind()
        {
            if (Busy) cts?.Cancel();
            SaveNow();
            ResetState();
            boundGame = null;
            sessionKey = null;
            colonyName = null;
        }

        static void ResetState()
        {
            generation++;
            Entries.Clear();
            turns.Clear();
            SessionCost = 0;
            LastAnswerCost = 0;
            SessionCostUnknown = false;
            Status = "";
            LiveText = "";
            Version++;
        }

        /// <summary>지금 대화를 파일에 저장. 메인 스레드에서 호출.</summary>
        public static void SaveNow()
        {
            if (sessionKey == null) return;
            // 정착지 이름은 바꿀 수 있어서, 게임이 살아 있을 때는 매번 새로 읽는다
            if (Current.Game != null && Current.Game == boundGame) colonyName = SessionStore.CurrentColonyName();
            SessionStore.Save(sessionKey, new SessionStore.Data
            {
                colony = colonyName,
                entries = new List<ChatEntry>(Entries),
                turns = new List<Turn>(turns),
                cost = SessionCost,
                costUnknown = SessionCostUnknown,
            });
        }

        public static void Clear()
        {
            if (Busy) return;
            ResetState();
            SaveNow();
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

            var tm = Find.TickManager;
            speedBeforePause = tm.CurTimeSpeed;
            if (s.pauseOnSend && !tm.Paused) tm.Pause();

            systemPrompt = systemPrompt ?? BuildSystemPrompt();
            var client = LlmClient.Create(provider, s.apiKey, model);
            client.webSearch = s.webSearch;
            client.stream = s.stream;
            int gen = generation;
            client.TextDelta = delta => MainThread.Post(() =>
            {
                if (gen != generation) return;
                if (LiveText.Length == 0) Status = "AIAdvisor_Writing".Translate();
                LiveText += delta;
                Version++;
            });
            client.StreamReset = () => MainThread.Post(() =>
            {
                if (gen != generation) return;
                LiveText = "";
                Version++;
            });
            var specs = GameTools.Specs;
            AddEntry(EntryKind.User, text);

            var turn = new Turn { question = text, tick = tm.TicksGame, time = GameTimeLabel() };
            var messages = BuildMessages(client, turn);

            Busy = true;
            Status = "AIAdvisor_Thinking".Translate();
            LlmClient.RetryNotice = (wait, attempt, max, reason) =>
                SetStatus("AIAdvisor_Retrying".TranslateSafe().Replace("{0}", wait.ToString()).Replace("{1}", attempt.ToString()).Replace("{2}", max.ToString()).Replace("{3}", reason));
            LastAnswerCost = 0;
            cts = new CancellationTokenSource();
            var token = cts.Token;
            int maxRounds = s.maxToolRounds;
            Task.Run(() => RunLoop(client, provider, model, specs, messages, turn, gen, maxRounds, token));
        }

        /// <summary>설정과 별개로, 이전 문답을 이 글자 수까지만 보낸다 (한국어는 대략 글자당 1토큰).</summary>
        const int MaxMemoryChars = 16000;

        /// <summary>
        /// 이전 문답과 이번 질문을 이 회사의 메시지 형식으로 만든다. 최근 문답부터 설정한 개수와 글자 수 한도까지 넣는다.
        /// 질문 앞에는 보낸 게임 시각을 붙여서, 모델이 예전 답의 수치가 언제 기준인지 알게 한다.
        /// </summary>
        static List<object> BuildMessages(LlmClient client, Turn current)
        {
            var remembered = new List<Turn>();
            int chars = 0;
            for (int i = turns.Count - 1; i >= 0 && remembered.Count < AdvisorMod.Settings.memoryTurns; i--)
            {
                int len = turns[i].question.Length + turns[i].answer.Length;
                if (chars + len > MaxMemoryChars) break;
                chars += len;
                remembered.Insert(0, turns[i]);
            }
            var messages = new List<object>();
            foreach (var t in remembered)
            {
                messages.Add(client.UserMessage(Stamped(t)));
                messages.Add(client.AssistantMessage(t.answer));
            }
            messages.Add(client.UserMessage(Stamped(current)));
            return messages;
        }

        static string Stamped(Turn t) => string.IsNullOrEmpty(t.time) ? t.question : "[" + t.time + "] " + t.question;

        static async Task RunLoop(LlmClient client, ProviderKind provider, string model, List<ToolSpec> specs, List<object> messages, Turn turn, int gen, int maxRounds, CancellationToken ct)
        {
            var sources = new List<KeyValuePair<string, string>>();
            try
            {
                for (int round = 0; ; round++)
                {
                    bool lastRound = round >= maxRounds;
                    string system = systemPrompt;
                    if (client.WebSearchActive) system += WebSearchHint;
                    if (lastRound) system += "\n\nYou have used all tool calls for this question. Answer now with the information you already have.";
                    var reply = await client.Complete(system, new List<object>(messages), specs, ct, allowTools: !lastRound).ConfigureAwait(false);
                    // 스트리밍으로 보여 주던 글자는 아래에서 정식 메시지로 다시 올린다 (같은 프레임에 처리돼서 깜빡이지 않는다)
                    ClearLiveText(gen);
                    RecordUsage(provider, model, reply.usage, gen);
                    if (reply.webSearchRejected != null)
                        Post(gen, EntryKind.Info, "AIAdvisor_WebSearchRejected".TranslateSafe().Replace("{0}", reply.webSearchRejected));

                    if (reply.refused)
                    {
                        Post(gen, EntryKind.Error, "AIAdvisor_Refused".TranslateSafe() + (reply.text.Length > 0 ? "\n" + reply.text : ""));
                        return;
                    }

                    messages.AddRange(reply.historyItems);
                    if (reply.webSearchQueries.Count > 0)
                        Post(gen, EntryKind.Tool, "AIAdvisor_WebSearchUse".TranslateSafe() + " " + string.Join(" / ", reply.webSearchQueries));
                    foreach (var src in reply.sources)
                        if (!sources.Any(x => x.Value == src.Value)) sources.Add(src);

                    if (reply.toolCalls.Count == 0 || reply.truncated || lastRound)
                    {
                        // 도구 금지를 무시하는 서버(일부 로컬 모델)는 마지막에도 도구만 부르고 끝날 수 있다
                        string empty = reply.toolCalls.Count > 0 ? "AIAdvisor_RoundLimitNoAnswer" : "AIAdvisor_EmptyAnswer";
                        string answer = reply.text.Length > 0 ? reply.text : empty.TranslateSafe();
                        answer += FormatSources(sources, answer);
                        if (reply.truncated || reply.unfinished) answer += "\n\n" + "AIAdvisor_Truncated".TranslateSafe();
                        // 조회 한도에 걸리거나 잘린 답이어도 플레이어가 본 답은 기억한다 (출처 목록 같은 UI 장식은 빼고)
                        if (reply.text.Length > 0) turn.answer = reply.text;
                        MainThread.Post(() =>
                        {
                            if (gen != generation) return;
                            AddEntry(EntryKind.Assistant, answer);
                            if (turn.answer != null) turns.Add(turn);
                        });
                        return;
                    }

                    if (reply.text.Length > 0) Post(gen, EntryKind.Info, reply.text);
                    string names = string.Join(", ", reply.toolCalls.Select(c => c.name));
                    Post(gen, EntryKind.Tool, "AIAdvisor_ToolUse".TranslateSafe() + " " + names);
                    SetStatus("AIAdvisor_ReadingData".TranslateSafe() + " " + names);

                    var outputs = await MainThread.Invoke(() =>
                    {
                        // 그사이 다른 게임을 불러왔다면 그 게임 데이터를 읽지 않는다
                        if (gen != generation) throw new OperationCanceledException();
                        return reply.toolCalls.Select(GameTools.Execute).ToList();
                    }).ConfigureAwait(false);
                    messages.AddRange(client.ToolResultMessages(outputs));
                    SetStatus("AIAdvisor_Thinking".TranslateSafe());
                }
            }
            catch (OperationCanceledException)
            {
                Post(gen, EntryKind.Info, "AIAdvisor_Cancelled".TranslateSafe());
            }
            catch (Exception e)
            {
                Post(gen, EntryKind.Error, "AIAdvisor_Error".TranslateSafe() + " " + e.Message);
                if (!(e is LlmException)) Log.Warning("[RimSage] " + e);
            }
            finally
            {
                MainThread.Post(() =>
                {
                    Busy = false;
                    Status = "";
                    if (gen == generation) LiveText = "";
                    var s = AdvisorMod.Settings;
                    if (gen == generation)
                    {
                        if (s.resumeAfterAnswer && s.pauseOnSend && speedBeforePause != TimeSpeed.Paused && Find.TickManager != null && Find.TickManager.Paused)
                            Find.TickManager.CurTimeSpeed = speedBeforePause;
                        SaveNow();
                    }
                    AdvisorMod.Save();
                });
            }
        }

        /// <summary>
        /// 질문 시각. get_colony_overview 의 date / hour 와 같은 달력 날짜라 순서를 헷갈리지 않는다
        /// (days_passed 는 게임 시작 시각부터 세서 자정을 넘겨도 그대로일 때가 있다).
        /// </summary>
        static string GameTimeLabel()
        {
            var map = Find.CurrentMap;
            var longLat = map != null ? Find.WorldGrid.LongLatOf(map.Tile) : UnityEngine.Vector2.zero;
            return GenDate.DateFullStringAt(GenTicks.TicksAbs, longLat) + ", " + GenDate.HourOfDay(GenTicks.TicksAbs, longLat.x) + "h";
        }

        const string WebSearchHint =
            "\n- You also have a web_search tool. Use it for how game mechanics work, strategies and 1.6/DLC changes that the game data cannot tell you, " +
            "preferring the RimWorld Wiki (rimworldwiki.com). For numbers and stats use lookup_def first, and do not search for things the game tools can tell you about this colony. Keep it to 1-3 searches per question.";

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

        static void RecordUsage(ProviderKind provider, string model, Usage usage, int gen)
        {
            var price = Providers.PriceFor(provider, model);
            double? cost = usage.Cost(price, Providers.WebSearchCostPerCall(provider, model));
            MainThread.Post(() =>
            {
                // 누적 비용은 실제로 쓴 돈이라 대화가 바뀌었어도 기록한다
                AdvisorMod.Settings.RecordUsage(usage, cost);
                if (gen != generation) return;
                if (cost.HasValue)
                {
                    SessionCost += cost.Value;
                    LastAnswerCost += cost.Value;
                }
                else SessionCostUnknown = true;
            });
        }

        static void Post(int gen, EntryKind kind, string text) => MainThread.Post(() =>
        {
            if (gen == generation) AddEntry(kind, text);
        });

        static void SetStatus(string text) => MainThread.Post(() => Status = text);

        static void ClearLiveText(int gen) => MainThread.Post(() =>
        {
            if (gen != generation || LiveText.Length == 0) return;
            LiveText = "";
            Version++;
        });

        static string BuildSystemPrompt()
        {
            var sb = new StringBuilder();
            sb.AppendLine("You are an expert RimWorld strategy advisor embedded in the player's running game (RimWorld " + VersionControl.CurrentVersionString + ").");
            sb.AppendLine("You can read the live game state through the provided tools. Rules:");
            sb.AppendLine("- Before answering questions about the colony, call the relevant tools to read the actual data. Never invent colonist names, numbers or items.");
            sb.AppendLine("- Call several tools in parallel when you need several kinds of data.");
            sb.AppendLine("- For exact stats of any item, weapon, building, crop, animal, research, recipe, disease or trait, use lookup_def: it reads this game's own data, including mods. Don't guess numbers.");
            sb.AppendLine("- For layout, defense and building-placement questions, look at the map with get_map_layout before answering.");
            sb.AppendLine("- Give concrete, prioritized, actionable advice grounded in the data (who should do what, what to build or research next, what the risks are).");
            sb.AppendLine("- Be concise. The answer is shown in a small in-game chat window: plain text, short paragraphs or '-' bullet lines. Do not use markdown tables or headings. **bold** is fine for key words.");
            sb.AppendLine("- Always reply in the same language the player writes in. The game UI language is " + LanguageDatabase.activeLanguage?.FriendlyNameEnglish + "; use the game's in-game names for items and colonists.");
            sb.AppendLine("- If a tool fails or data is missing, say so briefly and answer with general RimWorld knowledge, clearly marked as general.");
            sb.AppendLine("- Each player message starts with [date, hour]: the in-game time it was sent (same calendar as date and hour in get_colony_overview). Earlier answers describe the colony at their own time and the game has moved on since. Tool results from earlier questions are not kept, so read the current state with the tools again instead of reusing numbers from earlier answers.");
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
