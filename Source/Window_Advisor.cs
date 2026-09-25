using System.Text.RegularExpressions;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIAdvisor
{
    public class Window_Advisor : Window
    {
        const string InputControl = "AIAdvisorInput";
        const float HeaderHeight = 58f;
        const float InputHeight = 64f;
        const float StatusHeight = 26f;

        string input = "";
        Vector2 scroll;
        int seenVersion = -1;
        bool focusInput = true;
        int sendInFrames = -1;

        static readonly Regex boldRegex = new Regex(@"\*\*(.+?)\*\*", RegexOptions.Compiled);
        static readonly Regex headingRegex = new Regex(@"^#{1,6}\s*", RegexOptions.Compiled | RegexOptions.Multiline);

        public Window_Advisor()
        {
            draggable = true;
            resizeable = true;
            doCloseX = true;
            closeOnAccept = false; // Enter 는 전송에 쓴다
            closeOnCancel = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = false;
            preventCameraMotion = false;
            forcePause = false;
            onlyOneOfTypeAllowed = true;
            layer = WindowLayer.GameUI;
        }

        public override Vector2 InitialSize => new Vector2(560f, 680f);

        protected override void SetInitialSizeAndPosition()
        {
            base.SetInitialSizeAndPosition();
            // 화면 오른쪽에 붙여서 맵을 가리지 않게
            windowRect.x = UI.screenWidth - windowRect.width - 10f;
            windowRect.y = Mathf.Max(10f, (UI.screenHeight - windowRect.height) / 2f - 40f);
        }

        public override void WindowUpdate()
        {
            base.WindowUpdate();
            MainThread.Drain();
            if (sendInFrames > 0) sendInFrames--;
        }

        public override void DoWindowContents(Rect inRect)
        {
            var s = AdvisorMod.Settings;
            DrawHeader(new Rect(inRect.x, inRect.y, inRect.width, HeaderHeight), s);

            float bottom = InputHeight + StatusHeight + 8f;
            Rect chatRect = new Rect(inRect.x, inRect.y + HeaderHeight + 4f, inRect.width, inRect.height - HeaderHeight - bottom - 4f);
            DrawMessages(chatRect);

            Rect statusRect = new Rect(inRect.x, chatRect.yMax + 4f, inRect.width, StatusHeight);
            DrawStatus(statusRect);

            Rect inputRect = new Rect(inRect.x, statusRect.yMax + 4f, inRect.width, InputHeight);
            DrawInput(inputRect);
        }

        void DrawHeader(Rect rect, AdvisorSettings s)
        {
            var provider = s.Provider;
            Text.Font = GameFont.Small;
            Rect row1 = new Rect(rect.x, rect.y, rect.width - 30f, 28f);
            float btnW = 80f;
            // 언어마다 글자 길이가 달라서 추론 버튼 폭은 글자에 맞춘다
            string effortText = provider != ProviderKind.None ? "AIAdvisor_EffortShort".Translate(AdvisorMod.CurrentEffortLabel()).ToString() : "";
            float effortW = Mathf.Clamp(Text.CalcSize(effortText).x + 20f, 90f, 170f);
            Rect modelRect = new Rect(row1.x, row1.y, row1.width - btnW * 2 - effortW - 15f, row1.height);
            if (provider == ProviderKind.None)
            {
                if (Widgets.ButtonText(modelRect, "AIAdvisor_SetKeyFirst".Translate()))
                    OpenSettings();
            }
            else
            {
                string label = AdvisorMod.ModelLabel(provider, s.EffectiveModel());
                if (Widgets.ButtonText(modelRect, label) && !AdvisorSession.Busy)
                    AdvisorMod.OpenModelMenu();
                TooltipHandler.TipRegion(modelRect, "AIAdvisor_ModelTip".Translate(Providers.Label(provider)));
            }
            float x = modelRect.xMax + 5f;
            if (provider != ProviderKind.None)
            {
                Rect effortRect = new Rect(x, row1.y, effortW, row1.height);
                if (Widgets.ButtonText(effortRect, effortText, active: AdvisorMod.EffortSelectable()) && !AdvisorSession.Busy)
                    AdvisorMod.OpenEffortMenu();
                TooltipHandler.TipRegion(effortRect, "AIAdvisor_EffortHelp".Translate());
            }
            x += effortW + 5f;
            if (Widgets.ButtonText(new Rect(x, row1.y, btnW, row1.height), "AIAdvisor_NewChat".Translate()) && !AdvisorSession.Busy)
                AdvisorSession.Clear();
            if (Widgets.ButtonText(new Rect(x + btnW + 5f, row1.y, btnW, row1.height), "AIAdvisor_Settings".Translate()))
                OpenSettings();

            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.8f, 0.8f, 0.8f);
            string session = AdvisorMod.FormatCost(AdvisorSession.SessionCost) + (AdvisorSession.SessionCostUnknown ? "+?" : "");
            string costLine = "AIAdvisor_CostLine".Translate(AdvisorMod.FormatCost(AdvisorSession.LastAnswerCost), session, AdvisorMod.FormatCost(s.totalCostUsd));
            if (s.spendLimitUsd > 0f) costLine += "  " + "AIAdvisor_LimitShort".Translate(AdvisorMod.FormatCost(s.spendLimitUsd));
            Widgets.Label(new Rect(rect.x, row1.yMax + 4f, rect.width, 22f), costLine);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        static void OpenSettings()
        {
            Find.WindowStack.Add(new Dialog_ModSettings(AdvisorMod.Instance));
        }

        void DrawMessages(Rect rect)
        {
            Widgets.DrawMenuSection(rect);
            Rect outer = rect.ContractedBy(6f);
            float width = outer.width - 16f;
            var entries = AdvisorSession.Entries;

            float total = 0f;
            Text.Font = GameFont.Small;
            var heights = new float[entries.Count];
            for (int i = 0; i < entries.Count; i++)
            {
                SetFont(entries[i].kind);
                heights[i] = Text.CalcHeight(Format(entries[i]), width - 12f) + 12f;
                total += heights[i] + 6f;
            }
            Text.Font = GameFont.Small;

            if (entries.Count == 0)
            {
                GUI.color = Color.gray;
                Widgets.Label(outer, "AIAdvisor_Welcome".Translate());
                GUI.color = Color.white;
                return;
            }

            if (seenVersion != AdvisorSession.Version)
            {
                seenVersion = AdvisorSession.Version;
                scroll.y = float.MaxValue; // 새 메시지가 오면 맨 아래로
            }

            Rect view = new Rect(0f, 0f, width, total);
            Widgets.BeginScrollView(outer, ref scroll, view);
            float y = 0f;
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                Rect box = new Rect(0f, y, width, heights[i]);
                DrawEntry(box, e);
                y += heights[i] + 6f;
            }
            Widgets.EndScrollView();
        }

        static void SetFont(EntryKind kind)
        {
            Text.Font = kind == EntryKind.Tool || kind == EntryKind.Info ? GameFont.Tiny : GameFont.Small;
        }

        static string Format(ChatEntry e)
        {
            if (e.kind != EntryKind.Assistant && e.kind != EntryKind.Info) return e.text;
            string t = headingRegex.Replace(e.text, "");
            return boldRegex.Replace(t, "<b>$1</b>");
        }

        void DrawEntry(Rect box, ChatEntry e)
        {
            Color bg;
            switch (e.kind)
            {
                case EntryKind.User: bg = new Color(0.20f, 0.28f, 0.38f, 0.8f); break;
                case EntryKind.Assistant: bg = new Color(0.16f, 0.16f, 0.16f, 0.9f); break;
                case EntryKind.Error: bg = new Color(0.40f, 0.12f, 0.12f, 0.8f); break;
                default: bg = new Color(0f, 0f, 0f, 0f); break;
            }
            Widgets.DrawBoxSolid(box, bg);
            SetFont(e.kind);
            if (e.kind == EntryKind.Tool || e.kind == EntryKind.Info) GUI.color = new Color(0.65f, 0.75f, 0.65f);
            string text = Format(e);
            Widgets.Label(box.ContractedBy(6f), text);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;

            if (e.kind == EntryKind.Assistant)
            {
                Rect copy = new Rect(box.xMax - 20f, box.y + 2f, 18f, 18f);
                if (Widgets.ButtonImage(copy, TexButton.Copy))
                {
                    GUIUtility.systemCopyBuffer = e.text;
                    Messages.Message("AIAdvisor_Copied".Translate(), MessageTypeDefOf.SilentInput, false);
                }
            }
        }

        void DrawStatus(Rect rect)
        {
            if (!AdvisorSession.Busy) return;
            Text.Font = GameFont.Tiny;
            int dots = (int)(Time.realtimeSinceStartup * 3f) % 4;
            Widgets.Label(new Rect(rect.x, rect.y + 4f, rect.width - 90f, rect.height), AdvisorSession.Status + new string('.', dots));
            Text.Font = GameFont.Small;
            if (Widgets.ButtonText(new Rect(rect.xMax - 85f, rect.y, 85f, rect.height), "AIAdvisor_Cancel".Translate()))
                AdvisorSession.Cancel();
        }

        void DrawInput(Rect rect)
        {
            Rect field = new Rect(rect.x, rect.y, rect.width - 80f, rect.height);
            Rect sendBtn = new Rect(field.xMax + 5f, rect.y, 75f, rect.height);

            // Enter = 전송, Shift+Enter = 줄바꿈. TextArea 가 줄바꿈을 넣기 전에 이벤트를 가로챈다.
            var ev = Event.current;
            if (ev.type == EventType.KeyDown && GUI.GetNameOfFocusedControl() == InputControl && !ev.shift
                && (ev.keyCode == KeyCode.Return || ev.keyCode == KeyCode.KeypadEnter || ev.character == '\n' || ev.character == '\r'))
            {
                // 한글 IME 조합 중인 마지막 글자가 입력창에 반영될 때까지 몇 프레임 기다렸다가 보낸다
                if (ev.keyCode != KeyCode.None) sendInFrames = 2;
                ev.Use();
            }

            GUI.SetNextControlName(InputControl);
            input = Widgets.TextArea(field, input);

            // Unity 는 키 하나에 "키 코드" 이벤트와 "글자" 이벤트를 따로 보낸다. 입력창은 글자 이벤트만 쓰고
            // 키 코드 이벤트(스페이스, 숫자 등)를 남겨서, 그대로 두면 게임 단축키(일시정지, 속도 변경)가 같이 눌린다.
            // 입력창이 처리하고 남은 키 이벤트는 여기서 소비한다. Esc 는 창 닫기에 쓰도록 남긴다.
            if (ev.type == EventType.KeyDown && ev.keyCode != KeyCode.None && ev.keyCode != KeyCode.Escape
                && GUI.GetNameOfFocusedControl() == InputControl)
            {
                ev.Use();
            }

            if (focusInput)
            {
                GUI.FocusControl(InputControl);
                focusInput = false;
            }

            bool canSend = !AdvisorSession.Busy && !string.IsNullOrWhiteSpace(input);
            if (Widgets.ButtonText(sendBtn, "AIAdvisor_Send".Translate(), active: canSend) && canSend)
                sendInFrames = 0;

            if (sendInFrames == 0 && string.IsNullOrEmpty(Input.compositionString))
            {
                sendInFrames = -1;
                if (canSend)
                {
                    AdvisorSession.Send(input);
                    input = "";
                    focusInput = true;
                }
            }
        }
    }

    public class MainButtonWorker_Advisor : MainButtonWorker
    {
        public override void Activate()
        {
            var open = Find.WindowStack.WindowOfType<Window_Advisor>();
            if (open != null) open.Close();
            else Find.WindowStack.Add(new Window_Advisor());
        }
    }
}
