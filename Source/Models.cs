using System.Collections.Generic;
using System.Linq;
using Verse;

namespace AIAdvisor
{
    public enum ProviderKind
    {
        None,
        Anthropic,
        OpenAI,
        Gemini,
        OpenRouter,
        /// <summary>Ollama / LM Studio 같은 OpenAI 호환 로컬 서버. 키도 비용도 없다.</summary>
        Local,
    }

    /// <summary>Defs/AIModelDefs.xml 에 정의된 모델 한 개와 가격.</summary>
    public class AIModelDef : Def
    {
        public ProviderKind provider;
        public string modelId;
        public float inputPerM;
        public float outputPerM;
        public float cachedInputPerM = -1f;
        /// <summary>이 모델이 받는 추론 강도 값(약한 것부터). 비어 있으면 추론 강도를 보내지 않는다.</summary>
        public List<string> efforts = new List<string>();
        public bool serverFallback;
        public int order;
    }

    /// <summary>가격 정보. API 목록에서 가져온 모델(OpenRouter)도 이 형태로 저장한다.</summary>
    public class ModelPrice : IExposable
    {
        public string modelId;
        public string label;
        public float inputPerM = -1f;
        public float outputPerM = -1f;
        public float cachedInputPerM = -1f;
        /// <summary>지원하는 추론 강도. null 이면 알 수 없음(기본값만 허용).</summary>
        public List<string> efforts;

        public bool Known => inputPerM >= 0f && outputPerM >= 0f;

        public void ExposeData()
        {
            Scribe_Values.Look(ref modelId, "modelId");
            Scribe_Values.Look(ref label, "label");
            Scribe_Values.Look(ref inputPerM, "inputPerM", -1f);
            Scribe_Values.Look(ref outputPerM, "outputPerM", -1f);
            Scribe_Values.Look(ref cachedInputPerM, "cachedInputPerM", -1f);
            Scribe_Collections.Look(ref efforts, "efforts", LookMode.Value);
        }
    }

    public static class Providers
    {
        /// <summary>키 접두어로 어느 회사 키인지 판별한다.</summary>
        public static ProviderKind Detect(string key)
        {
            key = key?.Trim() ?? "";
            if (key.Length == 0) return ProviderKind.None;
            if (key.StartsWith("sk-ant-")) return ProviderKind.Anthropic;
            if (key.StartsWith("sk-or-")) return ProviderKind.OpenRouter;
            if (key.StartsWith("AIza")) return ProviderKind.Gemini;
            if (key.StartsWith("sk-")) return ProviderKind.OpenAI;
            return ProviderKind.None;
        }

        public static string Label(ProviderKind p)
        {
            switch (p)
            {
                case ProviderKind.Anthropic: return "Anthropic (Claude)";
                case ProviderKind.OpenAI: return "OpenAI";
                case ProviderKind.Gemini: return "Google Gemini";
                case ProviderKind.OpenRouter: return "OpenRouter";
                case ProviderKind.Local: return "AIAdvisor_ModeLocal".TranslateSafe();
                default: return "-";
            }
        }

        /// <summary>추론 강도 값을 약한 것부터 정렬할 때 쓰는 순서.</summary>
        public static readonly string[] EffortOrder = { "none", "minimal", "low", "medium", "high", "xhigh", "max" };

        /// <summary>
        /// 이 모델에서 고를 수 있는 추론 강도. 첫 항목 ""은 '모델 기본값'(파라미터를 보내지 않음).
        /// 모델별 지원 값이 제각각이라(예: gpt-6-luna 는 none~max, gpt-5-mini 는 minimal~high)
        /// Defs 나 OpenRouter 목록에서 확인된 값만 보여 준다.
        /// </summary>
        public static List<string> EffortsFor(ProviderKind p, string modelId)
        {
            var list = new List<string> { "" };
            var known = PriceFor(p, modelId).efforts;
            if (known != null) list.AddRange(known);
            return list;
        }

        /// <summary>캐시 가격이 명시되지 않았을 때 쓰는 입력 가격 대비 비율.</summary>
        public static float DefaultCacheReadRatio(ProviderKind p)
        {
            switch (p)
            {
                case ProviderKind.Anthropic: return 0.1f;
                case ProviderKind.OpenAI: return 0.1f;
                case ProviderKind.Gemini: return 0.25f;
                default: return 1f;
            }
        }

        /// <summary>
        /// 서버 웹 검색 1회당 요금(USD). Anthropic $10/1k. OpenAI 는 추론 모델 $10/1k, 비추론 모델(gpt-4.x) $25/1k.
        /// 검색 결과 토큰은 usage 의 입력 토큰에 이미 들어 있다. (2026-09 각 사 가격표)
        /// </summary>
        public static double WebSearchCostPerCall(ProviderKind p, string modelId)
        {
            switch (p)
            {
                case ProviderKind.Anthropic: return 0.01;
                case ProviderKind.OpenAI: return (modelId ?? "").StartsWith("gpt-4") ? 0.025 : 0.01;
                default: return 0;
            }
        }

        public static IEnumerable<AIModelDef> BuiltinModels(ProviderKind p)
        {
            return DefDatabase<AIModelDef>.AllDefsListForReading.Where(m => m.provider == p).OrderBy(m => m.order);
        }

        public static AIModelDef FindDef(ProviderKind p, string modelId)
        {
            if (string.IsNullOrEmpty(modelId)) return null;
            var defs = BuiltinModels(p).ToList();
            var exact = defs.FirstOrDefault(m => m.modelId == modelId);
            if (exact != null) return exact;
            // 날짜 스냅샷(예: gpt-5-mini-2026-01-01, claude-x-20260101)만 같은 모델로 본다.
            // gpt-5 가격이 gpt-5.5-pro 에 잘못 붙지 않도록 접두어 뒤는 '-숫자' 여야 한다.
            return defs.Where(m => modelId.Length > m.modelId.Length + 1
                                   && modelId.StartsWith(m.modelId + "-")
                                   && char.IsDigit(modelId[m.modelId.Length + 1]))
                       .OrderByDescending(m => m.modelId.Length).FirstOrDefault();
        }

        /// <summary>모델 가격을 찾는다. 내장 Def → API에서 받아온 가격 순서. 모르면 Known=false.</summary>
        public static ModelPrice PriceFor(ProviderKind p, string modelId)
        {
            // 로컬 모델은 항상 무료
            if (p == ProviderKind.Local)
                return new ModelPrice { modelId = modelId, label = modelId, inputPerM = 0f, outputPerM = 0f, cachedInputPerM = 0f };
            var def = FindDef(p, modelId);
            if (def != null)
            {
                return new ModelPrice
                {
                    modelId = def.modelId,
                    label = def.label,
                    inputPerM = def.inputPerM,
                    outputPerM = def.outputPerM,
                    cachedInputPerM = def.cachedInputPerM >= 0f ? def.cachedInputPerM : def.inputPerM * DefaultCacheReadRatio(p),
                    efforts = def.efforts,
                };
            }
            var fetched = AdvisorMod.Settings.fetchedModels.FirstOrDefault(m => m.modelId == modelId);
            if (fetched != null) return fetched;
            return new ModelPrice { modelId = modelId, label = modelId };
        }
    }
}
