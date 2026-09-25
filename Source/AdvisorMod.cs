using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Verse;

namespace AIAdvisor
{
    public class AdvisorMod : Mod
    {
        public static AdvisorSettings Settings;
        public static AdvisorMod Instance;

        bool showKey;
        string spendLimitBuffer;
        string mcpPortBuffer;
        static string status = "";
        static bool statusBusy;

        public AdvisorMod(ModContentPack content) : base(content)
        {
            Instance = this;
            Settings = GetSettings<AdvisorSettings>();
            // 모드 생성자는 로딩 스레드에서 불릴 수 있어서 Unity 객체는 로딩이 끝난 뒤 메인 스레드에서 만든다
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                AdvisorRunner.Ensure();
                if (Settings.mcpEnabled) McpServer.Start(Settings.mcpPort);
            });
        }

        public override string SettingsCategory() => "AI Advisor";

        public static void Save() => Instance?.WriteSettings();

        Vector2 settingsScroll;
        float settingsHeight = 1200f;

        public override void DoSettingsWindowContents(Rect inRect)
        {
            // AdvisorRunner 가 매 프레임 처리하지만, 설정 창이 열려 있을 때 응답을 조금 더 빨리 반영한다
            MainThread.Drain();
            var s = Settings;

            // 항목이 많아서 스크롤 영역 안에 그린다
            Rect view = new Rect(0f, 0f, inRect.width - 20f, settingsHeight);
            Widgets.BeginScrollView(inRect, ref settingsScroll, view);
            // maxOneColumn: 내용이 저장된 높이보다 길어지면 Listing 이 오른쪽 새 열로 넘겨 그려서
            // (화면 밖이라 안 보임) 높이가 계속 줄어드는 문제가 있었다. 항상 한 열로 그린다.
            var ls = new Listing_Standard { maxOneColumn = true };
            ls.Begin(view);

            // --- connection mode
            Rect modeRow = ls.GetRect(30f);
            Widgets.Label(new Rect(modeRow.x, modeRow.y + 4f, 120f, modeRow.height), "AIAdvisor_ConnectionMode".Translate());
            if (Widgets.ButtonText(new Rect(modeRow.x + 120f, modeRow.y, 160f, modeRow.height), "AIAdvisor_ModeApiKey".Translate(), active: s.useLocal))
                s.useLocal = false;
            if (Widgets.ButtonText(new Rect(modeRow.x + 290f, modeRow.y, 160f, modeRow.height), "AIAdvisor_ModeLocal".Translate(), active: !s.useLocal))
                s.useLocal = true;
            GUI.color = new Color(0.6f, 1f, 0.6f);
            Widgets.Label(new Rect(modeRow.x + 460f, modeRow.y + 4f, 300f, modeRow.height), "AIAdvisor_ModeCurrent".Translate(s.useLocal ? "AIAdvisor_ModeLocal".Translate() : "AIAdvisor_ModeApiKey".Translate()));
            GUI.color = Color.white;
            ls.GapLine();

            if (s.useLocal) DrawLocalSection(ls, s);
            else DrawApiKeySection(ls, s);

            var provider = s.Provider;
            if (provider != ProviderKind.None)
            {
                Rect er = ls.GetRect(30f);
                Widgets.Label(new Rect(er.x, er.y + 4f, 120f, er.height), "AIAdvisor_Effort".Translate());
                if (Widgets.ButtonText(new Rect(er.x + 120f, er.y, 200f, er.height), CurrentEffortLabel(), active: EffortSelectable()))
                    OpenEffortMenu();
                ls.Label("AIAdvisor_EffortHelp".Translate());
            }
            if (!string.IsNullOrEmpty(status)) ls.Label(status);
            ls.GapLine();

            // --- behaviour
            ls.CheckboxLabeled("AIAdvisor_PauseOnSend".Translate(), ref s.pauseOnSend);
            ls.CheckboxLabeled("AIAdvisor_ResumeAfter".Translate(), ref s.resumeAfterAnswer);
            ls.Label("AIAdvisor_MaxToolRounds".Translate(s.maxToolRounds));
            s.maxToolRounds = Mathf.RoundToInt(ls.Slider(s.maxToolRounds, 1, 20));

            Rect limitRow = ls.GetRect(30f);
            Widgets.Label(new Rect(limitRow.x, limitRow.y + 4f, 330f, limitRow.height), "AIAdvisor_SpendLimit".Translate());
            if (spendLimitBuffer == null) spendLimitBuffer = s.spendLimitUsd.ToString(CultureInfo.InvariantCulture);
            Widgets.TextFieldNumeric(new Rect(limitRow.x + 330f, limitRow.y, 100f, limitRow.height), ref s.spendLimitUsd, ref spendLimitBuffer, 0f, 100000f);
            ls.GapLine();

            DrawMcpSection(ls, s);

            // --- usage
            ls.Label("AIAdvisor_UsageTotal".Translate(FormatCost(s.totalCostUsd), s.totalRequests, FormatTokens(s.totalInputTokens), FormatTokens(s.totalOutputTokens)));
            if (s.unpricedRequests > 0) ls.Label("AIAdvisor_UnpricedNote".Translate(s.unpricedRequests));
            if (ls.ButtonText("AIAdvisor_ResetUsage".Translate()))
                s.ResetStats();
            ls.Gap();
            GUI.color = Color.gray;
            ls.Label("AIAdvisor_KeyStorageNote".Translate());
            GUI.color = Color.white;

            settingsHeight = ls.CurHeight + 20f;
            ls.End();
            Widgets.EndScrollView();
        }

        void DrawApiKeySection(Listing_Standard ls, AdvisorSettings s)
        {
            ls.Label("AIAdvisor_ApiKey".Translate());
            ls.Label("AIAdvisor_ApiKeyHelp".Translate());
            Rect keyRow = ls.GetRect(30f);
            Rect keyField = new Rect(keyRow.x, keyRow.y, keyRow.width - 200f, keyRow.height);
            string newKey = showKey ? Widgets.TextField(keyField, s.apiKey) : GUI.PasswordField(keyField, s.apiKey, '*', Text.CurTextFieldStyle);
            if (newKey != s.apiKey) s.apiKey = newKey.Trim();
            if (Widgets.ButtonText(new Rect(keyField.xMax + 5f, keyRow.y, 95f, keyRow.height), showKey ? "AIAdvisor_Hide".Translate() : "AIAdvisor_Show".Translate()))
                showKey = !showKey;
            if (Widgets.ButtonText(new Rect(keyField.xMax + 105f, keyRow.y, 95f, keyRow.height), "AIAdvisor_Paste".Translate()))
                s.apiKey = (GUIUtility.systemCopyBuffer ?? "").Trim();

            var provider = s.Provider;
            ls.Label("AIAdvisor_DetectedProvider".Translate(provider == ProviderKind.None
                ? (string.IsNullOrEmpty(s.apiKey) ? "-" : "AIAdvisor_UnknownKey".Translate().ToString())
                : Providers.Label(provider)));
            ls.GapLine();

            if (provider != ProviderKind.None)
            {
                string model = s.EffectiveModel();
                Rect row = ls.GetRect(30f);
                Widgets.Label(new Rect(row.x, row.y + 4f, 120f, row.height), "AIAdvisor_Model".Translate());
                if (Widgets.ButtonText(new Rect(row.x + 120f, row.y, 330f, row.height), ModelLabel(provider, model)))
                    OpenModelMenu();
                if (Widgets.ButtonText(new Rect(row.x + 460f, row.y, 200f, row.height), "AIAdvisor_FetchModels".Translate()) && !statusBusy)
                    FetchModels();
                ls.Label("AIAdvisor_PriceNote".Translate());
            }
        }

        void DrawMcpSection(Listing_Standard ls, AdvisorSettings s)
        {
            // 큰 글꼴은 한글 글꼴 텍스처를 많이 차지해서 쓰지 않는다
            GUI.color = new Color(1f, 0.85f, 0.5f);
            ls.Label("AIAdvisor_McpTitle".Translate());
            GUI.color = Color.white;
            ls.Label("AIAdvisor_McpHelp".Translate());

            bool enabled = s.mcpEnabled;
            ls.CheckboxLabeled("AIAdvisor_McpEnable".Translate(), ref enabled);
            Rect portRow = ls.GetRect(30f);
            Widgets.Label(new Rect(portRow.x, portRow.y + 4f, 120f, portRow.height), "AIAdvisor_McpPort".Translate());
            if (mcpPortBuffer == null) mcpPortBuffer = s.mcpPort.ToString();
            int port = s.mcpPort;
            Widgets.TextFieldNumeric(new Rect(portRow.x + 120f, portRow.y, 100f, portRow.height), ref port, ref mcpPortBuffer, 1024, 65535);
            bool restart = Widgets.ButtonText(new Rect(portRow.x + 230f, portRow.y, 140f, portRow.height), "AIAdvisor_McpApply".Translate(), active: enabled);

            if (enabled != s.mcpEnabled || port != s.mcpPort || restart)
            {
                bool portChanged = port != s.mcpPort;
                s.mcpEnabled = enabled;
                s.mcpPort = port;
                // 포트 입력 중에는 서버를 계속 재시작하지 않도록, 켜고 끌 때와 '적용'을 눌렀을 때만 반영
                if (!enabled) McpServer.Stop();
                else if (restart || !McpServer.Running || !portChanged) McpServer.Start(s.mcpPort);
                Save();
            }

            string url = McpServer.Url(McpServer.Running ? McpServer.Port : s.mcpPort);
            if (McpServer.Running)
            {
                GUI.color = new Color(0.6f, 1f, 0.6f);
                ls.Label("AIAdvisor_McpRunning".Translate(url, McpServer.RequestCount));
            }
            else if (s.mcpEnabled && McpServer.LastError != null)
            {
                GUI.color = new Color(1f, 0.6f, 0.6f);
                ls.Label("AIAdvisor_McpFailed".Translate(McpServer.LastError));
            }
            else
            {
                GUI.color = Color.gray;
                ls.Label("AIAdvisor_McpOff".Translate());
            }
            GUI.color = Color.white;

            if (s.mcpEnabled)
            {
                ls.Label("AIAdvisor_McpCommands".Translate());
                CommandRow(ls, "Claude Code", "claude mcp add --transport http --scope user rimworld " + url);
                CommandRow(ls, "Codex CLI", "codex mcp add rimworld --url " + url);
                CommandRow(ls, "Gemini CLI", "gemini mcp add --transport http rimworld " + url);
                GUI.color = Color.gray;
                ls.Label("AIAdvisor_McpUsage".Translate());
                GUI.color = Color.white;
            }
            ls.GapLine();
        }

        static void CommandRow(Listing_Standard ls, string app, string command)
        {
            Rect row = ls.GetRect(28f);
            Widgets.Label(new Rect(row.x, row.y + 4f, 110f, row.height), app);
            Rect field = new Rect(row.x + 110f, row.y, row.width - 110f - 90f, row.height);
            Widgets.TextField(field, command);
            if (Widgets.ButtonText(new Rect(field.xMax + 5f, row.y, 85f, row.height), "AIAdvisor_Copy".Translate()))
            {
                GUIUtility.systemCopyBuffer = command;
                Messages.Message("AIAdvisor_Copied".Translate(), RimWorld.MessageTypeDefOf.SilentInput, false);
            }
        }

        void DrawLocalSection(Listing_Standard ls, AdvisorSettings s)
        {
            ls.Label("AIAdvisor_LocalHelp".Translate());

            Rect urlRow = ls.GetRect(30f);
            Widgets.Label(new Rect(urlRow.x, urlRow.y + 4f, 120f, urlRow.height), "AIAdvisor_LocalUrl".Translate());
            Rect urlField = new Rect(urlRow.x + 120f, urlRow.y, urlRow.width - 120f - 230f, urlRow.height);
            string url = Widgets.TextField(urlField, s.localBaseUrl);
            if (url != s.localBaseUrl) s.localBaseUrl = url.Trim();
            if (Widgets.ButtonText(new Rect(urlField.xMax + 5f, urlRow.y, 110f, urlRow.height), "Ollama"))
                s.localBaseUrl = AdvisorSettings.OllamaUrl;
            if (Widgets.ButtonText(new Rect(urlField.xMax + 120f, urlRow.y, 110f, urlRow.height), "LM Studio"))
                s.localBaseUrl = AdvisorSettings.LmStudioUrl;

            string model = s.EffectiveModel() ?? "";
            Rect row = ls.GetRect(30f);
            Widgets.Label(new Rect(row.x, row.y + 4f, 120f, row.height), "AIAdvisor_Model".Translate());
            Rect modelField = new Rect(row.x + 120f, row.y, 290f, row.height);
            string typed = Widgets.TextField(modelField, model);
            if (typed != model) s.localModel = typed.Trim();
            if (Widgets.ButtonText(new Rect(modelField.xMax + 5f, row.y, 35f, row.height), "...", active: s.fetchedModels.Count > 0))
                OpenModelMenu();
            if (Widgets.ButtonText(new Rect(modelField.xMax + 50f, row.y, 200f, row.height), "AIAdvisor_FetchModels".Translate()) && !statusBusy)
                FetchModels();
            ls.Label("AIAdvisor_LocalModelHelp".Translate());
            ls.GapLine();
        }

        // ---------------------------------------------------------------- shared UI helpers

        public static string EffortLabel(string effort)
        {
            return string.IsNullOrEmpty(effort) ? "AIAdvisor_EffortDefault".Translate().ToString() : effort;
        }

        /// <summary>현재 모델이 추론 강도를 하나라도 받는지.</summary>
        public static bool EffortSelectable()
        {
            var s = Settings;
            return s.Provider != ProviderKind.None && Providers.EffortsFor(s.Provider, s.EffectiveModel()).Count > 1;
        }

        public static string CurrentEffortLabel()
        {
            var s = Settings;
            if (!EffortSelectable()) return "AIAdvisor_EffortUnsupported".Translate();
            return EffortLabel(s.EffectiveEffort(s.Provider, s.EffectiveModel()));
        }

        public static void OpenEffortMenu()
        {
            var s = Settings;
            var provider = s.Provider;
            if (!EffortSelectable()) return;
            var opts = new List<FloatMenuOption>();
            foreach (var e in Providers.EffortsFor(provider, s.EffectiveModel()))
            {
                string ec = e;
                opts.Add(new FloatMenuOption(EffortLabel(ec), () => { s.SetEffort(provider, ec); Save(); }));
            }
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        public static string ModelLabel(ProviderKind provider, string modelId)
        {
            if (string.IsNullOrEmpty(modelId)) return provider == ProviderKind.Local ? "AIAdvisor_PickModel".Translate().ToString() : "-";
            var price = Providers.PriceFor(provider, modelId);
            string label = price.label ?? modelId;
            if (provider == ProviderKind.Local) return label + "  (" + "AIAdvisor_Free".Translate() + ")";
            return price.Known ? label + "  ($" + FormatPrice(price.inputPerM) + " / $" + FormatPrice(price.outputPerM) + ")" : label + "  ($?)";
        }

        static string FormatPrice(float perM) => perM.ToString(perM < 1f ? "0.###" : "0.##", CultureInfo.InvariantCulture);

        public static void OpenModelMenu()
        {
            var s = Settings;
            var provider = s.Provider;
            if (provider == ProviderKind.None) return;
            var opts = new List<FloatMenuOption>();
            var seen = new HashSet<string>();
            foreach (var def in Providers.BuiltinModels(provider))
            {
                string id = def.modelId;
                seen.Add(id);
                opts.Add(new FloatMenuOption(ModelLabel(provider, id), () => SelectModel(provider, id)));
            }
            foreach (var m in s.fetchedModels.Where(m => !seen.Contains(m.modelId) && Providers.FindDef(provider, m.modelId)?.modelId != m.modelId))
            {
                string id = m.modelId;
                opts.Add(new FloatMenuOption(ModelLabel(provider, id), () => SelectModel(provider, id)));
            }
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        static void SelectModel(ProviderKind provider, string id)
        {
            if (provider == ProviderKind.Local) Settings.localModel = id;
            else
            {
                Settings.model = id;
                Settings.modelProvider = provider;
            }
            Save();
        }

        static void FetchModels()
        {
            var s = Settings;
            var provider = s.Provider;
            string key = s.apiKey;
            statusBusy = true;
            status = "AIAdvisor_Fetching".Translate();
            Task.Run(async () =>
            {
                try
                {
                    var client = LlmClient.Create(provider, key, s.EffectiveModel());
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                    {
                        var models = await client.ListModels(cts.Token).ConfigureAwait(false);
                        MainThread.Post(() =>
                        {
                            s.fetchedModels = models;
                            s.fetchedProvider = provider;
                            status = "AIAdvisor_FetchOk".Translate(models.Count);
                            Save();
                        });
                    }
                }
                catch (Exception e)
                {
                    string msg = e is OperationCanceledException ? "timeout" : e.Message;
                    MainThread.Post(() => status = "AIAdvisor_FetchFail".Translate(msg));
                }
                finally
                {
                    MainThread.Post(() => statusBusy = false);
                }
            });
        }

        public static string FormatCost(double usd)
        {
            if (usd <= 0) return "$0";
            if (usd < 0.01) return "$" + usd.ToString("0.0000", CultureInfo.InvariantCulture);
            if (usd < 1) return "$" + usd.ToString("0.000", CultureInfo.InvariantCulture);
            return "$" + usd.ToString("0.00", CultureInfo.InvariantCulture);
        }

        public static string FormatTokens(long n)
        {
            if (n >= 1_000_000) return (n / 1_000_000.0).ToString("0.##", CultureInfo.InvariantCulture) + "M";
            if (n >= 1000) return (n / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + "k";
            return n.ToString(CultureInfo.InvariantCulture);
        }
    }

    public static class LocExt
    {
        /// <summary>백그라운드 스레드에서도 쓸 수 있는 번역 헬퍼(번역 DB 읽기만 함).</summary>
        public static string TranslateSafe(this string key)
        {
            try { return key.Translate().ToString(); }
            catch { return key; }
        }
    }
}
