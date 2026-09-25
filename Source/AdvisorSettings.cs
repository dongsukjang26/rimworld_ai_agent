using System.Collections.Generic;
using Verse;

namespace AIAdvisor
{
    /// <summary>
    /// 모드 설정. RimWorld Config 폴더의 Mod_dsjang.aiadvisor_AdvisorMod.xml 에 평문으로 저장된다.
    /// </summary>
    public class AdvisorSettings : ModSettings
    {
        public string apiKey = "";
        /// <summary>true 면 API 키 대신 로컬 모델 서버(Ollama / LM Studio 등 OpenAI 호환)를 쓴다.</summary>
        public bool useLocal;
        public const string OllamaUrl = "http://localhost:11434/v1/";
        public const string LmStudioUrl = "http://localhost:1234/v1/";
        public string localBaseUrl = OllamaUrl;
        public string localModel = "";
        public string model = "";
        /// <summary>model 을 고를 때의 provider. 키가 다른 회사 것으로 바뀌면 모델 선택을 초기화한다.</summary>
        public ProviderKind modelProvider = ProviderKind.None;
        /// <summary>Anthropic effort. 빈 문자열이면 모델 기본값.</summary>
        public string effort = "";
        /// <summary>OpenAI / Gemini / OpenRouter reasoning effort. 빈 문자열이면 모델 기본값.</summary>
        public string reasoningEffort = "";
        public bool pauseOnSend = true;
        public bool resumeAfterAnswer = false;
        public int maxToolRounds = 8;
        /// <summary>Anthropic / OpenAI 서버의 웹 검색 도구를 쓴다. 검색 1회당 약 $0.01 추가.</summary>
        public bool webSearch = true;
        /// <summary>누적 비용이 이 금액(USD)을 넘으면 전송을 막는다. 0 이면 제한 없음.</summary>
        public float spendLimitUsd = 0f;

        /// <summary>외부 AI 도구 연결용 로컬 MCP 서버.</summary>
        public bool mcpEnabled;
        public const int DefaultMcpPort = 18765;
        public int mcpPort = DefaultMcpPort;

        public double totalCostUsd;
        public long totalInputTokens;
        public long totalOutputTokens;
        public int totalRequests;
        public int unpricedRequests;

        public List<ModelPrice> fetchedModels = new List<ModelPrice>();
        public ProviderKind fetchedProvider = ProviderKind.None;

        public ProviderKind Provider => useLocal ? ProviderKind.Local : Providers.Detect(apiKey);

        public string EffortFor(ProviderKind p) => p == ProviderKind.Anthropic ? effort : reasoningEffort;

        /// <summary>실제로 보낼 추론 강도. 저장된 값이 현재 모델에서 지원되지 않으면 기본값("")을 쓴다.</summary>
        public string EffectiveEffort(ProviderKind p, string modelId)
        {
            string e = EffortFor(p);
            return !string.IsNullOrEmpty(e) && Providers.EffortsFor(p, modelId).Contains(e) ? e : "";
        }

        public void SetEffort(ProviderKind p, string value)
        {
            if (p == ProviderKind.Anthropic) effort = value;
            else reasoningEffort = value;
        }

        public bool OverLimit => spendLimitUsd > 0f && totalCostUsd >= spendLimitUsd;

        /// <summary>키에 맞는 모델이 선택돼 있는지 확인하고, 아니면 첫 번째 내장 모델로 바꾼다.</summary>
        public string EffectiveModel()
        {
            var p = Provider;
            if (p == ProviderKind.None) return null;
            if (fetchedProvider != p)
            {
                fetchedModels.Clear();
                fetchedProvider = p;
            }
            if (p == ProviderKind.Local) return localModel ?? "";
            if (modelProvider != p || string.IsNullOrEmpty(model))
            {
                modelProvider = p;
                model = null;
                foreach (var m in Providers.BuiltinModels(p)) { model = m.modelId; break; }
            }
            return model;
        }

        public void RecordUsage(Usage usage, double? cost)
        {
            totalRequests++;
            totalInputTokens += usage.TotalInput;
            totalOutputTokens += usage.output;
            if (cost.HasValue) totalCostUsd += cost.Value;
            else unpricedRequests++;
        }

        public void ResetStats()
        {
            totalCostUsd = 0;
            totalInputTokens = 0;
            totalOutputTokens = 0;
            totalRequests = 0;
            unpricedRequests = 0;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref apiKey, "apiKey", "");
            Scribe_Values.Look(ref useLocal, "useLocal", false);
            Scribe_Values.Look(ref localBaseUrl, "localBaseUrl", OllamaUrl);
            Scribe_Values.Look(ref localModel, "localModel", "");
            Scribe_Values.Look(ref model, "model", "");
            Scribe_Values.Look(ref modelProvider, "modelProvider", ProviderKind.None);
            Scribe_Values.Look(ref effort, "effort", "");
            Scribe_Values.Look(ref reasoningEffort, "reasoningEffort", "");
            Scribe_Values.Look(ref pauseOnSend, "pauseOnSend", true);
            Scribe_Values.Look(ref resumeAfterAnswer, "resumeAfterAnswer", false);
            Scribe_Values.Look(ref maxToolRounds, "maxToolRounds", 8);
            Scribe_Values.Look(ref webSearch, "webSearch", true);
            Scribe_Values.Look(ref spendLimitUsd, "spendLimitUsd", 0f);
            Scribe_Values.Look(ref mcpEnabled, "mcpEnabled", false);
            Scribe_Values.Look(ref mcpPort, "mcpPort", DefaultMcpPort);
            Scribe_Values.Look(ref totalCostUsd, "totalCostUsd", 0);
            Scribe_Values.Look(ref totalInputTokens, "totalInputTokens", 0);
            Scribe_Values.Look(ref totalOutputTokens, "totalOutputTokens", 0);
            Scribe_Values.Look(ref totalRequests, "totalRequests", 0);
            Scribe_Values.Look(ref unpricedRequests, "unpricedRequests", 0);
            Scribe_Values.Look(ref fetchedProvider, "fetchedProvider", ProviderKind.None);
            Scribe_Collections.Look(ref fetchedModels, "fetchedModels", LookMode.Deep);
            if (fetchedModels == null) fetchedModels = new List<ModelPrice>();
            if (apiKey == null) apiKey = "";
            if (string.IsNullOrEmpty(localBaseUrl)) localBaseUrl = OllamaUrl;
            if (localModel == null) localModel = "";
        }
    }
}
