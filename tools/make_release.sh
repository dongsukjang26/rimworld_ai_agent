#!/bin/bash
# 배포용 모드 폴더를 만든다: 빌드(자체 점검 코드 제외) 후 게임에 필요한 파일만 복사.
# 사용법: tools/make_release.sh [대상 폴더]   (기본: 게임의 Mods/AIAdvisor)
set -euo pipefail

DEV="$(cd "$(dirname "$0")/.." && pwd)"
GAME_MODS="$HOME/Library/Application Support/Steam/steamapps/common/RimWorld/RimWorldMac.app/Mods"
TARGET="${1:-$GAME_MODS/AIAdvisor}"

export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export PATH="$DOTNET_ROOT:$PATH" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

echo "== build (release, no self-test)"
dotnet build "$DEV/Source/AIAdvisor.csproj" -c Release -p:SelfTest=false --nologo -v q

# 예전 개발용 심볼릭 링크가 남아 있으면 링크만 지운다 (대상 폴더 내용은 건드리지 않음)
if [ -L "$TARGET" ]; then
  echo "== removing old symlink $TARGET"
  rm "$TARGET"
fi
mkdir -p "$TARGET"

echo "== sync to $TARGET"
# Steam 업로드 후 생기는 About/PublishedFileId.txt 는 지우지 않는다 (업데이트 올릴 때 필요)
rsync -a --delete \
  --exclude 'About/PublishedFileId.txt' \
  --include 'About/***' \
  --include 'Assemblies/***' \
  --include 'Defs/***' \
  --include 'Languages/***' \
  --include 'Source/' --include 'Source/*.cs' --include 'Source/*.csproj' \
  --exclude '*' \
  "$DEV/" "$TARGET/"

# 업로드로 생긴 창작마당 ID 는 개발 폴더에도 보관해서 git 에 남긴다
if [ -f "$TARGET/About/PublishedFileId.txt" ] && ! cmp -s "$TARGET/About/PublishedFileId.txt" "$DEV/About/PublishedFileId.txt" 2>/dev/null; then
  cp "$TARGET/About/PublishedFileId.txt" "$DEV/About/PublishedFileId.txt"
  echo "== copied PublishedFileId.txt back to the dev folder"
fi

echo "== release contents"
(cd "$TARGET" && find . -type f | sort)
du -sh "$TARGET"
