using System;
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System.Linq;
using RimWorld;
using Verse;

namespace AIAdvisor
{
    /// <summary>질문 하나와 최종 답변. 회사별 메시지 형식과 무관하게 저장하고, 요청할 때 각 회사 형식으로 다시 만든다.</summary>
    public class Turn
    {
        public string question;
        public string answer;
        /// <summary>질문을 보낸 시점의 TicksGame. 세이브를 되돌렸는지 판단할 때 쓴다.</summary>
        public int tick;
        /// <summary>질문을 보낸 게임 시각 (예: "5500년 1분기 8일, 14h"). 게임 언어로 적힌다.</summary>
        public string time;
    }

    /// <summary>
    /// 정착지(월드)별 대화를 RimWorld Config 폴더의 JSON 파일로 저장한다.
    /// 세이브 파일에는 아무것도 쓰지 않으므로 모드를 빼도 세이브에 흔적이 남지 않는다.
    /// </summary>
    public static class SessionStore
    {
        public class Data
        {
            public List<ChatEntry> entries = new List<ChatEntry>();
            public List<Turn> turns = new List<Turn>();
            public double cost;
            public bool costUnknown;
            /// <summary>파일을 알아보기 쉽게 적어 두는 정착지 이름. 게임을 닫는 중에는 읽을 수 없어서 미리 받아 둔다.</summary>
            public string colony;
        }

        const int FormatVersion = 1;
        /// <summary>파일에 남길 최대 개수. 오래된 것부터 버린다.</summary>
        const int MaxEntries = 300;
        const int MaxTurns = 100;

        public static string Folder => Path.Combine(Path.Combine(GenFilePaths.ConfigFolderPath, "RimSage"), "Sessions");

        /// <summary>지금 게임의 정착지 이름. 메인 스레드에서 호출.</summary>
        public static string CurrentColonyName()
        {
            try { return Faction.OfPlayerSilentFail?.Name ?? ""; }
            catch { return ""; }
        }

        /// <summary>
        /// 월드를 만들 때 정해지고 세이브에 같이 저장되는 값이라, 같은 정착지의 여러 세이브는 같은 대화를 쓰고
        /// 다른 정착지는 다른 대화를 쓴다. 메인 스레드에서 호출.
        /// </summary>
        public static string CurrentKey()
        {
            var info = Find.World?.info;
            return info == null ? null : "colony_" + ((uint)info.persistentRandomValue).ToString();
        }

        static string PathFor(string key) => Path.Combine(Folder, key + ".json");

        public static Data Load(string key)
        {
            var data = new Data();
            string path = PathFor(key);
            if (!File.Exists(path)) return data;
            try
            {
                var root = Json.Parse(File.ReadAllText(path));
                foreach (var e in root.Arr("entries"))
                {
                    if (!Enum.TryParse(e.Str("kind"), out EntryKind kind)) continue;
                    data.entries.Add(new ChatEntry { kind = kind, text = e.Str("text") ?? "" });
                }
                foreach (var t in root.Arr("turns"))
                {
                    string q = t.Str("q"), a = t.Str("a");
                    if (string.IsNullOrEmpty(q) || string.IsNullOrEmpty(a)) continue;
                    data.turns.Add(new Turn { question = q, answer = a, tick = (int)t.Num("tick"), time = t.Str("time") ?? "" });
                }
                data.colony = root.Str("colony");
                data.cost = root.Num("cost"); // 문자열("0.0174...")과 숫자 모두 읽는다
                data.costUnknown = root.Get("cost_unknown") is bool b && b;
            }
            catch (Exception e)
            {
                // 깨진 파일은 옆에 옮겨 두고 새 대화로 시작한다
                Log.Warning("[RimSage] could not read " + path + ": " + e.Message);
                try { File.Copy(path, path + ".bad", true); }
                catch
                {
                    // 복사까지 실패하면 그냥 새로 시작
                }
                return new Data();
            }
            return data;
        }

        public static void Save(string key, Data data)
        {
            if (string.IsNullOrEmpty(key)) return;
            try
            {
                var root = new Dictionary<string, object>
                {
                    { "version", FormatVersion },
                    { "colony", data.colony ?? "" },
                    { "saved_utc", DateTime.UtcNow.ToString("o") },
                    // Json 은 숫자를 소수 셋째 자리까지만 쓰는데, 답변 하나가 $0.001 도 안 될 때가 많아서 문자열로 그대로 남긴다
                    { "cost", data.cost.ToString("R", CultureInfo.InvariantCulture) },
                    { "cost_unknown", data.costUnknown },
                    { "entries", data.entries.Skip(Math.Max(0, data.entries.Count - MaxEntries))
                        .Select(e => (object)new Dictionary<string, object> { { "kind", e.kind.ToString() }, { "text", e.text } }).ToList() },
                    { "turns", data.turns.Skip(Math.Max(0, data.turns.Count - MaxTurns))
                        .Select(t => (object)new Dictionary<string, object> { { "q", t.question }, { "a", t.answer }, { "tick", t.tick }, { "time", t.time } }).ToList() },
                };
                Directory.CreateDirectory(Folder);
                string path = PathFor(key);
                // 쓰는 도중 게임이 꺼져도 이전 파일이 남도록 임시 파일에 먼저 쓴다
                string tmp = path + ".tmp";
                File.WriteAllText(tmp, Json.Serialize(root));
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception e)
            {
                Log.Warning("[RimSage] could not save the conversation: " + e.Message);
            }
        }
    }
}
