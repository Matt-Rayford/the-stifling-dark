#!/usr/bin/env bash
# Sync the rules engine, the wire protocol, and game data into the Unity project.
#
#   tools/sync_unity.sh
#
# 1. Builds StiflingDark.Engine + StiflingDark.Protocol + StiflingDark.Bots (Release,
#    netstandard2.1) and copies the DLLs into Assets/Plugins. The client uses them for local
#    geometry — the flashlight beam preview runs FlashlightBeam + RasterLineOfSightBlocker +
#    MapGraph in-process — and for an offline "play vs bots" mode driven by the same bot
#    brains the arena plays with, while the SERVER stays authoritative for every online rule.
#    (Newtonsoft.Json is NOT copied — Unity's com.unity.nuget.newtonsoft-json provides it.)
# 2. Copies game-data/ (maps/*.json, the *-los-mask.bin rasters, flashlight.json, config,
#    cards) and game-assets/tokens/ into Assets/StreamingAssets.
# 3. Renders the two board textures at 4096x4096 into Assets/Textures (only if missing, or
#    with FORCE_TEXTURES=1), and clones them into StreamingAssets so built players find them.
#
# Run after any engine change, game-data edit, or fresh clone.
set -euo pipefail
cd "$(dirname "$0")/.."

DOTNET="${DOTNET:-$HOME/.dotnet/dotnet}"
UNITY_DIR="unity"
PLUGINS="$UNITY_DIR/Assets/Plugins"
STREAMING="$UNITY_DIR/Assets/StreamingAssets"
TEXTURES="$UNITY_DIR/Assets/Textures"

echo "== building engine + protocol =="
"$DOTNET" build src/StiflingDark.Protocol -c Release --nologo -v quiet

echo "== building bots =="
"$DOTNET" build src/StiflingDark.Bots -c Release --nologo -v quiet

echo "== syncing DLLs =="
mkdir -p "$PLUGINS"
cp src/StiflingDark.Engine/bin/Release/netstandard2.1/StiflingDark.Engine.dll "$PLUGINS/"
cp src/StiflingDark.Protocol/bin/Release/netstandard2.1/StiflingDark.Protocol.dll "$PLUGINS/"
cp src/StiflingDark.Bots/bin/Release/netstandard2.1/StiflingDark.Bots.dll "$PLUGINS/"

echo "== syncing game data =="
# The client loads this with the engine's own GameDatabase.Load(), so the layout under
# StreamingAssets/game-data must mirror the repo's game-data/ exactly.
rm -rf "$STREAMING/game-data" "$STREAMING/tokens"
mkdir -p "$STREAMING/game-data/maps" "$STREAMING/game-data/cards" "$STREAMING/tokens"
cp game-data/*.json "$STREAMING/game-data/"
cp game-data/cards/*.json "$STREAMING/game-data/cards/"
cp game-data/maps/*.json "$STREAMING/game-data/maps/"
# The LoS masks are what make the local flashlight preview agree with the server.
cp game-data/maps/*-los-mask.bin "$STREAMING/game-data/maps/"

echo "== syncing token art =="
# game-assets/ is gitignored, so this is a best-effort copy: the client falls back to
# colored discs with initials for anything it cannot find.
if [ -d game-assets/tokens ]; then
  cp -R game-assets/tokens/. "$STREAMING/tokens/"
  find "$STREAMING/tokens" -name '.DS_Store' -delete
fi
if [ -f game-assets/other/small-flashlight-transparent.png ]; then
  mkdir -p "$STREAMING/tokens/other"
  cp game-assets/other/small-flashlight-transparent.png "$STREAMING/tokens/other/"
fi

echo "== syncing fonts =="
# Display fonts go under Assets/Resources so Unity imports them and the code-built UI can
# Resources.Load them at runtime (StreamingAssets files never become Font assets).
if [ -d game-assets/fonts ]; then
  mkdir -p "$UNITY_DIR/Assets/Resources/Fonts"
  cp game-assets/fonts/*.otf game-assets/fonts/*.ttf "$UNITY_DIR/Assets/Resources/Fonts/" 2>/dev/null || true
fi

echo "== board textures =="
# Rendered straight from the print PDFs with pdftoppm and scaled to 4096 on the long edge.
# game-data/maps/*.json records the FULL-res render (source.imageSize / source.renderDpi);
# the client divides by it to map space coordinates onto whatever texture size it finds, so
# a different -scale-to here stays correct as long as the aspect ratio is square.
mkdir -p "$TEXTURES"
render_board() {
  local pdf="$1" out="$2"
  if [ -f "$out" ] && [ "${FORCE_TEXTURES:-0}" != "1" ]; then
    echo "  $out (present; FORCE_TEXTURES=1 to re-render)"
    return
  fi
  if [ ! -f "$pdf" ]; then
    echo "  !! $pdf missing — cannot render $out (game-assets/ is gitignored)"
    return
  fi
  if ! command -v pdftoppm >/dev/null; then
    echo "  !! pdftoppm not found (brew install poppler) — cannot render $out"
    return
  fi
  pdftoppm -png -scale-to 4096 -singlefile "$pdf" "${out%.png}"
  echo "  wrote $out"
}
render_board game-assets/maps/sawmill.pdf "$TEXTURES/board-sawmill.png"
render_board game-assets/maps/amusement-park.pdf "$TEXTURES/board-amusement-park.png"

# Clone (copy-on-write on APFS, so no extra disk) into StreamingAssets: Assets/Textures is
# only reachable in the Editor, StreamingAssets ships with a built player.
mkdir -p "$STREAMING/textures"
for png in "$TEXTURES"/board-*.png; do
  [ -f "$png" ] || continue
  cp -c "$png" "$STREAMING/textures/" 2>/dev/null || cp "$png" "$STREAMING/textures/"
done

echo "== syncing music =="
# Music goes under Assets/Resources so Unity imports it as AudioClips the code-built UI
# can Resources.Load at runtime (StreamingAssets files never become AudioClips).
if [ -d game-assets/music ]; then
  mkdir -p "$UNITY_DIR/Assets/Resources/Music"
  cp game-assets/music/*.mp3 "$UNITY_DIR/Assets/Resources/Music/" 2>/dev/null || true
fi

echo "== player boards =="
# Investigator player-board BACKS (the character-sheet side) for the solo-setup selector:
# one PNG per base Investigator, page-mapped from the print PDF (pages 1-10 are the base
# roster alphabetically; 11-12 are the excluded promos). FORCE_TEXTURES=1 re-renders.
BOARDS="$STREAMING/player-boards"
if [ -f game-assets/player-boards/investigator-backs.pdf ] && command -v pdftoppm >/dev/null; then
  mkdir -p "$BOARDS"
  page=1
  for id in aira asher brielle dylan ibraheem lucy-belle mada marci mitchell vincent; do
    out="$BOARDS/$id.png"
    if [ ! -f "$out" ] || [ "${FORCE_TEXTURES:-0}" = "1" ]; then
      pdftoppm -png -f $page -l $page -scale-to 1100 -singlefile \
        game-assets/player-boards/investigator-backs.pdf "${out%.png}"
    fi
    page=$((page+1))
  done
  # Sharp minification: pre-baked Lanczos mip pyramids (see tools/make_mips.py).
  python3 tools/make_mips.py "$BOARDS" || echo "  !! mip packing failed (pip install pillow?)"
fi
# Adversary board FRONTS (the rules side) for the solo-setup picker. Page 3 is Mor'gonnod's
# corporeal flip, not a pickable adversary. (The PDF's filename typo is in game-assets.)
ADV_BOARDS="$STREAMING/adversary-boards"
if [ -f "game-assets/player-boards/adersary-fronts.pdf" ] && command -v pdftoppm >/dev/null; then
  mkdir -p "$ADV_BOARDS"
  render_adversary() {
    local page="$1" id="$2" out="$ADV_BOARDS/$2.png"
    if [ ! -f "$out" ] || [ "${FORCE_TEXTURES:-0}" = "1" ]; then
      pdftoppm -png -f "$page" -l "$page" -scale-to 1100 -singlefile \
        "game-assets/player-boards/adersary-fronts.pdf" "${out%.png}"
    fi
  }
  render_adversary 1 butcher
  render_adversary 2 cult-of-hunlow
  render_adversary 4 insatiable-horror
  python3 tools/make_mips.py "$ADV_BOARDS" || echo "  !! mip packing failed (pip install pillow?)"
fi

echo "== client config =="
# Baked into builds but never committed (StreamingAssets is gitignored).
# Set SD_SERVER_URL when building for friends:  SD_SERVER_URL=wss://... tools/sync_unity.sh
if [ ! -f "$STREAMING/client-config.json" ] || [ -n "${SD_SERVER_URL:-}" ]; then
  echo "{ \"serverUrl\": \"${SD_SERVER_URL:-ws://localhost:5226/ws}\" }" > "$STREAMING/client-config.json"
  echo "  wrote client-config.json (${SD_SERVER_URL:-ws://localhost:5226/ws})"
fi

echo "done:"
echo "  $(ls "$PLUGINS" | grep -c '\.dll$') dll(s) in Assets/Plugins"
echo "  $(find "$STREAMING/game-data" -name '*.json' | wc -l | tr -d ' ') json + $(find "$STREAMING/game-data" -name '*.bin' | wc -l | tr -d ' ') los-mask files"
echo "  $(find "$STREAMING/tokens" -name '*.png' 2>/dev/null | wc -l | tr -d ' ') token images"
echo "  $(find "$STREAMING/textures" -name '*.png' 2>/dev/null | wc -l | tr -d ' ') board textures"
