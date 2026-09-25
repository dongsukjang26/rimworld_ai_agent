#if SELFTEST
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIAdvisor
{
    /// <summary>
    /// 창작마당 미리보기 촬영용 (개발 빌드 전용).
    /// AIADVISOR_PROMO_SAVE: 메인 메뉴에서 불러올 세이브 이름
    /// AIADVISOR_PROMO_DIR:  캡처를 저장할 폴더
    /// AIADVISOR_PROMO_QUESTIONS: '|' 로 구분한 질문들 (실제 모델에 보냄)
    /// </summary>
    public static class PromoCapture
    {
        static bool loadRequested;
        static bool started;

        static string Dir => Environment.GetEnvironmentVariable("AIADVISOR_PROMO_DIR");
        public static bool Enabled => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("AIADVISOR_PROMO_SAVE"));

        /// <summary>AdvisorRunner.Update 에서 매 프레임 호출.</summary>
        public static void Tick()
        {
            if (!Enabled) return;
            if (!loadRequested && Current.ProgramState == ProgramState.Entry && Time.realtimeSinceStartup > 8f && !LongEventHandler.AnyEventNowOrWaiting)
            {
                loadRequested = true;
                Log.Message("[AI Advisor promo] loading save");
                GameDataSaveLoader.LoadGame(Environment.GetEnvironmentVariable("AIADVISOR_PROMO_SAVE"));
            }
            if (!started && Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null && !LongEventHandler.AnyEventNowOrWaiting)
            {
                started = true;
                Task.Run(Run);
            }
        }

        static async Task Run()
        {
            try
            {
                await Task.Delay(4000).ConfigureAwait(false);
                var targets = await MainThread.Invoke(() =>
                {
                    Find.TickManager.Pause();
                    var st = AdvisorMod.Settings;
                    st.useLocal = false;
                    string m = Environment.GetEnvironmentVariable("AIADVISOR_SELFTEST_MODEL");
                    if (!string.IsNullOrEmpty(m)) { st.model = m; st.modelProvider = st.Provider; }
                    string e = Environment.GetEnvironmentVariable("AIADVISOR_SELFTEST_EFFORT");
                    if (e != null) st.SetEffort(st.Provider, e);
                    st.pauseOnSend = true;
                    // 오른쪽 위 학습 도우미 상자가 채팅창을 가리지 않게 (테스트 프로필에만 적용)
                    Prefs.AdaptiveTrainingEnabled = false;
                    Find.WindowStack.WindowOfType<Window_Advisor>()?.Close(false);
                    return CameraTargets(Find.CurrentMap);
                }).ConfigureAwait(false);

                // 1) UI 를 숨긴 배경 화면
                for (int i = 0; i < targets.Count; i++)
                {
                    var t = targets[i];
                    await MainThread.Invoke(() =>
                    {
                        Find.UIRoot.screenshotMode.Active = true;
                        Find.CameraDriver.PanToMapLocAndSize(t.pos, t.size, 0.01f);
                        return true;
                    }).ConfigureAwait(false);
                    await Task.Delay(2500).ConfigureAwait(false);
                    string path = Path.Combine(Dir, "bg_" + i + "_" + t.name + ".png");
                    await MainThread.Invoke(() => { ScreenCapture.CaptureScreenshot(path); return true; }).ConfigureAwait(false);
                    await Task.Delay(1500).ConfigureAwait(false);
                    Log.Message("[AI Advisor promo] background " + path);
                }
                await MainThread.Invoke(() => { Find.UIRoot.screenshotMode.Active = false; return true; }).ConfigureAwait(false);

                // 2) 실제 모델에 질문하고 채팅창 캡처
                var questions = (Environment.GetEnvironmentVariable("AIADVISOR_PROMO_QUESTIONS") ?? "").Split('|').Where(q => q.Trim().Length > 0).ToList();
                for (int i = 0; i < questions.Count; i++)
                {
                    string q = questions[i].Trim();
                    await MainThread.Invoke(() =>
                    {
                        if (Find.WindowStack.WindowOfType<Window_Advisor>() == null) Find.WindowStack.Add(new Window_Advisor());
                        AdvisorSession.Clear();
                        AdvisorSession.Send(q);
                        return true;
                    }).ConfigureAwait(false);
                    await Task.Delay(1000).ConfigureAwait(false);
                    for (int w = 0; w < 400 && await MainThread.Invoke(() => AdvisorSession.Busy).ConfigureAwait(false); w++)
                        await Task.Delay(500).ConfigureAwait(false);
                    await Task.Delay(1000).ConfigureAwait(false);
                    // 질문이 보이도록 맨 위로 스크롤하고, 캡처용으로 창을 세로로 키운다
                    string rectInfo = await MainThread.Invoke(() =>
                    {
                        var w = Find.WindowStack.WindowOfType<Window_Advisor>();
                        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                        typeof(Window_Advisor).GetField("seenVersion", flags).SetValue(w, AdvisorSession.Version);
                        typeof(Window_Advisor).GetField("scroll", flags).SetValue(w, Vector2.zero);
                        w.windowRect = new Rect(UI.screenWidth - 600f - 20f, 60f, 600f, 820f);
                        return w.windowRect.x + "," + w.windowRect.y + "," + w.windowRect.width + "," + w.windowRect.height + " scale=" + Prefs.UIScale;
                    }).ConfigureAwait(false);
                    Log.Message("[AI Advisor promo] chat window rect " + i + " " + rectInfo);
                    await Task.Delay(1500).ConfigureAwait(false);
                    string path = Path.Combine(Dir, "chat_" + i + ".png");
                    await MainThread.Invoke(() => { ScreenCapture.CaptureScreenshot(path); return true; }).ConfigureAwait(false);
                    await Task.Delay(1500).ConfigureAwait(false);
                    string log = await MainThread.Invoke(() => string.Join(" | ", AdvisorSession.Entries.Select(e => e.kind + ":" + e.text))).ConfigureAwait(false);
                    Log.Message("[AI Advisor promo] chat " + i + " cost=" + AdvisorSession.LastAnswerCost + " :: " + log);
                }
                Log.Message("[AI Advisor promo] done");
            }
            catch (Exception e)
            {
                Log.Error("[AI Advisor promo] failed: " + e);
            }
        }

        struct Target
        {
            public string name;
            public Vector3 pos;
            public float size;
        }

        static List<Target> CameraTargets(Map map)
        {
            var list = new List<Target>();
            void Add(string name, IntVec3 c, float size) => list.Add(new Target { name = name, pos = c.ToVector3Shifted(), size = size });

            var colonists = map.mapPawns.FreeColonistsSpawned;
            if (colonists.Count > 0)
                Add("colonists", new IntVec3((int)colonists.Average(p => p.Position.x), 0, (int)colonists.Average(p => p.Position.z)), 22f);

            var home = map.areaManager.Home.ActiveCells.ToList();
            if (home.Count > 0)
                Add("base", new IntVec3((int)home.Average(c => c.x), 0, (int)home.Average(c => c.z)), 30f);

            var room = map.regionGrid.AllRooms.Where(r => r.ProperRoom && !r.PsychologicallyOutdoors && !r.IsHuge).OrderByDescending(r => r.CellCount).FirstOrDefault();
            if (room != null)
            {
                var cells = room.Cells.ToList();
                Add("bigroom", new IntVec3((int)cells.Average(c => c.x), 0, (int)cells.Average(c => c.z)), 16f);
            }

            var farm = map.zoneManager.AllZones.OfType<Zone_Growing>().OrderByDescending(z => z.Cells.Count).FirstOrDefault();
            if (farm != null)
                Add("farm", new IntVec3((int)farm.Cells.Average(c => c.x), 0, (int)farm.Cells.Average(c => c.z)), 20f);

            var turrets = map.listerBuildings.allBuildingsColonist.OfType<Building_Turret>().ToList();
            if (turrets.Count > 0)
                Add("defense", new IntVec3((int)turrets.Average(t => t.Position.x), 0, (int)turrets.Average(t => t.Position.z)), 20f);
            return list;
        }
    }
}
#endif
