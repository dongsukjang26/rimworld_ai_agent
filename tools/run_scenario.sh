#!/bin/bash
# 자체 점검 시나리오를 모의 LLM 서버(tools/mock_llm.py)로 돌린다. API 키도 비용도 들지 않는다.
# 사용법: tools/run_scenario.sh <session|memory|stream|tools> [제한 초, 기본 300]
#         REAL=1 tools/run_scenario.sh real   실제 API 로 확인 (설정에 저장된 키 사용, 비용 발생. 상한: AIADVISOR_SELFTEST_BUDGET, 기본 $0.30)
#         LOADSAVE=<세이브.rws> ...             빠른 테스트 맵 대신 그 세이브의 복사본을, 평소 모드 목록으로 불러와서 확인
#
# - 자체 점검 빌드를 게임의 Mods/AIAdvisor 에 잠시 넣었다가, 끝나면 원래 파일로 되돌린다.
# - 설정과 세이브는 임시 폴더(-savedatafolder)를 쓰므로 평소 설정(API 키 포함)과 세이브는 건드리지 않는다.
#   REAL=1 이면 평소 설정 폴더를 임시 폴더로 복사해서 쓰고(키 포함), 끝나면 그 복사본을 지운다.
# - 결과는 [RimSage scenario] 줄로 출력되고, 전체 로그/스크린샷/모의 서버가 받은 요청은 작업 폴더에 남는다.
set -euo pipefail

SCENARIO="${1:?usage: tools/run_scenario.sh <session|memory|stream|tools> [timeout_s]}"
TIMEOUT="${2:-300}"
DEV="$(cd "$(dirname "$0")/.." && pwd)"
GAME_APP="${RIMWORLD_APP:-$HOME/Library/Application Support/Steam/steamapps/common/RimWorld/RimWorldMac.app}"
GAME_BIN="$GAME_APP/Contents/MacOS/RimWorld by Ludeon Studios"
MOD="$GAME_APP/Mods/AIAdvisor"
WORK="${SCENARIO_WORK:-$(mktemp -d -t rimsage-scenario)}"
PORT="${MOCK_PORT:-18999}"
REAL="${REAL:-0}"
REAL_CONFIG="$HOME/Library/Application Support/RimWorld/Config"

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

echo "== work folder: $WORK"
mkdir -p "$WORK/build" "$WORK/shots"
if [ "$REAL" = "1" ]; then
  # 키가 들어 있는 설정 파일을 그대로 복사한다 (내용을 읽거나 출력하지 않음). 대화 기록은 비우고 시작.
  mkdir -p "$WORK/data"
  cp -R "$REAL_CONFIG" "$WORK/data/Config"
  rm -rf "$WORK/data/Config/RimSage"
fi
mkdir -p "$WORK/data/Config"
dotnet build "$DEV/Source/AIAdvisor.csproj" -c Release -p:SelfTest=true -p:OutputPath="$WORK/build/" --nologo -v q

# 설치된 모드를 백업하고, 끝나면(실패해도) 되돌린다
if [ -d "$MOD" ]; then rsync -a --delete "$MOD/" "$WORK/installed/"; fi
GAME_PID=""; MOCK_PID=""
cleanup() {
  if [ -n "$GAME_PID" ]; then kill "$GAME_PID" 2>/dev/null || true; wait "$GAME_PID" 2>/dev/null || true; fi
  if [ -n "$MOCK_PID" ]; then kill "$MOCK_PID" 2>/dev/null || true; wait "$MOCK_PID" 2>/dev/null || true; fi
  if [ -d "$WORK/installed" ]; then rsync -a --delete "$WORK/installed/" "$MOD/"; else rm -rf "$MOD"; fi
  echo "== restored $MOD"
  if [ "$REAL" = "1" ] || [ -n "${LOADSAVE:-}" ]; then
    sleep 1
    rm -rf "$WORK/data"
    echo "== deleted the copied settings and saves"
  fi
}
trap cleanup EXIT

mkdir -p "$MOD/Assemblies"
for d in About Defs Languages; do rsync -a --delete "$DEV/$d/" "$MOD/$d/"; done
cp "$WORK/build/AIAdvisor.dll" "$MOD/Assemblies/AIAdvisor.dll"

# 코어 + 설치된 확장팩 + 이 모드만 켠다. 창 모드, 백그라운드에서도 실행, 소리 끔.
# 세이브를 불러올 때는 그 세이브와 맞도록 평소 모드 목록을 쓴다.
if [ -n "${LOADSAVE:-}" ]; then
  cp "$REAL_CONFIG/ModsConfig.xml" "$WORK/data/Config/ModsConfig.xml"
  mkdir -p "$WORK/data/Saves"
  cp "$LOADSAVE" "$WORK/data/Saves/"
  SAVE_NAME="$(basename "$LOADSAVE" .rws)"
else {
  echo '<?xml version="1.0" encoding="utf-8"?>'
  echo '<ModsConfigData>'
  echo "  <version>$(cat "$GAME_APP/Version.txt" 2>/dev/null || echo 1.6)</version>"
  echo '  <activeMods>'
  echo '    <li>ludeon.rimworld</li>'
  for dlc in Royalty Ideology Biotech Anomaly Odyssey; do
    [ -d "$GAME_APP/Data/$dlc" ] && echo "    <li>ludeon.rimworld.$(echo $dlc | tr '[:upper:]' '[:lower:]')</li>"
  done
  echo '    <li>dsjang.aiadvisor</li>'
  echo '  </activeMods>'
  echo '</ModsConfigData>'
} > "$WORK/data/Config/ModsConfig.xml"
fi
cat > "$WORK/data/Config/Prefs.xml" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<PrefsData>
  <volumeMaster>0</volumeMaster>
  <screenWidth>1600</screenWidth>
  <screenHeight>900</screenHeight>
  <fullscreen>False</fullscreen>
  <uiScale>1</uiScale>
  <runInBackground>True</runInBackground>
  <langFolderName>${SCENARIO_LANG:-Korean (한국어)}</langFolderName>
</PrefsData>
EOF

BASE_ENV=()
if [ "$REAL" != "1" ]; then
  : > "$WORK/requests.jsonl"
  python3 "$DEV/tools/mock_llm.py" "$PORT" "$WORK/requests.jsonl" > "$WORK/mock.log" 2>&1 &
  MOCK_PID=$!
  BASE_ENV=(AIADVISOR_SELFTEST_BASE="http://127.0.0.1:$PORT/v1/")
  sleep 1
fi

LOG="$WORK/player.log"
: > "$LOG"
START_ARGS=(-quicktest)
if [ -n "${LOADSAVE:-}" ]; then BASE_ENV+=(AIADVISOR_SELFTEST_LOADSAVE="$SAVE_NAME"); START_ARGS=(); fi
# Steam 이 창작마당 모드를 찾으려면 게임 폴더(steam_appid.txt 가 있는 곳)에서 실행해야 한다
(cd "$GAME_APP" && exec env SteamAppId=294100 AIADVISOR_SELFTEST=1 AIADVISOR_SELFTEST_SCENARIO="$SCENARIO" ${BASE_ENV[@]+"${BASE_ENV[@]}"} AIADVISOR_SELFTEST_SHOTDIR="$WORK/shots" \
    "$GAME_BIN" -savedatafolder="$WORK/data" ${START_ARGS[@]+"${START_ARGS[@]}"} -logFile "$LOG" > /dev/null 2>&1) &
GAME_PID=$!

for i in $(seq 1 "$TIMEOUT"); do
  sleep 1
  grep -q "\[RimSage scenario\] done" "$LOG" 2>/dev/null && break
  kill -0 "$GAME_PID" 2>/dev/null || { echo "== game exited early"; break; }
done
echo "== finished after ${i}s"
grep -E "\[RimSage scenario\]|\[RimSage\] |\[RimSage tool\]|Exception" "$LOG" | grep -v "^\s*at " || true
echo "== log: $LOG"
[ "$REAL" != "1" ] && echo "== requests the mock received: $WORK/requests.jsonl"
true
