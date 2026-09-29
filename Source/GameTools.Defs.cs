using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIAdvisor
{
    /// <summary>
    /// lookup_def: 게임 정의(Def)에서 아이템·무기·건물·식물·동물·연구·레시피·질병·특성·유전자의 정확한 수치를 찾는다.
    /// 게임이 실제로 읽은 데이터라 모드 콘텐츠까지 맞는 값이 나온다 (웹 검색은 비용이 들고 모드를 모른다).
    /// </summary>
    public static partial class GameTools
    {
        static readonly string[] DefKinds = { "item", "weapon", "apparel", "building", "plant", "animal", "research", "recipe", "condition", "trait", "gene" };

        static void RegisterDefTools()
        {
            Add("lookup_def",
                "Look up exact game data by name: items, weapons, apparel, buildings, plants, animals, research projects, recipes, health conditions and diseases, traits and genes. " +
                "It reads the game's own definitions, so the numbers are exact for this game version and include content from the player's mods. " +
                "Use it instead of guessing or searching the web for stats, costs, work amounts, research prerequisites, crop growth, disease progression and similar facts.",
                new Dictionary<string, object>
                {
                    { "type", "object" },
                    { "properties", new Dictionary<string, object>
                        {
                            { "query", new Dictionary<string, object> { { "type", "string" }, { "description", "Name or part of it, in the game's language or English (defName also works), e.g. 'simple meal', 'assault rifle', 'plague'." } } },
                            { "kind", new Dictionary<string, object> { { "type", "string" }, { "enum", DefKinds.Cast<object>().ToList() }, { "description", "Optional. Only this kind of definition." } } },
                            { "limit", new Dictionary<string, object> { { "type", "integer" }, { "description", "How many matches to return with details (default 5, max 10)." } } },
                        }
                    },
                    { "required", new List<object> { "query" } },
                },
                a => LookupDef(StrArg(a, "query"), StrArg(a, "kind"), (int)NumArg(a, "limit", 5)));
        }

        class DefHit
        {
            public Def def;
            public string kind;
            public int score;
        }

        static object LookupDef(string query, string kind, int limit)
        {
            if (string.IsNullOrWhiteSpace(query)) throw new ArgumentException("query is required");
            query = query.Trim();
            if (!string.IsNullOrEmpty(kind) && !DefKinds.Contains(kind)) throw new ArgumentException("Unknown kind '" + kind + "'. Valid: " + string.Join(", ", DefKinds));
            limit = Mathf.Clamp(limit, 1, 10);
            string q = Normalize(query);
            var qWords = query.ToLowerInvariant().Split(new[] { ' ', '-', '_' }, StringSplitOptions.RemoveEmptyEntries);

            var hits = new List<DefHit>();
            void Consider(Def def, string k, IEnumerable<string> extraLabels = null)
            {
                if (def == null || (!string.IsNullOrEmpty(kind) && k != kind)) return;
                int best = Score(q, qWords, def.label, def.defName);
                if (extraLabels != null)
                    foreach (var l in extraLabels) best = Math.Min(best, Score(q, qWords, l, null));
                if (best < int.MaxValue) hits.Add(new DefHit { def = def, kind = k, score = best });
            }

            foreach (var d in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                string k = ThingKind(d);
                if (k != null) Consider(d, k);
            }
            foreach (var d in DefDatabase<ResearchProjectDef>.AllDefsListForReading) Consider(d, "research");
            foreach (var d in DefDatabase<RecipeDef>.AllDefsListForReading) Consider(d, "recipe");
            foreach (var d in DefDatabase<HediffDef>.AllDefsListForReading) Consider(d, "condition");
            foreach (var d in DefDatabase<TraitDef>.AllDefsListForReading) Consider(d, "trait", d.degreeDatas?.Select(x => x.label));
            if (ModsConfig.BiotechActive)
                foreach (var d in DefDatabase<GeneDef>.AllDefsListForReading) Consider(d, "gene");

            if (hits.Count == 0)
                return new Dictionary<string, object> { { "note", "Nothing matched '" + query + "'" + (kind != null ? " (kind " + kind + ")" : "") + ". Try a shorter or English name." } };

            // 정확히 같은 이름 → 이름이 이걸로 시작 → 이름에 들어 있음 순서. 같으면 짧은 이름(더 기본형)과 아이템 쪽을 먼저.
            var top = hits.OrderBy(h => h.score).ThenBy(h => (h.def.label ?? h.def.defName).Length).ThenBy(h => Array.IndexOf(DefKinds, h.kind))
                .Take(limit).ToList();
            var results = top.Select(h => SafeDetails(h)).ToList();
            var o = Obj();
            o["matches"] = results;
            if (hits.Count > top.Count) o["more_matches"] = hits.Count - top.Count;
            return o;
        }

        static string Normalize(string s) => (s ?? "").ToLowerInvariant().Replace(" ", "").Replace("-", "").Replace("_", "");

        static readonly Regex defNameWord = new Regex(@"[A-Z]?[a-z]+|[A-Z]+(?![a-z])|\d+", RegexOptions.Compiled);

        /// <summary>
        /// 0 = 같음, 1 = 이름이 이걸로 시작, 2 = 이름에 들어 있음, 3 = defName 에 들어 있음,
        /// 4 = 검색어 단어가 모두 defName 단어에 있음 (게임이 한국어라도 "simple meal" 로 MealSimple 을 찾게). 안 맞으면 MaxValue.
        /// </summary>
        static int Score(string q, string[] qWords, string label, string defName)
        {
            string l = Normalize(label), d = Normalize(defName);
            if (l.Length > 0 && l == q || d.Length > 0 && d == q) return 0;
            if (l.Length > 0 && l.StartsWith(q)) return 1;
            if (l.Length > 0 && l.Contains(q)) return 2;
            if (d.Length > 0 && d.Contains(q)) return 3;
            if (qWords.Length > 1 && !string.IsNullOrEmpty(defName))
            {
                var words = defNameWord.Matches(defName).Cast<Match>().Select(m => m.Value.ToLowerInvariant()).ToList();
                if (qWords.All(w => words.Any(x => x.StartsWith(w)))) return 4;
            }
            return int.MaxValue;
        }

        /// <summary>검색에 넣을 ThingDef 의 종류. 청사진, 시체, 효과 같은 내부용 정의는 뺀다.</summary>
        static string ThingKind(ThingDef d)
        {
            if (d.IsBlueprint || d.IsFrame || d.IsCorpse || d.isUnfinishedThing || d.label.NullOrEmpty()) return null;
            switch (d.category)
            {
                case ThingCategory.Item:
                    if (d.IsWeapon) return "weapon";
                    if (d.IsApparel) return "apparel";
                    return "item";
                case ThingCategory.Building: return "building";
                case ThingCategory.Plant: return "plant";
                case ThingCategory.Pawn: return d.race != null ? "animal" : null;
                default: return null;
            }
        }

        static object SafeDetails(DefHit h)
        {
            try
            {
                switch (h.kind)
                {
                    case "research": return ResearchDetails((ResearchProjectDef)h.def);
                    case "recipe": return RecipeDetails((RecipeDef)h.def);
                    case "condition": return HediffDetails((HediffDef)h.def);
                    case "trait": return TraitDetails((TraitDef)h.def);
                    case "gene": return GeneDetails((GeneDef)h.def);
                    default: return ThingDetails((ThingDef)h.def, h.kind);
                }
            }
            catch (Exception e)
            {
                // 모드 정의가 특이해서 일부를 못 읽어도 이름은 돌려준다
                return new Dictionary<string, object> { { "kind", h.kind }, { "label", h.def.LabelCap.ToString() }, { "defName", h.def.defName }, { "error", "Could not read details: " + e.Message } };
            }
        }

        static Dictionary<string, object> DefHeader(Def def, string kind)
        {
            var o = Obj();
            o["kind"] = kind;
            // 특성은 정의 자체에 이름이 없고 단계마다 이름이 있다
            string label = def.label.NullOrEmpty() && def is TraitDef t && t.degreeDatas?.Count > 0 ? t.degreeDatas[0].label : def.label;
            o["label"] = (label ?? def.defName).CapitalizeFirst();
            o["defName"] = def.defName;
            var mod = def.modContentPack;
            if (mod != null && !mod.IsCoreMod) o["from"] = mod.Name;
            string desc = def.description;
            if (!string.IsNullOrEmpty(desc))
            {
                desc = ColoredText.StripTags(desc).Trim();
                o["description"] = desc.Length > 300 ? desc.Substring(0, 300) + "…" : desc;
            }
            return o;
        }

        static Dictionary<string, object> StatList(IEnumerable<StatModifier> mods, Func<StatModifier, string> format)
        {
            var d = Obj();
            if (mods == null) return d;
            foreach (var m in mods)
            {
                if (m?.stat == null) continue;
                try { d[m.stat.LabelCap.ToString()] = format(m); }
                catch
                {
                    // 모드 스탯 서식이 깨져 있으면 숫자만
                    d[m.stat.LabelCap.ToString()] = Math.Round(m.value, 3);
                }
            }
            return d;
        }

        static string StatValue(StatModifier m) => m.stat.ValueToString(m.value, ToStringNumberSense.Absolute, true);

        static List<object> Labels<T>(IEnumerable<T> defs) where T : Def => defs == null ? new List<object>() : defs.Where(x => x != null).Select(x => (object)x.LabelCap.ToString()).ToList();

        static Dictionary<string, object> ThingDetails(ThingDef d, string kind)
        {
            var o = DefHeader(d, kind);
            o["market_value"] = Math.Round(d.BaseMarketValue, 1);
            var stats = StatList(d.statBases?.Where(s => s.stat != StatDefOf.MarketValue), StatValue);
            if (stats.Count > 0) o["stats"] = stats;
            if (d.techLevel != TechLevel.Undefined) o["tech_level"] = d.techLevel.ToStringHuman();

            // 재료와 연구 조건 (건물, 제작 아이템)
            var cost = (d.costList ?? new List<ThingDefCountClass>()).Where(c => c.thingDef != null).Select(c => (object)(c.thingDef.LabelCap + " x" + c.count)).ToList();
            if (d.MadeFromStuff && d.stuffCategories != null)
                cost.Insert(0, d.costStuffCount + " of any " + string.Join("/", d.stuffCategories.Select(c => c.label)));
            if (cost.Count > 0) o["cost"] = cost;
            if (d.researchPrerequisites != null && d.researchPrerequisites.Count > 0)
                o["research_required"] = d.researchPrerequisites.Select(r => (object)(r.LabelCap + (r.IsFinished ? " (done)" : " (not researched)"))).ToList();
            var makers = DefDatabase<RecipeDef>.AllDefsListForReading.Where(r => r.ProducedThingDef == d).ToList();
            if (makers.Count > 0)
            {
                o["made_at"] = makers.SelectMany(r => r.AllRecipeUsers ?? Enumerable.Empty<ThingDef>()).Distinct().Select(b => (object)b.LabelCap.ToString()).ToList();
                var skills = makers.SelectMany(r => r.skillRequirements ?? new List<SkillRequirement>()).Select(s => (object)(s.skill.LabelCap + " " + s.minLevel)).Distinct().ToList();
                if (skills.Count > 0) o["skill_required"] = skills;
            }

            if (d.IsWeapon) AddWeapon(o, d);
            if (d.apparel != null)
            {
                o["covers"] = d.apparel.bodyPartGroups?.Select(g => (object)g.LabelCap.ToString()).ToList();
                o["layers"] = d.apparel.layers?.Select(l => (object)l.LabelCap.ToString()).ToList();
            }
            if (d.equippedStatOffsets != null && d.equippedStatOffsets.Count > 0)
                o["when_equipped"] = StatList(d.equippedStatOffsets, m => m.ValueToStringAsOffset);
            if (d.ingestible != null)
            {
                o["food_preferability"] = d.ingestible.preferability.ToString();
                if (d.ingestible.joy > 0f) o["joy"] = Math.Round(d.ingestible.joy, 2);
            }
            if (d.category == ThingCategory.Building)
            {
                o["size"] = d.size.x + "x" + d.size.z;
                var power = d.GetCompProperties<CompProperties_Power>();
                // basePowerConsumption 이 음수면 발전기
                if (power != null) o["power_w"] = power.PowerConsumption > 0 ? "uses " + Mathf.RoundToInt(power.PowerConsumption) : "produces " + Mathf.RoundToInt(-power.PowerConsumption);
            }
            if (d.plant != null)
            {
                var p = d.plant;
                o["grow_days"] = Math.Round(p.growDays, 1);
                if (p.harvestedThingDef != null) o["harvest"] = p.harvestedThingDef.LabelCap + " x" + Math.Round(p.harvestYield, 1);
                if (p.sowMinSkill > 0) o["sow_min_skill"] = p.sowMinSkill;
                o["fertility_sensitivity"] = Math.Round(p.fertilitySensitivity, 2);
                if (p.IsTree) o["tree"] = true;
            }
            if (d.race != null)
            {
                var r = d.race;
                o["body_size"] = Math.Round(r.baseBodySize, 2);
                o["health_scale"] = Math.Round(r.baseHealthScale, 2);
                o["diet"] = r.foodType.ToString();
                if (r.trainability != null) o["trainability"] = r.trainability.LabelCap.ToString();
                if (r.packAnimal) o["pack_animal"] = true;
                if (r.predator) o["predator"] = true;
                if (r.herdAnimal) o["herd_animal"] = true;
                o["life_expectancy_years"] = Math.Round(r.lifeExpectancy, 1);
                if (r.manhunterOnDamageChance > 0f) o["revenge_chance_when_hurt"] = Pct(r.manhunterOnDamageChance) + "%";
                if (r.manhunterOnTameFailChance > 0f) o["revenge_chance_on_failed_tame"] = Pct(r.manhunterOnTameFailChance) + "%";
            }
            return o;
        }

        static void AddWeapon(Dictionary<string, object> o, ThingDef d)
        {
            var verb = d.Verbs?.FirstOrDefault(v => v.isPrimary) ?? d.Verbs?.FirstOrDefault();
            if (verb != null && verb.defaultProjectile?.projectile != null)
            {
                var proj = verb.defaultProjectile.projectile;
                var r = Obj();
                r["range"] = Math.Round(verb.range, 1);
                if (verb.minRange > 0f) r["min_range"] = Math.Round(verb.minRange, 1);
                r["warmup_s"] = Math.Round(verb.warmupTime, 2);
                if (verb.burstShotCount > 1) r["burst_shots"] = verb.burstShotCount;
                r["damage"] = proj.GetDamageAmount(d, null, null) + " " + proj.damageDef?.label;
                try { r["armor_penetration"] = Pct(proj.GetArmorPenetration(null, null)) + "%"; }
                catch
                {
                    // 모드 투사체가 무기 인스턴스를 요구하는 경우
                }
                if (proj.stoppingPower > 0f) r["stopping_power"] = Math.Round(proj.stoppingPower, 2);
                o["ranged"] = r;
            }
            if (d.tools != null && d.tools.Count > 0)
            {
                o["melee"] = d.tools.Select(t => (object)new Dictionary<string, object>
                {
                    { "part", t.label },
                    { "attacks", t.capacities?.Select(c => c.label).ToList() is List<string> caps ? string.Join("/", caps) : "" },
                    { "damage", Math.Round(t.power, 1) },
                    { "cooldown_s", Math.Round(t.cooldownTime, 2) },
                    { "armor_penetration", t.armorPenetration >= 0f ? Pct(t.armorPenetration) + "%" : "default" },
                }).ToList();
            }
        }

        static Dictionary<string, object> ResearchDetails(ResearchProjectDef r)
        {
            var o = DefHeader(r, "research");
            o["cost"] = Mathf.RoundToInt(r.CostApparent);
            o["tech_level"] = r.techLevel.ToStringHuman();
            o["status"] = r.IsFinished ? "finished" : r.CanStartNow ? "available now" : "locked";
            if (!r.IsFinished && r.ProgressPercent > 0f) o["progress_pct"] = Pct(r.ProgressPercent);
            if (r.prerequisites != null && r.prerequisites.Count > 0)
                o["prerequisites"] = r.prerequisites.Select(p => (object)(p.LabelCap + (p.IsFinished ? " (done)" : " (not done)"))).ToList();
            if (r.requiredResearchBuilding != null) o["needs_bench"] = r.requiredResearchBuilding.LabelCap.ToString();
            if (r.requiredResearchFacilities != null && r.requiredResearchFacilities.Count > 0) o["needs_facilities"] = Labels(r.requiredResearchFacilities);
            var unlocks = r.UnlockedDefs;
            if (unlocks != null && unlocks.Count > 0)
            {
                o["unlocks"] = unlocks.Take(20).Select(u => (object)u.LabelCap.ToString()).ToList();
                if (unlocks.Count > 20) o["unlocks_more"] = unlocks.Count - 20;
            }
            return o;
        }

        static Dictionary<string, object> RecipeDetails(RecipeDef r)
        {
            var o = DefHeader(r, "recipe");
            // 게임 화면(과 아이템의 '제작 작업량' 스탯)과 같은 단위로 적는다 (정의에는 틱 단위로 들어 있다)
            float work = r.workAmount > 0f ? r.workAmount : (r.ProducedThingDef?.GetStatValueAbstract(StatDefOf.WorkToMake) ?? 0f);
            o["work_amount"] = StatDefOf.WorkToMake.ValueToString(work, ToStringNumberSense.Absolute, true);
            if (r.workSkill != null) o["work_skill"] = r.workSkill.LabelCap.ToString();
            if (r.skillRequirements != null && r.skillRequirements.Count > 0)
                o["skill_required"] = r.skillRequirements.Select(s => (object)(s.skill.LabelCap + " " + s.minLevel)).ToList();
            if (r.ingredients != null && r.ingredients.Count > 0)
                o["ingredients"] = r.ingredients.Select(i => (object)(i.filter.Summary + " x" + Math.Round(i.GetBaseCount(), 2))).ToList();
            if (r.products != null && r.products.Count > 0)
                o["products"] = r.products.Where(p => p.thingDef != null).Select(p => (object)(p.thingDef.LabelCap + " x" + p.count)).ToList();
            var users = r.AllRecipeUsers?.ToList();
            if (users != null && users.Count > 0) o["made_at"] = Labels(users);
            var research = new List<ResearchProjectDef>();
            if (r.researchPrerequisite != null) research.Add(r.researchPrerequisite);
            if (r.researchPrerequisites != null) research.AddRange(r.researchPrerequisites);
            if (research.Count > 0) o["research_required"] = research.Select(p => (object)(p.LabelCap + (p.IsFinished ? " (done)" : " (not researched)"))).ToList();
            return o;
        }

        static Dictionary<string, object> HediffDetails(HediffDef h)
        {
            var o = DefHeader(h, "condition");
            if (h.tendable) o["tendable"] = true;
            if (h.chronic) o["chronic"] = true;
            if (h.lethalSeverity > 0f) o["lethal_at_severity"] = Math.Round(h.lethalSeverity, 2);
            var imm = h.CompProps<HediffCompProperties_Immunizable>();
            if (imm != null)
            {
                o["immunity_per_day_while_sick"] = Math.Round(imm.immunityPerDaySick, 3);
                o["severity_per_day_not_immune"] = Math.Round(imm.severityPerDayNotImmune, 3);
                o["severity_per_day_once_immune"] = Math.Round(imm.severityPerDayImmune, 3);
            }
            var tend = h.CompProps<HediffCompProperties_TendDuration>();
            if (tend != null && tend.severityPerDayTended != 0f) o["severity_per_day_when_tended"] = Math.Round(tend.severityPerDayTended, 3) + " x tend quality";
            if (h.stages != null && h.stages.Count > 0)
            {
                o["stages"] = h.stages.Take(8).Select(s =>
                {
                    var st = new Dictionary<string, object> { { "from_severity", Math.Round(s.minSeverity, 2) } };
                    if (!string.IsNullOrEmpty(s.label)) st["label"] = s.label;
                    var caps = (s.capMods ?? new List<PawnCapacityModifier>()).Where(c => c.capacity != null).Select(c =>
                        (object)(c.capacity.LabelCap + (c.offset != 0f ? " " + (c.offset > 0 ? "+" : "") + Pct(c.offset) + "%" : "")
                                 + (c.postFactor != 1f ? " x" + Math.Round(c.postFactor, 2) : "")
                                 + (c.setMax < 999f ? " max " + Pct(c.setMax) + "%" : ""))).ToList();
                    if (caps.Count > 0) st["capacities"] = caps;
                    if (s.painOffset != 0f) st["pain"] = (s.painOffset > 0 ? "+" : "") + Pct(s.painOffset) + "%";
                    if (s.lifeThreatening) st["life_threatening"] = true;
                    return (object)st;
                }).ToList();
            }
            return o;
        }

        static readonly Regex pawnToken = new Regex(@"\[PAWN_\w+\]|\{PAWN_\w+\}", RegexOptions.Compiled);

        static Dictionary<string, object> TraitDetails(TraitDef t)
        {
            var o = DefHeader(t, "trait");
            o.Remove("description");
            o["degrees"] = (t.degreeDatas ?? new List<TraitDegreeData>()).Select(dd =>
            {
                var x = new Dictionary<string, object> { { "label", dd.label.CapitalizeFirst() } };
                if (t.degreeDatas.Count > 1) x["degree"] = dd.degree;
                if (!string.IsNullOrEmpty(dd.description))
                {
                    string desc = ColoredText.StripTags(pawnToken.Replace(dd.description, "[pawn]")).Trim();
                    x["description"] = desc.Length > 300 ? desc.Substring(0, 300) + "…" : desc;
                }
                var offs = StatList(dd.statOffsets, m => m.ValueToStringAsOffset);
                if (offs.Count > 0) x["stat_offsets"] = offs;
                var facs = StatList(dd.statFactors, m => m.ToStringAsFactor);
                if (facs.Count > 0) x["stat_factors"] = facs;
                return (object)x;
            }).ToList();
            if (t.conflictingTraits != null && t.conflictingTraits.Count > 0) o["conflicts_with"] = Labels(t.conflictingTraits);
            if (t.disabledWorkTags != WorkTags.None) o["disables_work"] = WorkTagLabels(t.disabledWorkTags);
            if (t.disabledWorkTypes != null && t.disabledWorkTypes.Count > 0) o["disables_work_types"] = Labels(t.disabledWorkTypes);
            return o;
        }

        static Dictionary<string, object> GeneDetails(GeneDef g)
        {
            var o = DefHeader(g, "gene");
            if (g.displayCategory != null) o["category"] = g.displayCategory.LabelCap.ToString();
            o["complexity"] = g.biostatCpx;
            o["metabolism"] = g.biostatMet;
            if (g.biostatArc != 0) o["archites"] = g.biostatArc;
            var offs = StatList(g.statOffsets, m => m.ValueToStringAsOffset);
            if (offs.Count > 0) o["stat_offsets"] = offs;
            var facs = StatList(g.statFactors, m => m.ToStringAsFactor);
            if (facs.Count > 0) o["stat_factors"] = facs;
            return o;
        }
    }
}
