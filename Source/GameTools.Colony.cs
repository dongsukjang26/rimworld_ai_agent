using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace AIAdvisor
{
    /// <summary>정착지 운영 도구: 퀘스트, 세계와 세력, 생산, 농사, 의료, 인간관계, 거래, 기록 그래프.</summary>
    public static partial class GameTools
    {
        static void RegisterColonyTools()
        {
            Add("get_quests",
                "Quests: offers waiting for an answer (with time left to accept), ongoing quests and recently ended ones. Includes the quest text (threats and conditions), challenge rating, rewards to choose from and involved factions. Use for 'should I accept this quest?'.",
                NoArgs(), _ => Quests());
            Add("get_world",
                "The world beyond this map: other factions (relation, goodwill, tech level, distance to their nearest settlement), the player's other maps and settlements, caravans (members, food days, mass, destination) and world-wide conditions.",
                NoArgs(), _ => World());
            Add("get_production",
                "Work benches on this map and their bill queues: what is being made, repeat/target counts, paused or suspended bills, power state. Use for questions about crafting, cooking, drugs, smelting and production plans.",
                NoArgs(), _ => Production());
            Add("get_farming",
                "Growing zones and hydroponics on this map: crop, size, average growth, days until harvest, harvest-ready, blighted or dying plants, soil fertility, whether plants can grow right now, and the growing period for this location.",
                NoArgs(), _ => Farming());
            Add("get_medical",
                "Medical situation: sick or injured colonists, prisoners, slaves and colony animals with each condition (bleeding, needs tending, disease severity vs immunity with a survival estimate), surgery queued, doctors and their skill, medicine stock and default medical care settings.",
                NoArgs(), _ => Medical());
            Add("get_social",
                "Relationships between colonists: each colonist's best and worst opinion of another colonist, rivalries and friendships (with opinion values) and family/romantic relations. Use for fights, mood from social thoughts and who should share rooms.",
                NoArgs(), _ => Social());
            Add("get_trade",
                "Trading: if a trade window is open, the trader's goods and what the colony can sell, with prices. Otherwise the traders currently visiting the map, orbital trade ships in range and whether a comms console is available.",
                Schema(("filter", "string", "Optional substring to filter goods by name while trading")),
                a => Trade(StrArg(a, "filter")));
            Add("get_trends",
                "History graphs the game records (wealth, colonist count, mood and others) sampled over the last days, with the change. Use for 'why is X going down lately' questions.",
                Schema(("days", "integer", "How many days back (default 5, max 60)")),
                a => Trends((int)NumArg(a, "days", 5)));
        }

        static string Period(int ticks) => ticks > 0 ? ticks.ToStringTicksToPeriod() : "now";

        static string StripText(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = ColoredText.StripTags(s).Trim();
            return s.Length > max ? s.Substring(0, max) + "…" : s;
        }

        // ---------------------------------------------------------------- quests

        static object Quests()
        {
            var all = Find.QuestManager.QuestsListForReading.Where(q => !q.hidden && !q.hiddenInUI).ToList();
            object Info(Quest q)
            {
                var o = Obj();
                o["name"] = q.name;
                o["state"] = q.State.ToString();
                if (q.challengeRating > 0) o["challenge_stars"] = q.challengeRating;
                if (q.State == QuestState.NotYetAccepted && q.TicksUntilExpiry > 0) o["accept_within"] = Period(q.TicksUntilExpiry);
                if (q.State == QuestState.Ongoing && q.EverAccepted) o["accepted_ago"] = Period(q.TicksSinceAccepted);
                if (q.State == QuestState.NotYetAccepted && q.RequiresAccepter) o["needs_accepter"] = true;
                try { o["text"] = StripText(q.description.Resolve(), 700); }
                catch
                {
                    // 문장 생성에 실패한 퀘스트
                }
                var rewards = QuestRewards(q);
                if (rewards.Count > 0) o["reward_options"] = rewards;
                var factions = q.InvolvedFactions?.Where(f => f != null && !f.IsPlayer).Select(f => (object)f.Name).Distinct().ToList();
                if (factions != null && factions.Count > 0) o["factions"] = factions;
                return o;
            }
            var o2 = Obj();
            o2["offers"] = all.Where(q => q.State == QuestState.NotYetAccepted).Select(Info).ToList();
            o2["ongoing"] = all.Where(q => q.State == QuestState.Ongoing).Select(Info).ToList();
            o2["recently_ended"] = all.Where(q => q.Historical).OrderByDescending(q => q.appearanceTick).Take(5)
                .Select(q => (object)(q.name + " (" + q.State + ")")).ToList();
            return o2;
        }

        /// <summary>보상 선택지 (여러 개면 하나를 고른다). 선택지마다 보상 설명 목록.</summary>
        static List<object> QuestRewards(Quest q)
        {
            var result = new List<object>();
            foreach (var part in q.PartsListForReading.OfType<QuestPart_Choice>())
            {
                foreach (var choice in part.choices)
                {
                    var list = new List<object>();
                    foreach (var r in choice.rewards)
                    {
                        switch (r)
                        {
                            case Reward_Items items:
                                list.AddRange(items.ItemsListForReading.Select(t => (object)t.LabelCap.ToString()));
                                break;
                            case Reward_Pawn pawn when pawn.pawn != null:
                                list.Add("joins the colony: " + pawn.pawn.LabelShortCap + " (" + pawn.pawn.KindLabel + ")");
                                break;
                            case Reward_Goodwill gw when gw.faction != null:
                                list.Add("goodwill " + gw.faction.Name + " " + gw.amount.ToString("+0;-0"));
                                break;
                            default:
                                string desc = null;
                                try { desc = StripText(r.GetDescription(default(RewardsGeneratorParams)), 150); }
                                catch
                                {
                                    // 설명을 만들 수 없는 보상은 종류 이름만
                                }
                                list.Add(string.IsNullOrEmpty(desc) ? r.GetType().Name.Replace("Reward_", "") : desc);
                                break;
                        }
                    }
                    if (list.Count > 0) result.Add(list);
                }
            }
            return result;
        }

        // ---------------------------------------------------------------- world

        static object World()
        {
            var o = Obj();
            var home = Map;
            var homeTile = home.Tile;

            o["factions"] = Find.FactionManager.AllFactionsVisibleInViewOrder
                .Where(f => !f.IsPlayer && !f.temporary)
                .Select(f =>
                {
                    var d = Obj();
                    d["name"] = f.Name;
                    d["kind"] = f.def.LabelCap.ToString();
                    d["relation"] = f.PlayerRelationKind.GetLabelCap();
                    if (f.HasGoodwill) d["goodwill"] = f.PlayerGoodwill;
                    if (f.def.permanentEnemy) d["permanent_enemy"] = true;
                    d["tech_level"] = f.def.techLevel.ToStringHuman();
                    if (f.leader != null) d["leader"] = f.leader.LabelShortCap + (string.IsNullOrEmpty(f.LeaderTitle) ? "" : " (" + f.LeaderTitle + ")");
                    if (f.defeated) d["defeated"] = true;
                    if (ModsConfig.IdeologyActive && f.ideos?.PrimaryIdeo != null) d["ideoligion"] = f.ideos.PrimaryIdeo.name;
                    var nearest = Find.WorldObjects.Settlements.Where(s => s.Faction == f && s.Tile.Layer == homeTile.Layer)
                        .Select(s => Find.WorldGrid.ApproxDistanceInTiles(homeTile, s.Tile)).DefaultIfEmpty(-1f).Min();
                    if (nearest >= 0f) d["nearest_settlement_tiles"] = Mathf.RoundToInt(nearest);
                    return (object)d;
                }).ToList();

            o["player_maps"] = Find.Maps.Where(m => m.IsPlayerHome || m.mapPawns.FreeColonistsSpawnedCount > 0).Select(m => (object)new Dictionary<string, object>
            {
                { "name", m.Parent?.LabelCap ?? "" },
                { "home", m.IsPlayerHome },
                { "current", m == Find.CurrentMap },
                { "colonists", m.mapPawns.FreeColonistsSpawnedCount },
                { "biome", m.Biome?.LabelCap.ToString() },
                { "wealth", Mathf.RoundToInt(m.wealthWatcher.WealthTotal) },
            }).ToList();

            o["caravans"] = Find.WorldObjects.Caravans.Where(c => c.IsPlayerControlled).Select(c =>
            {
                var pawns = c.PawnsListForReading;
                var d = Obj();
                d["name"] = c.Label;
                d["colonists"] = pawns.Where(p => p.IsFreeColonist).Select(p => (object)p.LabelShort).ToList();
                int animals = pawns.Count(p => p.RaceProps.Animal), prisoners = pawns.Count(p => p.IsPrisoner);
                if (animals > 0) d["animals"] = animals;
                if (prisoners > 0) d["prisoners"] = prisoners;
                d["days_of_food"] = Math.Round(c.DaysWorthOfFood.Item1, 1);
                d["mass"] = Mathf.RoundToInt(c.MassUsage) + "/" + Mathf.RoundToInt(c.MassCapacity) + " kg";
                d["moving"] = c.pather != null && c.pather.Moving;
                if (c.pather != null && c.pather.Moving && c.pather.Destination.Valid)
                {
                    var target = Find.WorldObjects.ObjectsAt(c.pather.Destination).FirstOrDefault();
                    d["destination"] = target != null ? target.LabelCap : "tile " + c.pather.Destination.tileId;
                }
                if (c.NightResting) d["resting_for_night"] = true;
                if (c.Tile.Layer == homeTile.Layer) d["tiles_from_home"] = Mathf.RoundToInt(Find.WorldGrid.ApproxDistanceInTiles(homeTile, c.Tile));
                return (object)d;
            }).ToList();

            var world = Find.World.gameConditionManager.ActiveConditions.Select(c => (object)(c.LabelCap + (c.Permanent ? "" : " (" + c.TicksLeft.ToStringTicksToPeriod() + " left)"))).ToList();
            if (world.Count > 0) o["world_conditions"] = world;
            return o;
        }

        // ---------------------------------------------------------------- production

        static object Production()
        {
            var benches = Map.listerBuildings.allBuildingsColonist.Where(b => b is IBillGiver g && g.BillStack != null).ToList();
            var withBills = new List<object>();
            var idle = new Dictionary<string, int>();
            foreach (var b in benches)
            {
                var stack = ((IBillGiver)b).BillStack;
                var power = b.TryGetComp<CompPowerTrader>();
                if (stack.Count == 0)
                {
                    string key = b.LabelCap + (power != null && !power.PowerOn ? " (no power)" : "");
                    idle[key] = idle.TryGetValue(key, out int n) ? n + 1 : 1;
                    continue;
                }
                var d = Obj();
                d["bench"] = b.LabelCap;
                if (power != null) d["powered"] = power.PowerOn;
                d["bills"] = stack.Bills.Select(bill =>
                {
                    var x = Obj();
                    x["recipe"] = bill.LabelCap;
                    if (bill is Bill_Production bp)
                    {
                        x["repeat"] = bp.RepeatInfoText;
                        if (bp.paused) x["paused_target_reached"] = true;
                    }
                    if (bill.suspended) x["suspended"] = true;
                    if (bill.allowedSkillRange.min > 0 || bill.allowedSkillRange.max < 20) x["skill_range"] = bill.allowedSkillRange.min + "-" + bill.allowedSkillRange.max;
                    if (!bill.CompletableEver) x["can_never_complete"] = true;
                    return (object)x;
                }).ToList();
                withBills.Add(d);
            }
            var o = Obj();
            o["benches_with_bills"] = withBills;
            o["benches_without_bills"] = idle.Select(kv => (object)(kv.Key + (kv.Value > 1 ? " x" + kv.Value : ""))).ToList();
            return o;
        }

        // ---------------------------------------------------------------- farming

        static object Farming()
        {
            var map = Map;
            var o = Obj();
            o["outdoor_temp_c"] = Math.Round(map.mapTemperature.OutdoorTemp, 1);
            o["season"] = GenLocalDate.Season(map).LabelCap().ToString();
            try { o["growing_period"] = StripText(Zone_Growing.GrowingQuadrumsDescription(map, null), 200); }
            catch
            {
                // 계산할 수 없는 맵 (우주 등)
            }

            Dictionary<string, object> Crops(ThingDef plantDef, List<IntVec3> cells, IEnumerable<Plant> plants)
            {
                var d = Obj();
                d["crop"] = plantDef?.LabelCap.ToString() ?? "none";
                d["cells"] = cells.Count;
                var mine = plants.Where(p => plantDef == null || p.def == plantDef).ToList();
                d["planted"] = mine.Count;
                if (mine.Count > 0)
                {
                    d["avg_growth_pct"] = Pct(mine.Average(p => p.Growth));
                    int ready = mine.Count(p => p.HarvestableNow);
                    if (ready > 0) d["harvestable_now"] = ready;
                    var growing = mine.Where(p => !p.HarvestableNow && p.GrowthRate > 0f).ToList();
                    var ticks = growing.Select(TicksUntilGrown).Where(t => t >= 0).ToList();
                    if (ticks.Count > 0) d["days_to_harvest_min_avg"] = Math.Round(ticks.Min() / 60000f, 1) + " / " + Math.Round(ticks.Average() / 60000f, 1);
                    int notGrowing = mine.Count(p => !p.HarvestableNow && p.GrowthRate <= 0f);
                    if (notGrowing > 0) d["not_growing"] = notGrowing;
                    int blighted = mine.Count(p => p.Blighted), dying = mine.Count(p => p.Dying);
                    if (blighted > 0) d["blighted"] = blighted;
                    if (dying > 0) d["dying"] = dying;
                }
                if (cells.Count > 0)
                {
                    d["avg_fertility_pct"] = Pct(cells.Average(c => map.fertilityGrid.FertilityAt(c)));
                    if (plantDef != null) d["can_grow_now"] = PlantUtility.GrowthSeasonNow(cells[0], map, plantDef);
                }
                return d;
            }

            o["zones"] = map.zoneManager.AllZones.OfType<Zone_Growing>().Select(z =>
            {
                var cells = z.Cells;
                var plants = cells.Select(c => c.GetPlant(map)).Where(p => p != null);
                var d = Crops(z.GetPlantDefToGrow(), cells, plants);
                d = new Dictionary<string, object> { { "zone", z.label } }.Concat(d).ToDictionary(kv => kv.Key, kv => kv.Value);
                if (!z.allowSow) d["sowing_disabled"] = true;
                return (object)d;
            }).ToList();

            var growers = map.listerBuildings.allBuildingsColonist.OfType<Building_PlantGrower>().ToList();
            if (growers.Count > 0)
            {
                o["growers"] = growers.GroupBy(g => (g.def, plant: g.GetPlantDefToGrow())).Select(g =>
                {
                    var cells = g.SelectMany(b => b.OccupiedRect().Cells).ToList();
                    var d = Crops(g.Key.plant, cells, g.SelectMany(b => b.PlantsOnMe));
                    d = new Dictionary<string, object> { { "building", g.Key.def.LabelCap + " x" + g.Count() } }.Concat(d).ToDictionary(kv => kv.Key, kv => kv.Value);
                    return (object)d;
                }).ToList();
            }
            return o;
        }

        // 게임 화면의 '다 자랄 때까지' 값 (밤에 쉬는 시간 포함). protected 라서 리플렉션으로 읽는다.
        static readonly System.Reflection.PropertyInfo ticksUntilGrownProp =
            typeof(Plant).GetProperty("TicksUntilFullyGrown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);

        static int TicksUntilGrown(Plant p)
        {
            try { return ticksUntilGrownProp != null ? (int)ticksUntilGrownProp.GetValue(p) : -1; }
            catch { return -1; }
        }

        // ---------------------------------------------------------------- medical

        static object Medical()
        {
            var map = Map;
            var o = Obj();
            var ps = Find.PlaySettings;
            o["default_care"] = new Dictionary<string, object>
            {
                { "colonists", ps.defaultCareForColonist.GetLabel() },
                { "prisoners", ps.defaultCareForPrisoner.GetLabel() },
                { "slaves", ps.defaultCareForSlave.GetLabel() },
                { "animals", ps.defaultCareForTamedAnimal.GetLabel() },
            };
            o["medicine"] = map.resourceCounter.AllCountedAmounts.Where(kv => kv.Key.IsMedicine && kv.Value > 0).ToDictionary(kv => kv.Key.LabelCap.ToString(), kv => (object)kv.Value);
            var beds = map.listerBuildings.allBuildingsColonist.OfType<Building_Bed>().Where(b => b.Medical).ToList();
            o["medical_beds"] = beds.Count;

            var doctor = DefDatabase<WorkTypeDef>.GetNamedSilentFail("Doctor");
            if (doctor != null)
            {
                o["doctors"] = map.mapPawns.FreeColonistsSpawned.Where(p => !p.WorkTypeIsDisabled(doctor) && p.skills != null)
                    .OrderByDescending(p => p.skills.GetSkill(SkillDefOf.Medicine).Level)
                    .Select(p => (object)(p.LabelShort + ": medicine " + p.skills.GetSkill(SkillDefOf.Medicine).Level + PassionMark(p.skills.GetSkill(SkillDefOf.Medicine).passion)
                                          + ", doctor priority " + (p.workSettings?.GetPriority(doctor) ?? 0))).ToList();
            }

            var pawns = map.mapPawns.FreeColonistsSpawned
                .Concat(map.mapPawns.PrisonersOfColonySpawned)
                .Concat(map.mapPawns.SlavesOfColonySpawned)
                .Concat(map.mapPawns.SpawnedColonyAnimals)
                .Distinct().ToList();
            var patients = new List<object>();
            int healthy = 0;
            foreach (var p in pawns)
            {
                var conditions = p.health.hediffSet.hediffs.Where(h => h.Visible && (h.Bleeding || h.TendableNow(false) || h.TryGetComp<HediffComp_Immunizable>() != null
                                                                                   || (h.def.isBad && h.def.lethalSeverity > 0f))).ToList();
                var surgery = p.health.surgeryBills?.Bills?.Select(b => (object)b.LabelCap).ToList() ?? new List<object>();
                if (conditions.Count == 0 && surgery.Count == 0 && !p.Downed)
                {
                    healthy++;
                    continue;
                }
                var d = Obj();
                d["name"] = p.LabelShortCap;
                d["who"] = p.IsFreeColonist ? "colonist" : p.IsPrisonerOfColony ? "prisoner" : p.IsSlaveOfColony ? "slave" : p.KindLabel;
                d["health_pct"] = Pct(p.health.summaryHealth.SummaryHealthPercent);
                if (p.Downed) d["downed"] = true;
                float bleed = p.health.hediffSet.BleedRateTotal;
                if (bleed > 0.01f)
                {
                    d["bleeding_per_day"] = Math.Round(bleed, 2);
                    int ticksToDeath = HealthUtility.TicksUntilDeathDueToBloodLoss(p);
                    if (ticksToDeath < 60000 * 5) d["dies_from_blood_loss_in"] = ticksToDeath.ToStringTicksToPeriod();
                }
                var bed = p.CurrentBed();
                if (bed != null) d["in_bed"] = bed.Medical ? "medical bed" : "bed";
                if (p.playerSettings != null) d["care"] = p.playerSettings.medCare.GetLabel();
                d["conditions"] = conditions.Select(h => (object)DescribeCondition(p, h)).ToList();
                if (surgery.Count > 0) d["surgery_queued"] = surgery;
                patients.Add(d);
            }
            o["patients"] = patients;
            o["healthy_count"] = healthy;
            return o;
        }

        /// <summary>상태 하나. 병이면 면역과 악화 속도를 비교해 살아남을지 가늠한다.</summary>
        static string DescribeCondition(Pawn p, Hediff h)
        {
            string s = h.LabelCap;
            if (h.Part != null) s += " @ " + h.Part.LabelCap;
            if (h.Bleeding) s += ", bleeding";
            if (h.TendableNow(false)) s += ", needs tending";
            else if (h.IsTended()) s += ", tended";
            var imm = h.TryGetComp<HediffComp_Immunizable>();
            if (imm != null && h.def.lethalSeverity > 0f && !imm.FullyImmune)
            {
                float lethal = h.def.lethalSeverity;
                s += ", severity " + Pct(h.Severity / lethal) + "% of lethal, immunity " + Pct(imm.Immunity) + "%";
                float immPerDay = imm.Props.immunityPerDaySick * p.GetStatValue(StatDefOf.ImmunityGainSpeed);
                float sevPerDay = imm.Props.severityPerDayNotImmune;
                var tend = h.TryGetComp<HediffComp_TendDuration>();
                if (tend != null && tend.IsTended) sevPerDay += tend.TProps.severityPerDayTended * tend.tendQuality;
                if (immPerDay > 0f)
                {
                    float daysImmune = (1f - imm.Immunity) / immPerDay;
                    s += ", immune in ~" + Math.Round(daysImmune, 1) + " days";
                    if (sevPerDay > 0f)
                    {
                        float daysLethal = (lethal - h.Severity) / sevPerDay;
                        s += daysImmune < daysLethal ? " (should survive at the current rate, lethal in ~" + Math.Round(daysLethal, 1) + " days)"
                                                     : " (AT RISK: lethal in ~" + Math.Round(daysLethal, 1) + " days, keep it tended)";
                    }
                }
            }
            else if (imm != null && imm.FullyImmune) s += ", immune";
            return s;
        }

        // ---------------------------------------------------------------- social

        const int FriendOpinion = 20;
        const int RivalOpinion = -20;

        static object Social()
        {
            var colonists = Map.mapPawns.FreeColonistsSpawned.Where(p => p.relations != null).ToList();
            var o = Obj();
            o["colonists"] = colonists.Select(p =>
            {
                var d = Obj();
                d["name"] = p.LabelShortCap;
                var others = colonists.Where(x => x != p).Select(x => (x, op: p.relations.OpinionOf(x))).ToList();
                if (others.Count > 0)
                {
                    var best = others.OrderByDescending(x => x.op).First();
                    var worst = others.OrderBy(x => x.op).First();
                    d["likes_most"] = best.x.LabelShort + " " + best.op.ToString("+0;-0;0");
                    if (worst.x != best.x) d["likes_least"] = worst.x.LabelShort + " " + worst.op.ToString("+0;-0;0");
                }
                var rels = p.relations.DirectRelations.Where(r => r.otherPawn != null && !r.otherPawn.Dead)
                    .Select(r => (object)(r.def.GetGenderSpecificLabelCap(r.otherPawn) + ": " + r.otherPawn.LabelShort + (r.otherPawn.IsFreeColonist ? "" : " (not a colonist here)"))).ToList();
                if (rels.Count > 0) d["relations"] = rels;
                return (object)d;
            }).ToList();

            var pairs = new List<(Pawn a, Pawn b, int ab, int ba)>();
            for (int i = 0; i < colonists.Count; i++)
                for (int j = i + 1; j < colonists.Count; j++)
                    pairs.Add((colonists[i], colonists[j], colonists[i].relations.OpinionOf(colonists[j]), colonists[j].relations.OpinionOf(colonists[i])));
            string Pair((Pawn a, Pawn b, int ab, int ba) x) => x.a.LabelShort + " -> " + x.b.LabelShort + " " + x.ab.ToString("+0;-0;0") + ", " + x.b.LabelShort + " -> " + x.a.LabelShort + " " + x.ba.ToString("+0;-0;0");
            o["rivalries"] = pairs.Where(x => Math.Min(x.ab, x.ba) <= RivalOpinion).OrderBy(x => x.ab + x.ba).Take(10).Select(x => (object)Pair(x)).ToList();
            o["friendships"] = pairs.Where(x => x.ab >= FriendOpinion && x.ba >= FriendOpinion).OrderByDescending(x => x.ab + x.ba).Take(10).Select(x => (object)Pair(x)).ToList();
            return o;
        }

        // ---------------------------------------------------------------- trade

        static object Trade(string filter)
        {
            var map = Map;
            var o = Obj();
            o["colony_silver"] = map.resourceCounter.Silver;
            if (TradeSession.Active && TradeSession.deal != null)
            {
                var t = TradeSession.trader;
                o["trading_with"] = t.TraderName + " (" + t.TraderKind?.LabelCap.ToString() + ")";
                if (t.Faction != null) o["trader_faction"] = t.Faction.Name;
                if (TradeSession.playerNegotiator != null)
                    o["negotiator"] = TradeSession.playerNegotiator.LabelShort + ", social " + TradeSession.playerNegotiator.skills?.GetSkill(SkillDefOf.Social).Level;
                var goods = TradeSession.deal.AllTradeables.Where(x => !x.IsCurrency && x.ThingDef != null
                    && (string.IsNullOrEmpty(filter) || x.Label.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
                o["trader_sells"] = goods.Where(x => x.CountHeldBy(Transactor.Trader) > 0)
                    .OrderByDescending(x => x.GetPriceFor(TradeAction.PlayerBuys) * x.CountHeldBy(Transactor.Trader)).Take(40)
                    .Select(x => (object)(x.Label + " x" + x.CountHeldBy(Transactor.Trader) + " @ " + Math.Round(x.GetPriceFor(TradeAction.PlayerBuys), 1))).ToList();
                o["colony_can_sell"] = goods.Where(x => x.CountHeldBy(Transactor.Colony) > 0 && x.TraderWillTrade)
                    .OrderByDescending(x => x.GetPriceFor(TradeAction.PlayerSells) * x.CountHeldBy(Transactor.Colony)).Take(40)
                    .Select(x => (object)(x.Label + " x" + x.CountHeldBy(Transactor.Colony) + " @ " + Math.Round(x.GetPriceFor(TradeAction.PlayerSells), 1))).ToList();
                o["note"] = "Prices are per unit in silver for this trader and negotiator.";
                return o;
            }
            o["visiting_traders"] = map.mapPawns.AllPawnsSpawned.Where(p => p.trader != null && p.trader.CanTradeNow)
                .Select(p => (object)(p.LabelShort + " - " + p.trader.traderKind?.LabelCap.ToString() + (p.Faction != null ? " (" + p.Faction.Name + ")" : ""))).ToList();
            o["orbital_traders"] = map.passingShipManager.passingShips.Where(s => !s.Departed)
                .Select(s => (object)(s.FullTitle + ", leaves in " + Period(s.ticksUntilDeparture))).ToList();
            var consoles = map.listerBuildings.allBuildingsColonist.OfType<Building_CommsConsole>().ToList();
            o["comms_console"] = consoles.Count == 0 ? "none" : consoles.Any(c => c.CanUseCommsNow) ? "ready" : "not usable now (no power or broken)";
            o["note"] = "No trade window is open. Open trade with a trader to see goods and prices.";
            return o;
        }

        // ---------------------------------------------------------------- trends

        static object Trends(int days)
        {
            days = Mathf.Clamp(days, 1, 60);
            int now = Find.TickManager.TicksGame;
            var series = new List<object>();
            // 개발 모드 전용 그래프(디버그)는 플레이어가 보는 기록 탭에 없어서 뺀다
            foreach (var group in Find.History.Groups().Where(g => !g.def.devModeOnly))
            {
                foreach (var rec in group.recorders)
                {
                    var values = rec.records;
                    int freq = rec.def.recordTicksFrequency;
                    if (values == null || values.Count == 0 || freq <= 0) continue;
                    // records[i] 는 i * freq 틱의 값 (게임 시작부터)
                    var points = new List<object>();
                    int step = Math.Max(1, days <= 10 ? 1 : days / 10);
                    for (int d = days; d >= 0; d -= step)
                    {
                        int index = Mathf.Clamp((now - d * 60000) / freq, 0, values.Count - 1);
                        if (now - d * 60000 < 0) continue;
                        points.Add(new Dictionary<string, object> { { "days_ago", d }, { "value", Math.Round(values[index], 1) } });
                    }
                    if (points.Count == 0) continue;
                    float first = Convert.ToSingle(((Dictionary<string, object>)points[0])["value"]);
                    float last = values[values.Count - 1];
                    series.Add(new Dictionary<string, object>
                    {
                        { "graph", group.def.LabelCap.ToString() },
                        { "series", rec.def.LabelCap.ToString() },
                        { "now", Math.Round(last, 1) },
                        { "change", Math.Round(last - first, 1) },
                        { "points", points },
                    });
                }
            }
            return new Dictionary<string, object> { { "days", days }, { "series", series } };
        }
    }
}
