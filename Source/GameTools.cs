using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace AIAdvisor
{
    /// <summary>
    /// LLM 이 호출할 수 있는 도구들. 전부 게임 데이터를 읽기만 한다 (focus_camera 만 카메라를 움직임).
    /// Execute 는 반드시 메인 스레드에서 호출해야 한다.
    /// </summary>
    public static class GameTools
    {
        delegate object ToolFn(Dictionary<string, object> args);

        class Tool
        {
            public ToolSpec spec;
            public ToolFn fn;
        }

        static readonly Dictionary<string, Tool> tools = new Dictionary<string, Tool>();

        static GameTools()
        {
            Add("get_colony_overview",
                "Snapshot of the current colony map: date, season, weather, temperature, wealth, colonist/prisoner/animal counts, food & medicine stock, storyteller, danger level, active game conditions and the alerts currently shown in the UI. Call this first for general questions.",
                NoArgs(), _ => ColonyOverview());
            Add("list_colonists",
                "Brief list of all free colonists on the current map: mood, health, current activity, drafted/mental state and top skills.",
                NoArgs(), _ => ListColonists());
            Add("get_colonist_details",
                "Detailed info for one colonist (or any pawn on the map): skills with passions, traits, backstory, health conditions, needs, mood thoughts, equipment, apparel and work priorities.",
                Schema(("name", "string", "Pawn name or part of it"), required: "name"),
                a => ColonistDetails(StrArg(a, "name")));
            Add("get_resources",
                "Counts of stored resources on the map grouped by category. Optionally filter by a text (e.g. 'steel', 'medicine', 'meal').",
                Schema(("filter", "string", "Optional substring to filter item names or categories")),
                a => Resources(StrArg(a, "filter")));
            Add("get_threats",
                "Current dangers on the map: hostile pawns grouped by faction and kind with weapons, manhunters, fires, active harmful game conditions, colony defenses (turrets) and danger rating.",
                NoArgs(), _ => Threats());
            Add("get_research",
                "Research status: current project and progress, number finished, and projects available to start now.",
                NoArgs(), _ => Research());
            Add("get_work_priorities",
                "Work priority table (colonist x work type). 1 = highest priority, 0 = not assigned, 'X' = incapable.",
                NoArgs(), _ => WorkPriorities());
            Add("get_base_infrastructure",
                "Base layout facts: rooms with role, impressiveness, temperature and owners; beds (normal/medical/prisoner); power grids (generation, consumption, stored energy).",
                NoArgs(), _ => Infrastructure());
            Add("get_animals",
                "Tame colony animals and mechanoids on the map with health, age and master.",
                NoArgs(), _ => Animals());
            Add("get_prisoners_and_slaves",
                "Prisoners and slaves of the colony with recruitment resistance, will and health.",
                NoArgs(), _ => Prisoners());
            Add("get_recent_events",
                "Most recent letters and messages from the game history (raids, deaths, quests, events).",
                Schema(("count", "integer", "How many recent entries (default 15, max 40)")),
                a => RecentEvents((int)NumArg(a, "count", 15)));
            Add("get_selected",
                "Details about whatever the player currently has selected in the game (pawns, buildings, items, zones). Use when the player says 'this', 'it', 'selected' etc.",
                NoArgs(), _ => Selected());
            Add("focus_camera",
                "Move the game camera to a pawn by name and select it, to show the player what you are talking about. Only use when it helps the player.",
                Schema(("name", "string", "Pawn name"), required: "name"),
                a => FocusCamera(StrArg(a, "name")));
        }

        public static List<ToolSpec> Specs => tools.Values.Select(t => t.spec).ToList();

        /// <summary>메인 스레드에서 실행. 결과는 JSON 문자열.</summary>
        public static ToolOutput Execute(ToolCall call)
        {
            var output = new ToolOutput { call = call };
            try
            {
                if (call.parseError != null) throw new ArgumentException(call.parseError);
                if (!tools.TryGetValue(call.name ?? "", out var tool)) throw new ArgumentException("Unknown tool: " + call.name);
                if (Find.CurrentMap == null) throw new InvalidOperationException("No map is loaded.");
                output.content = Json.Serialize(tool.fn(call.args ?? new Dictionary<string, object>()));
            }
            catch (Exception e)
            {
                output.isError = true;
                output.content = e.Message;
            }
            return output;
        }

        // ---------------------------------------------------------------- schema helpers

        static void Add(string name, string description, Dictionary<string, object> schema, ToolFn fn)
        {
            tools[name] = new Tool { spec = new ToolSpec { name = name, description = description, schema = schema }, fn = fn };
        }

        static Dictionary<string, object> NoArgs() => Schema();

        static Dictionary<string, object> Schema(params (string name, string type, string desc)[] props) => Schema(props, null);

        static Dictionary<string, object> Schema((string name, string type, string desc) prop, string required) => Schema(new[] { prop }, required);

        static Dictionary<string, object> Schema((string name, string type, string desc)[] props, string required)
        {
            var p = new Dictionary<string, object>();
            foreach (var (name, type, desc) in props)
                p[name] = new Dictionary<string, object> { { "type", type }, { "description", desc } };
            var s = new Dictionary<string, object> { { "type", "object" }, { "properties", p } };
            if (required != null) s["required"] = new List<object> { required };
            return s;
        }

        static string StrArg(Dictionary<string, object> a, string key) => a.TryGetValue(key, out var v) ? v?.ToString() : null;

        static double NumArg(Dictionary<string, object> a, string key, double fallback)
        {
            if (a.TryGetValue(key, out var v))
            {
                if (v is double d) return d;
                if (v is string s && double.TryParse(s, out var p)) return p;
            }
            return fallback;
        }

        static Dictionary<string, object> Obj() => new Dictionary<string, object>();

        static int Pct(float f) => Mathf.RoundToInt(f * 100f);

        static Map Map => Find.CurrentMap;

        // ---------------------------------------------------------------- overview

        static object ColonyOverview()
        {
            var map = Map;
            var o = Obj();
            Vector2 longLat = Find.WorldGrid.LongLatOf(map.Tile);
            o["date"] = GenDate.DateFullStringAt(GenTicks.TicksAbs, longLat);
            o["hour"] = GenLocalDate.HourOfDay(map);
            o["season"] = GenLocalDate.Season(map).LabelCap().ToString();
            o["days_passed"] = GenDate.DaysPassed;
            o["map"] = map.Parent?.LabelCap ?? "";
            o["biome"] = map.Biome?.LabelCap.ToString();
            o["weather"] = map.weatherManager?.curWeather?.LabelCap.ToString();
            o["outdoor_temp_c"] = Math.Round(map.mapTemperature.OutdoorTemp, 1);
            o["wealth"] = Mathf.RoundToInt(map.wealthWatcher.WealthTotal);
            o["storyteller"] = Find.Storyteller.def.LabelCap.ToString() + " / " + Find.Storyteller.difficultyDef.LabelCap;
            o["danger"] = map.dangerWatcher.DangerRating.ToString();

            var colonists = map.mapPawns.FreeColonistsSpawned;
            o["colonists"] = colonists.Count;
            o["prisoners"] = map.mapPawns.PrisonersOfColonySpawnedCount;
            o["slaves"] = map.mapPawns.SlavesOfColonySpawned.Count;
            o["colony_animals"] = map.mapPawns.SpawnedColonyAnimals.Count;
            if (colonists.Count > 0)
            {
                var moods = colonists.Where(p => p.needs?.mood != null).Select(p => p.needs.mood.CurLevelPercentage).ToList();
                if (moods.Count > 0) o["average_mood_pct"] = Pct(moods.Average());
            }

            float nutrition = map.resourceCounter.TotalHumanEdibleNutrition;
            int eaters = Math.Max(1, map.mapPawns.FreeColonistsAndPrisonersSpawnedCount);
            o["food_nutrition"] = Math.Round(nutrition, 1);
            o["food_days_estimate"] = Math.Round(nutrition / (eaters * 1.6f), 1);
            o["silver"] = map.resourceCounter.Silver;
            o["medicine"] = map.resourceCounter.AllCountedAmounts.Where(kv => kv.Key.IsMedicine).Sum(kv => kv.Value);

            o["game_conditions"] = ActiveConditions(map);
            o["alerts"] = ActiveAlerts();
            return o;
        }

        static List<object> ActiveConditions(Map map)
        {
            var list = new List<object>();
            foreach (var c in map.gameConditionManager.ActiveConditions)
                list.Add(c.LabelCap + (c.Permanent ? "" : " (" + c.TicksLeft.ToStringTicksToPeriod() + " left)"));
            foreach (var c in Find.World.gameConditionManager.ActiveConditions)
                list.Add(c.LabelCap + " [world]");
            return list;
        }

        static readonly FieldInfo activeAlertsField = typeof(AlertsReadout).GetField("activeAlerts", BindingFlags.NonPublic | BindingFlags.Instance);

        static List<object> ActiveAlerts()
        {
            var list = new List<object>();
            if (!(Find.UIRoot is UIRoot_Play play) || activeAlertsField == null) return list;
            if (!(activeAlertsField.GetValue(play.alerts) is List<Alert> alerts)) return list;
            foreach (var a in alerts)
            {
                try
                {
                    string explanation = a.GetExplanation().ToString();
                    if (explanation.Length > 300) explanation = explanation.Substring(0, 300) + "…";
                    list.Add(new Dictionary<string, object>
                    {
                        { "label", a.GetLabel() },
                        { "priority", a.Priority.ToString() },
                        { "detail", explanation },
                    });
                }
                catch
                {
                    // 개별 알림이 예외를 던져도 전체 결과는 돌려준다
                }
            }
            return list;
        }

        // ---------------------------------------------------------------- pawns

        static object ListColonists()
        {
            return Map.mapPawns.FreeColonistsSpawned.Select(p => (object)PawnBrief(p)).ToList();
        }

        static Dictionary<string, object> PawnBrief(Pawn p)
        {
            var o = Obj();
            o["name"] = p.LabelShortCap;
            o["age"] = p.ageTracker.AgeBiologicalYears;
            if (p.needs?.mood != null) o["mood_pct"] = Pct(p.needs.mood.CurLevelPercentage);
            o["health_pct"] = Pct(p.health.summaryHealth.SummaryHealthPercent);
            o["activity"] = p.jobs?.curDriver?.GetReport() ?? "";
            if (p.Drafted) o["drafted"] = true;
            if (p.Downed) o["downed"] = true;
            if (p.InMentalState) o["mental_state"] = p.MentalStateDef.label;
            if (p.health.hediffSet.BleedRateTotal > 0.01f) o["bleeding"] = true;
            if (p.skills != null)
            {
                o["top_skills"] = p.skills.skills.Where(s => !s.TotallyDisabled).OrderByDescending(s => s.Level).Take(3)
                    .Select(s => (object)(s.def.LabelCap + " " + s.Level + PassionMark(s.passion))).ToList();
            }
            return o;
        }

        static string PassionMark(Passion p)
        {
            switch (p)
            {
                case Passion.Minor: return " (passion)";
                case Passion.Major: return " (burning passion)";
                default: return "";
            }
        }

        static Pawn FindPawn(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("name is required");
            name = name.Trim();
            var pawns = Map.mapPawns.AllPawnsSpawned;
            Pawn Match(Func<Pawn, string> label, bool exact) => pawns.FirstOrDefault(p =>
            {
                string l = label(p);
                if (l == null) return false;
                return exact ? string.Equals(l, name, StringComparison.OrdinalIgnoreCase) : l.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0;
            });
            var found = Match(p => p.LabelShort, true) ?? Match(p => p.Name?.ToStringFull, true)
                        ?? Match(p => p.LabelShort, false) ?? Match(p => p.Name?.ToStringFull, false);
            if (found == null)
            {
                var names = Map.mapPawns.FreeColonistsSpawned.Select(p => p.LabelShort);
                throw new ArgumentException("No pawn named '" + name + "' on this map. Colonists: " + string.Join(", ", names));
            }
            return found;
        }

        static object ColonistDetails(string name) => PawnDetails(FindPawn(name));

        static object PawnDetails(Pawn p)
        {
            var o = PawnBrief(p);
            o["full_name"] = p.Name?.ToStringFull ?? p.LabelCap;
            o["gender"] = p.gender.GetLabel();
            o["kind"] = p.KindLabel;
            if (p.Faction != null) o["faction"] = p.Faction.Name;
            if (p.genes?.Xenotype != null) o["xenotype"] = p.genes.XenotypeLabelCap;
            if (p.Ideo != null) o["ideoligion"] = p.Ideo.name;

            if (p.story != null)
            {
                o["childhood"] = p.story.Childhood?.TitleCapFor(p.gender);
                o["adulthood"] = p.story.Adulthood?.TitleCapFor(p.gender);
                o["traits"] = p.story.traits.allTraits.Select(t => (object)t.LabelCap).ToList();
                var disabled = p.story.DisabledWorkTagsBackstoryTraitsAndGenes;
                if (disabled != WorkTags.None) o["incapable_of"] = disabled.LabelTranslated();
            }

            if (p.skills != null)
            {
                o["skills"] = p.skills.skills.Select(s => (object)(s.def.LabelCap + ": " + (s.TotallyDisabled ? "incapable" : s.Level + PassionMark(s.passion)))).ToList();
            }

            var health = Obj();
            health["conditions"] = p.health.hediffSet.hediffs.Where(h => h.Visible).Select(h =>
            {
                string s = h.LabelCap;
                if (h.Part != null) s += " @ " + h.Part.LabelCap;
                if (h.Bleeding) s += " (bleeding)";
                if (h.TendableNow()) s += " (needs tending)";
                return (object)s;
            }).ToList();
            health["bleed_rate_per_day"] = Math.Round(p.health.hediffSet.BleedRateTotal, 2);
            var caps = Obj();
            foreach (var cap in new[] { PawnCapacityDefOf.Consciousness, PawnCapacityDefOf.Moving, PawnCapacityDefOf.Manipulation, PawnCapacityDefOf.Sight, PawnCapacityDefOf.Breathing })
                caps[cap.defName] = Pct(p.health.capacities.GetLevel(cap));
            health["capacities_pct"] = caps;
            o["health"] = health;

            if (p.needs != null)
            {
                o["needs_pct"] = p.needs.AllNeeds.ToDictionary(n => n.LabelCap.ToString(), n => (object)Pct(n.CurLevelPercentage));
                if (p.needs.mood != null)
                {
                    var groups = new List<Thought>();
                    p.needs.mood.thoughts.GetDistinctMoodThoughtGroups(groups);
                    o["mood_thoughts"] = groups
                        .Select(t => (t, off: p.needs.mood.thoughts.MoodOffsetOfGroup(t)))
                        .OrderBy(x => x.off)
                        .Select(x => (object)(x.t.LabelCap + " " + x.off.ToString("+0;-0")))
                        .ToList();
                    var breaker = p.mindState?.mentalBreaker;
                    if (breaker != null) o["mental_break_threshold_pct"] = Pct(breaker.BreakThresholdMinor);
                }
            }

            if (p.equipment?.Primary != null) o["weapon"] = p.equipment.Primary.LabelCap;
            if (p.apparel != null) o["apparel"] = p.apparel.WornApparel.Select(a => (object)a.LabelCap).ToList();
            if (p.inventory != null && p.inventory.innerContainer.Count > 0)
                o["inventory"] = p.inventory.innerContainer.Select(t => (object)t.LabelCap).ToList();

            if (p.workSettings != null && p.workSettings.EverWork)
            {
                var w = Obj();
                foreach (var wt in WorkTypeDefsUtility.WorkTypeDefsInPriorityOrder)
                    w[wt.labelShort] = p.WorkTypeIsDisabled(wt) ? (object)"X" : p.workSettings.GetPriority(wt);
                o["work_priorities"] = w;
            }

            var room = p.GetRoom();
            if (room != null && !room.PsychologicallyOutdoors) o["current_room"] = room.GetRoomRoleLabel();
            if (p.ownership?.OwnedBed != null) o["has_own_bed"] = true;
            return o;
        }

        // ---------------------------------------------------------------- resources

        static object Resources(string filter)
        {
            var groups = new SortedDictionary<string, Dictionary<string, object>>();
            foreach (var kv in Map.resourceCounter.AllCountedAmounts)
            {
                if (kv.Value <= 0) continue;
                string cat = kv.Key.FirstThingCategory?.LabelCap.ToString() ?? "Other";
                string label = kv.Key.LabelCap;
                if (!string.IsNullOrEmpty(filter)
                    && label.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                    && cat.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0
                    && kv.Key.defName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0)
                    continue;
                if (!groups.TryGetValue(cat, out var g)) groups[cat] = g = new Dictionary<string, object>();
                g[label] = kv.Value;
            }
            var o = Obj();
            foreach (var kv in groups) o[kv.Key] = kv.Value;
            if (o.Count == 0) o["note"] = "No stored resources matched.";
            return o;
        }

        // ---------------------------------------------------------------- threats

        static object Threats()
        {
            var map = Map;
            var o = Obj();
            o["danger"] = map.dangerWatcher.DangerRating.ToString();

            var hostiles = map.mapPawns.AllPawnsSpawned
                .Where(p => !p.Dead && p.HostileTo(Faction.OfPlayer) && !p.IsPrisonerOfColony)
                .ToList();
            o["hostiles_total"] = hostiles.Count;
            o["hostiles_active"] = hostiles.Count(p => !p.Downed);
            o["hostile_groups"] = hostiles.Where(p => !p.Downed)
                .GroupBy(p => (p.Faction?.Name ?? (p.InMentalState ? p.MentalStateDef.label : "wild")) + " - " + p.KindLabel)
                .Select(g => (object)new Dictionary<string, object>
                {
                    { "group", g.Key },
                    { "count", g.Count() },
                    { "weapons", g.Select(p => p.equipment?.Primary?.def.label ?? "none").GroupBy(x => x).Select(x => (object)(x.Key + " x" + x.Count())).ToList() },
                    { "near", DescribePosition(g.First().Position, map) },
                }).ToList();

            o["fires"] = map.listerThings.ThingsOfDef(ThingDefOf.Fire).Count;
            o["game_conditions"] = ActiveConditions(map);

            var turrets = map.listerBuildings.allBuildingsColonist.OfType<Building_Turret>().ToList();
            o["colony_turrets"] = turrets.GroupBy(t => t.def.label).Select(g => (object)(g.Key + " x" + g.Count())).ToList();
            o["colonists_drafted"] = map.mapPawns.FreeColonistsSpawned.Count(p => p.Drafted);
            o["colonists_able_to_fight"] = map.mapPawns.FreeColonistsSpawned.Count(p => !p.Downed && !p.WorkTagIsDisabled(WorkTags.Violent));
            return o;
        }

        static string DescribePosition(IntVec3 c, Map map)
        {
            int w = map.Size.x, h = map.Size.z;
            string ns = c.z > h * 2 / 3 ? "north" : c.z < h / 3 ? "south" : "";
            string ew = c.x > w * 2 / 3 ? "east" : c.x < w / 3 ? "west" : "";
            string dir = (ns + ew).Length > 0 ? ns + (ns.Length > 0 && ew.Length > 0 ? "-" : "") + ew : "center";
            return dir + " (" + c.x + "," + c.z + ")";
        }

        // ---------------------------------------------------------------- research

        static object Research()
        {
            var rm = Find.ResearchManager;
            var o = Obj();
            var cur = rm.GetProject();
            if (cur != null)
            {
                o["current"] = cur.LabelCap.ToString();
                o["current_progress_pct"] = Pct(cur.ProgressPercent);
                o["current_description"] = cur.description;
            }
            else o["current"] = "none";
            var all = DefDatabase<ResearchProjectDef>.AllDefsListForReading;
            o["finished_count"] = all.Count(r => r.IsFinished);
            o["total_count"] = all.Count;
            o["available_now"] = all.Where(r => r.CanStartNow && !r.IsFinished)
                .OrderBy(r => r.CostApparent)
                .Select(r => (object)(r.LabelCap + " (cost " + Mathf.RoundToInt(r.CostApparent) + (r.ProgressPercent > 0 ? ", " + Pct(r.ProgressPercent) + "% done" : "") + ")"))
                .ToList();
            o["research_benches"] = Map.listerBuildings.allBuildingsColonist.Count(b => b is Building_ResearchBench);
            return o;
        }

        // ---------------------------------------------------------------- work

        static object WorkPriorities()
        {
            var o = Obj();
            o["manual_priorities_enabled"] = Find.PlaySettings.useWorkPriorities;
            var table = Obj();
            foreach (var p in Map.mapPawns.FreeColonistsSpawned)
            {
                if (p.workSettings == null || !p.workSettings.EverWork) continue;
                var row = Obj();
                foreach (var wt in WorkTypeDefsUtility.WorkTypeDefsInPriorityOrder)
                    row[wt.labelShort] = p.WorkTypeIsDisabled(wt) ? (object)"X" : p.workSettings.GetPriority(wt);
                table[p.LabelShortCap] = row;
            }
            o["table"] = table;
            return o;
        }

        // ---------------------------------------------------------------- infrastructure

        static object Infrastructure()
        {
            var map = Map;
            var o = Obj();

            o["rooms"] = map.regionGrid.AllRooms
                .Where(r => !r.PsychologicallyOutdoors && !r.IsHuge && r.ProperRoom && r.CellCount > 1)
                .OrderByDescending(r => r.CellCount)
                .Take(40)
                .Select(r =>
                {
                    var d = new Dictionary<string, object>
                    {
                        { "role", r.GetRoomRoleLabel() },
                        { "cells", r.CellCount },
                        { "impressiveness", Mathf.RoundToInt(r.GetStat(RoomStatDefOf.Impressiveness)) },
                        { "temp_c", Math.Round(r.Temperature, 1) },
                    };
                    var owners = r.Owners.Select(p => (object)p.LabelShort).ToList();
                    if (owners.Count > 0) d["owners"] = owners;
                    return (object)d;
                }).ToList();

            var beds = map.listerBuildings.allBuildingsColonist.OfType<Building_Bed>().Where(b => b.def.building.bed_humanlike).ToList();
            o["beds"] = new Dictionary<string, object>
            {
                { "colonist", beds.Count(b => !b.Medical && !b.ForPrisoners) },
                { "medical", beds.Count(b => b.Medical) },
                { "prisoner", beds.Count(b => b.ForPrisoners) },
                { "colonists_without_bed", map.mapPawns.FreeColonistsSpawned.Count(p => p.ownership?.OwnedBed == null) },
            };

            var grids = new List<object>();
            foreach (var net in map.powerNetManager.AllNetsListForReading)
            {
                if (net.powerComps.Count == 0 && net.batteryComps.Count == 0) continue;
                float produced = net.powerComps.Where(c => c.PowerOn && c.PowerOutput > 0).Sum(c => c.PowerOutput);
                float consumed = -net.powerComps.Where(c => c.PowerOn && c.PowerOutput < 0).Sum(c => c.PowerOutput);
                grids.Add(new Dictionary<string, object>
                {
                    { "generation_w", Mathf.RoundToInt(produced) },
                    { "consumption_w", Mathf.RoundToInt(consumed) },
                    { "net_w", Mathf.RoundToInt(net.CurrentEnergyGainRate() / CompPower.WattsToWattDaysPerTick) },
                    { "stored_wd", Mathf.RoundToInt(net.CurrentStoredEnergy()) },
                    { "batteries", net.batteryComps.Count },
                    { "devices", net.powerComps.Count },
                });
            }
            o["power_grids"] = grids;
            return o;
        }

        // ---------------------------------------------------------------- animals / prisoners

        static object Animals()
        {
            var map = Map;
            var list = map.mapPawns.SpawnedColonyAnimals.Concat(map.mapPawns.SpawnedColonyMechs)
                .Select(p =>
                {
                    var d = new Dictionary<string, object>
                    {
                        { "name", p.LabelShortCap },
                        { "kind", p.KindLabel },
                        { "health_pct", Pct(p.health.summaryHealth.SummaryHealthPercent) },
                        { "age", p.ageTracker.AgeBiologicalYears },
                    };
                    if (p.playerSettings?.Master != null) d["master"] = p.playerSettings.Master.LabelShort;
                    if (p.Downed) d["downed"] = true;
                    return (object)d;
                }).ToList();
            return new Dictionary<string, object> { { "count", list.Count }, { "animals", list } };
        }

        static object Prisoners()
        {
            var map = Map;
            object Info(Pawn p)
            {
                var d = new Dictionary<string, object>
                {
                    { "name", p.LabelShortCap },
                    { "kind", p.KindLabel },
                    { "health_pct", Pct(p.health.summaryHealth.SummaryHealthPercent) },
                };
                if (p.Faction != null) d["faction"] = p.Faction.Name;
                if (p.guest != null)
                {
                    d["resistance"] = Math.Round(p.guest.resistance, 1);
                    d["will"] = Math.Round(p.guest.will, 1);
                    d["recruitable"] = p.guest.Recruitable;
                }
                if (p.needs?.mood != null) d["mood_pct"] = Pct(p.needs.mood.CurLevelPercentage);
                if (p.skills != null)
                    d["top_skills"] = p.skills.skills.Where(s => !s.TotallyDisabled).OrderByDescending(s => s.Level).Take(3)
                        .Select(s => (object)(s.def.LabelCap + " " + s.Level + PassionMark(s.passion))).ToList();
                return d;
            }
            return new Dictionary<string, object>
            {
                { "prisoners", map.mapPawns.PrisonersOfColonySpawned.Select(Info).ToList() },
                { "slaves", map.mapPawns.SlavesOfColonySpawned.Select(Info).ToList() },
            };
        }

        // ---------------------------------------------------------------- history / selection

        static object RecentEvents(int count)
        {
            count = Mathf.Clamp(count, 1, 40);
            int now = Find.TickManager.TicksGame;
            return Find.Archive.ArchivablesListForReading
                .OrderByDescending(a => a.CreatedTicksGame)
                .Take(count)
                .Select(a =>
                {
                    string tip = a.ArchivedTooltip ?? "";
                    if (tip.Length > 400) tip = tip.Substring(0, 400) + "…";
                    return (object)new Dictionary<string, object>
                    {
                        { "label", a.ArchivedLabel },
                        { "ago", (now - a.CreatedTicksGame).ToStringTicksToPeriod() },
                        { "detail", tip },
                    };
                }).ToList();
        }

        static object Selected()
        {
            var list = new List<object>();
            foreach (var obj in Find.Selector.SelectedObjectsListForReading.Take(10))
            {
                switch (obj)
                {
                    case Pawn p:
                        list.Add(p.RaceProps.Humanlike ? PawnDetails(p) : PawnBrief(p));
                        break;
                    case Thing t:
                        var d = new Dictionary<string, object>
                        {
                            { "thing", t.LabelCap },
                            { "def", t.def.defName },
                            { "description", t.def.description },
                        };
                        string inspect = t.GetInspectString();
                        if (!string.IsNullOrEmpty(inspect)) d["inspect"] = inspect;
                        if (t.def.useHitPoints) d["hit_points"] = t.HitPoints + "/" + t.MaxHitPoints;
                        list.Add(d);
                        break;
                    case Zone z:
                        list.Add(new Dictionary<string, object> { { "zone", z.label }, { "cells", z.Cells.Count }, { "inspect", z.GetInspectString() } });
                        break;
                    default:
                        list.Add(obj.ToString());
                        break;
                }
            }
            if (list.Count == 0) return "Nothing is selected.";
            return list;
        }

        static object FocusCamera(string name)
        {
            var p = FindPawn(name);
            CameraJumper.TryJumpAndSelect(p);
            return "Camera moved to " + p.LabelShort + ".";
        }
    }
}
