using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIAdvisor
{
    /// <summary>
    /// 확장팩 전용 도구. 그 확장팩이 켜져 있을 때만 등록해서, 없는 사람에게는 도구 설명 토큰을 쓰지 않는다.
    /// (도구 목록은 게임을 켤 때 한 번 만든다. 확장팩은 게임을 다시 켜야 바뀐다.)
    /// </summary>
    public static partial class GameTools
    {
        static void RegisterDlcTools()
        {
            if (ModsConfig.IdeologyActive)
                Add("get_ideology",
                    "Ideology DLC: the colony's ideoligion (memes, precepts, rituals), who holds each role, and every colonist's ideoligion and certainty.",
                    NoArgs(), _ => Ideology());
            if (ModsConfig.BiotechActive)
                Add("get_biotech",
                    "Biotech DLC: mechanitors (bandwidth, control groups, controlled mechs), children (age, growth tier, progress), each colonist's xenotype and genes, and map pollution.",
                    NoArgs(), _ => Biotech());
            if (ModsConfig.RoyaltyActive)
                Add("get_royalty",
                    "Royalty DLC: colonists' royal titles with favor, permits, psylink level and known psycasts.",
                    NoArgs(), _ => Royalty());
            if (ModsConfig.AnomalyActive)
                Add("get_anomaly",
                    "Anomaly DLC: monolith level and every captured entity on a holding platform with containment strength vs what it needs, escape risk and containment mode.",
                    NoArgs(), _ => Anomaly());
            if (ModsConfig.OdysseyActive)
                Add("get_gravship",
                    "Odyssey DLC: the colony's gravship: fuel, fuel use per tile, maximum launch distance, installed and missing components.",
                    NoArgs(), _ => Gravship());
        }

        static IEnumerable<Pawn> Colonists => Map.mapPawns.FreeColonistsSpawned;

        // ---------------------------------------------------------------- Ideology

        static object Ideology()
        {
            var o = Obj();
            var ideo = Faction.OfPlayer.ideos?.PrimaryIdeo;
            if (ideo != null)
            {
                o["name"] = ideo.name;
                o["memes"] = Labels(ideo.memes);
                var precepts = ideo.PreceptsListForReading;
                o["precepts"] = precepts.Where(p => !(p is Precept_Role) && !(p is Precept_Ritual) && p.def.visible)
                    .Select(p => (object)(p.def.issue != null ? p.def.issue.LabelCap + ": " + p.LabelCap : p.LabelCap.ToString())).Take(40).ToList();
                o["rituals"] = precepts.OfType<Precept_Ritual>().Select(r => (object)r.LabelCap.ToString()).Take(15).ToList();
                o["roles"] = ideo.RolesListForReading.Where(r => r.Active).Select(r =>
                {
                    var holders = r.ChosenPawns().Select(p => p.LabelShort).ToList();
                    return (object)(r.LabelCap + ": " + (holders.Count > 0 ? string.Join(", ", holders) : "unassigned"));
                }).ToList();
            }
            o["colonists"] = Colonists.Where(p => p.ideo != null).Select(p => (object)(p.LabelShort + ": " + (p.Ideo?.name ?? "none")
                + ", certainty " + Pct(p.ideo.Certainty) + "%" + (p.Ideo?.GetRole(p) is Precept_Role role ? ", role " + role.LabelCap : ""))).ToList();
            return o;
        }

        // ---------------------------------------------------------------- Biotech

        static object Biotech()
        {
            var map = Map;
            var o = Obj();
            o["pollution_pct"] = Pct(map.pollutionGrid.TotalPollutionPercent);
            o["mechanitors"] = Colonists.Where(MechanitorUtility.IsMechanitor).Select(p =>
            {
                var m = p.mechanitor;
                return (object)new Dictionary<string, object>
                {
                    { "name", p.LabelShort },
                    { "bandwidth", m.UsedBandwidth + "/" + m.TotalBandwidth },
                    { "control_groups", m.TotalAvailableControlGroups },
                    { "mechs", m.ControlledPawns.GroupBy(x => x.KindLabel).Select(g => (object)(g.Key + " x" + g.Count())).ToList() },
                };
            }).ToList();
            o["children"] = map.mapPawns.FreeColonistsAndPrisonersSpawned.Where(p => p.DevelopmentalStage.Juvenile()).Select(p => (object)new Dictionary<string, object>
            {
                { "name", p.LabelShort },
                { "age", p.ageTracker.AgeBiologicalYears },
                { "stage", p.DevelopmentalStage.ToString() },
                { "growth_tier", p.ageTracker.GrowthTier },
                { "to_next_tier_pct", p.ageTracker.AtMaxGrowthTier ? 100 : Pct(p.ageTracker.PercentToNextGrowthTier) },
            }).ToList();
            o["genes"] = Colonists.Where(p => p.genes != null).Select(p =>
            {
                // 피부색·머리색 같은 외형 유전자는 조언에 쓸모가 없어서 뺀다
                var genes = p.genes.GenesListForReading.Where(g => g.Active && g.def.displayCategory != null && !g.def.displayCategory.defName.StartsWith("Cosmetic"))
                    .Select(g => g.LabelCap).ToList();
                return (object)new Dictionary<string, object>
                {
                    { "name", p.LabelShort },
                    { "xenotype", p.genes.XenotypeLabelCap },
                    { "genes", genes.Count > 25 ? genes.Take(25).Concat(new[] { "+" + (genes.Count - 25) + " more" }).ToList() : genes },
                };
            }).ToList();
            return o;
        }

        // ---------------------------------------------------------------- Royalty

        static object Royalty()
        {
            var list = Colonists.Where(p => p.royalty != null).Select(p =>
            {
                var d = Obj();
                d["name"] = p.LabelShort;
                var titles = p.royalty.AllTitlesForReading;
                if (titles.Count > 0)
                    d["titles"] = titles.Select(t => (object)(t.def.GetLabelCapFor(p) + " (" + t.faction.Name + "), favor " + p.royalty.GetFavor(t.faction))).ToList();
                var permits = p.royalty.AllFactionPermits;
                if (permits.Count > 0) d["permits"] = permits.Select(x => (object)x.Permit.LabelCap.ToString()).ToList();
                int psylink = p.GetPsylinkLevel();
                if (psylink > 0)
                {
                    d["psylink_level"] = psylink;
                    d["psycasts"] = p.abilities?.AllAbilitiesForReading.Where(a => a.def.IsPsycast).Select(a => (object)a.def.LabelCap.ToString()).ToList();
                }
                return d;
            }).Where(d => d.Count > 1).Select(d => (object)d).ToList();
            return new Dictionary<string, object> { { "colonists", list }, { "note", list.Count == 0 ? "No colonist has a title, permit or psylink." : "" } };
        }

        // ---------------------------------------------------------------- Anomaly

        static object Anomaly()
        {
            var o = Obj();
            var anomaly = Find.Anomaly;
            if (anomaly != null)
            {
                o["monolith_level"] = anomaly.Level;
                if (anomaly.LevelDef != null) o["monolith_stage"] = anomaly.LevelDef.LabelCap.ToString();
            }
            o["contained_entities"] = Map.listerBuildings.allBuildingsColonist.OfType<Building_HoldingPlatform>().Where(b => b.Occupied).Select(b =>
            {
                var e = b.HeldPawn;
                var d = Obj();
                d["entity"] = e.LabelCap;
                float strength = b.GetStatValue(StatDefOf.ContainmentStrength);
                float needed = e.GetStatValue(StatDefOf.MinimumContainmentStrength);
                d["containment"] = Mathf.RoundToInt(strength) + " (needs " + Mathf.RoundToInt(needed) + ")";
                if (strength < needed) d["warning"] = "containment too weak, it can escape";
                var target = e.TryGetComp<CompHoldingPlatformTarget>();
                if (target != null)
                {
                    d["mode"] = target.containmentMode.ToString();
                    if (target.isEscaping) d["escaping"] = true;
                }
                return (object)d;
            }).ToList();
            return o;
        }

        // ---------------------------------------------------------------- Odyssey

        static object Gravship()
        {
            var engines = Map.listerBuildings.allBuildingsColonist.OfType<Building_GravEngine>().ToList();
            if (engines.Count == 0) return new Dictionary<string, object> { { "note", "No grav engine on this map." } };
            return engines.Select(g =>
            {
                var d = Obj();
                d["engine"] = g.LabelCap;
                d["fuel"] = Mathf.RoundToInt(g.TotalFuel) + "/" + Mathf.RoundToInt(g.MaxFuel);
                d["fuel_per_tile"] = Math.Round(g.FuelPerTile, 2);
                d["max_launch_distance_tiles"] = g.MaxLaunchDistance;
                d["components"] = g.GravshipComponents.Where(c => c?.parent != null).GroupBy(c => c.parent.def.LabelCap.ToString())
                    .Select(x => (object)(x.Key + (x.Count() > 1 ? " x" + x.Count() : ""))).ToList();
                var missing = g.MissingComponents;
                if (missing != null && missing.Count > 0) d["missing_components"] = missing.Select(m => (object)m.LabelCap.ToString()).ToList();
                return (object)d;
            }).ToList();
        }
    }
}
