using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace AIAdvisor
{
    /// <summary>
    /// get_map_layout: 맵 일부를 글자 격자로 그린다. 모델은 맵을 볼 수 없어서, 이게 없으면 방어선·동선·방 배치를 짐작으로만 말한다.
    /// </summary>
    public static partial class GameTools
    {
        const int DefaultLayoutRadius = 20;
        const int MaxLayoutRadius = 35;

        static readonly Dictionary<string, object> LayoutLegend = new Dictionary<string, object>
        {
            { "@", "colonist" }, { "X", "hostile" }, { "a", "other pawn or animal" }, { "f", "fire" },
            { "#", "wall or other impassable building" }, { "D", "door" }, { "T", "turret" }, { "B", "bed" },
            { "W", "work or research bench" }, { "E", "power generator or battery" }, { "S", "shelf or other storage" }, { "t", "table" },
            { "p", "plant pot or hydroponics" }, { "=", "cover (sandbags, barricade, other waist-high building)" }, { "o", "other building" },
            { "^", "natural rock" }, { "~", "water" }, { "g", "growing zone" }, { "s", "stockpile zone" },
            { ",", "roofed floor" }, { ".", "open ground" }, { "?", "unexplored" },
        };

        static void RegisterMapTools()
        {
            Add("get_map_layout",
                "Draw part of the current map as a character grid (north up, one character per cell) with walls, doors, turrets, beds, benches, power, cover, rock, water, zones, roofs and pawns. " +
                "Use it for questions about base layout, defenses and killboxes, where to build, and where enemies are relative to the base. Coordinates match the (x,z) positions other tools report.",
                new Dictionary<string, object>
                {
                    { "type", "object" },
                    { "properties", new Dictionary<string, object>
                        {
                            { "center", new Dictionary<string, object> { { "type", "string" }, { "description", "Optional: a pawn name or 'x,z'. Default: the middle of the home area." } } },
                            { "radius", new Dictionary<string, object> { { "type", "integer" }, { "description", "Cells from the center to each edge (default " + DefaultLayoutRadius + ", max " + MaxLayoutRadius + ")." } } },
                        }
                    },
                },
                a => MapLayout(StrArg(a, "center"), (int)NumArg(a, "radius", DefaultLayoutRadius)));
        }

        static object MapLayout(string center, int radius)
        {
            var map = Map;
            radius = Mathf.Clamp(radius, 5, MaxLayoutRadius);
            IntVec3 c = LayoutCenter(map, center);
            int x0 = Mathf.Max(0, c.x - radius), x1 = Mathf.Min(map.Size.x - 1, c.x + radius);
            int z0 = Mathf.Max(0, c.z - radius), z1 = Mathf.Min(map.Size.z - 1, c.z + radius);

            var rows = new List<object>();
            var used = new HashSet<char>();
            for (int z = z1; z >= z0; z--)
            {
                var sb = new StringBuilder(x1 - x0 + 1);
                for (int x = x0; x <= x1; x++)
                {
                    char ch = LayoutChar(new IntVec3(x, 0, z), map);
                    used.Add(ch);
                    sb.Append(ch);
                }
                rows.Add(sb.ToString());
            }
            return new Dictionary<string, object>
            {
                { "center", c.x + "," + c.z },
                { "x_from_to", x0 + ".." + x1 + " (left to right)" },
                { "z_from_to", z1 + ".." + z0 + " (top to bottom, north is up)" },
                { "legend", LayoutLegend.Where(kv => used.Contains(kv.Key[0])).ToDictionary(kv => kv.Key, kv => kv.Value) },
                { "rows", rows },
            };
        }

        static IntVec3 LayoutCenter(Map map, string center)
        {
            if (!string.IsNullOrWhiteSpace(center))
            {
                var parts = center.Split(',');
                if (parts.Length == 2 && int.TryParse(parts[0].Trim(), out int x) && int.TryParse(parts[1].Trim(), out int z))
                {
                    var cell = new IntVec3(x, 0, z);
                    if (!cell.InBounds(map)) throw new ArgumentException("(" + x + "," + z + ") is outside the map (size " + map.Size.x + "x" + map.Size.z + ").");
                    return cell;
                }
                return FindPawn(center).Position;
            }
            // 기본: 집 구역의 가운데. 없으면 정착민들의 가운데, 그것도 없으면 맵 가운데.
            var home = map.areaManager.Home?.ActiveCells.ToList();
            var points = home != null && home.Count > 0 ? home : map.mapPawns.FreeColonistsSpawned.Select(p => p.Position).ToList();
            if (points.Count == 0) return map.Center;
            return new IntVec3(Mathf.RoundToInt((float)points.Average(p => p.x)), 0, Mathf.RoundToInt((float)points.Average(p => p.z)));
        }

#if SELFTEST
        public static char LayoutCharForTest(IntVec3 c, Map map) => LayoutChar(c, map);
#endif

        static char LayoutChar(IntVec3 c, Map map)
        {
            if (c.Fogged(map)) return '?';
            var pawn = c.GetFirstPawn(map);
            if (pawn != null)
            {
                if (pawn.IsFreeColonist) return '@';
                if (pawn.HostileTo(Faction.OfPlayer) && !pawn.IsPrisonerOfColony) return 'X';
                return 'a';
            }
            if (FireUtility.ContainsStaticFire(c, map)) return 'f';
            var b = c.GetEdifice(map);
            if (b != null)
            {
                if (b.def.building != null && b.def.building.isNaturalRock) return '^';
                if (b.def.IsDoor) return 'D';
                if (b is Building_Turret) return 'T';
                if (b is Building_Bed) return 'B';
                if (b is IBillGiver || b is Building_ResearchBench) return 'W';
                if (b.TryGetComp<CompPowerPlant>() != null || b.TryGetComp<CompPowerBattery>() != null) return 'E';
                if (b.def.passability == Traversability.Impassable) return '#';
                // 선반과 탁자도 허리 높이라 채움 비율만 보면 엄폐물처럼 보인다
                if (b is Building_Storage) return 'S';
                if (b.def.surfaceType == SurfaceType.Eat) return 't';
                if (b is Building_PlantGrower) return 'p';
                if (b.def.fillPercent >= 0.4f) return '=';
                return 'o';
            }
            if (c.GetTerrain(map).IsWater) return '~';
            var zone = c.GetZone(map);
            if (zone is Zone_Growing) return 'g';
            if (zone is Zone_Stockpile) return 's';
            return c.Roofed(map) ? ',' : '.';
        }
    }
}
