#!/bin/zsh
# Privacy-masking plugin release packaging (doc/18 section 7).
# Independent from build-mac-app.sh / publish-helper.bat - NOT part of the Helper main release.
#
# Output root (matches main release): src/publish/updated/plugin/
#   index.json                                       # ready to upload as-is (single plugin)
#   privacy-masking/{version}/
#     privacy-masking-{version}-{rid}.zip   x4 RID  # win-x64/win-arm64/osx-x64/osx-arm64
#     manifest.json                                  # per-package SHA-256
#     index.entry.json                               # merge into index.json for multi-plugin deploys
#     release-notes.txt
#   privacy-masking/models/
#     models.json + cluener_baseline/{model.onnx,vocab.txt,labels.txt}
#     web.config                                     # IIS MIME map for .onnx
#
# Usage:
#   bash tools/plugin-publish/build-privacy-plugin.sh
#   VERSION=1.1.0 bash tools/plugin-publish/build-privacy-plugin.sh
set -euo pipefail
cd "$(dirname "$0")/../.."

VERSION="${VERSION:-1.0.12}"   # 单模型部署：下拉移除，识别模型行（安装/已安装+测试）；新增 model-ready 查询
PROJ="src/AgentBrook.Plugin.PrivacyMasking/AgentBrook.Plugin.PrivacyMasking.csproj"
PUBLISH_BASE="${PUBLISH_BASE:-$(pwd)/src/publish}"
PLUGIN_OUT="$PUBLISH_BASE/updated/plugin/privacy-masking/$VERSION"
MODEL_OUT="$PUBLISH_BASE/updated/plugin/privacy-masking/models"
MODEL_SRC="src/AgentBrook.Helper/workspace/privacy/models"
RIDS="win-x64 win-arm64 osx-x64 osx-arm64"

command -v dotnet >/dev/null || { echo "need dotnet SDK"; exit 1; }
command -v python3 >/dev/null || { echo "need python3"; exit 1; }

NOTES="tools/plugin-publish/release-notes.txt"
[ -f "$NOTES" ] || { echo "missing $NOTES (release notes are required)"; exit 1; }

echo "==> plugin version: $VERSION"
rm -rf "$PLUGIN_OUT" "$MODEL_OUT"
mkdir -p "$PLUGIN_OUT" "$MODEL_OUT"

# -- build zip per RID --
SHA_CSV=""
SIZE_CSV=""
RID_CSV=""
for rid in $RIDS; do
  echo "==> dotnet publish ($rid)..."
  OUT="dist/plugin-publish/$rid"
  rm -rf "$OUT"
  dotnet publish "$PROJ" -c Release -f net10.0 -r "$rid" --self-contained false -o "$OUT" >/dev/null

  # exclude host-shared contract assembly (falls back to default ALC, not shipped in plugin package)
  rm -f "$OUT/AgentBrook.Plugin.Abstractions.dll" "$OUT/AgentBrook.Plugin.Abstractions.pdb"

  zip_name="privacy-masking-$VERSION-$rid.zip"
  (cd "$OUT" && zip -qr "$PLUGIN_OUT/$zip_name" .)
  zip_sha=$(shasum -a 256 "$PLUGIN_OUT/$zip_name" | awk '{print $1}')
  zip_size=$(stat -f%z "$PLUGIN_OUT/$zip_name")
  SHA_CSV="$SHA_CSV$zip_sha,"
  SIZE_CSV="$SIZE_CSV$zip_size,"
  RID_CSV="$RID_CSV$rid,"
  echo "  ok $zip_name ($zip_size bytes)"
done

# -- manifest.json + index.entry.json + top-level index.json (python3) --
python3 - "$PLUGIN_OUT" "$MODEL_OUT" "$VERSION" "$SHA_CSV" "$SIZE_CSV" "$RID_CSV" <<'PY'
import json, os, sys
plugin_out, model_out, version, sha_csv, size_csv, rid_csv = sys.argv[1:7]
shas = [x for x in sha_csv.split(",") if x]
sizes = [int(x) for x in size_csv.split(",") if x]
rids = [x for x in rid_csv.split(",") if x]

files = [{"path": f"privacy-masking-{version}-{r}.zip", "sha256": h} for r, h in zip(rids, shas)]
with open(os.path.join(plugin_out, "manifest.json"), "w", encoding="utf-8") as f:
    json.dump({"id": "privacy-masking", "version": version, "files": files}, f, ensure_ascii=False, indent=2)

packages = {r: {"url": f"privacy-masking/{version}/privacy-masking-{version}-{r}.zip", "sha256": h, "size": s}
            for r, h, s in zip(rids, shas, sizes)}
entry = {"id": "privacy-masking", "name": "Privacy Masking", "latest": version, "minHost": "1.1.0",
         "capabilities": ["chat-client-decorator", "settings-contributor"],
         "modelsUrl": "privacy-masking/models/models.json", "packages": packages}
with open(os.path.join(plugin_out, "index.entry.json"), "w", encoding="utf-8") as f:
    json.dump(entry, f, ensure_ascii=False, indent=2)

# top-level index.json: ready to upload as-is (single plugin); merge entries for multi-plugin deploys
# (plugin_out = .../updated/plugin/privacy-masking/{version} → index.json belongs two levels up, at updated/plugin/)
with open(os.path.join(os.path.dirname(os.path.dirname(plugin_out.rstrip("/"))), "index.json"), "w", encoding="utf-8") as f:
    json.dump({"schemaVersion": 1, "plugins": [entry]}, f, ensure_ascii=False, indent=2)
print("  manifest.json + index.entry.json + index.json generated")
PY

cp "$NOTES" "$PLUGIN_OUT/release-notes.txt"

# -- recognition models (3-piece set + web.config IIS MIME map + models.json) --
mkdir -p "$MODEL_OUT/cluener_baseline"
for f in model.onnx vocab.txt labels.txt; do
  src="$MODEL_SRC/cluener_baseline/$f"
  [ -f "$src" ] || { echo "missing $src (install model in Helper settings first, or download from server)"; exit 1; }
  cp "$src" "$MODEL_OUT/cluener_baseline/$f"
done

model_sha=$(shasum -a 256 "$MODEL_OUT/cluener_baseline/model.onnx" | awk '{print $1}')
{
  echo '{'
  echo '  "cluener_baseline": { "files": ['
  echo "    { \"path\": \"model.onnx\", \"sha256\": \"$model_sha\" },"
  echo '    { "path": "vocab.txt" },'
  echo '    { "path": "labels.txt" }'
  echo '  ] }'
  echo '}'
} > "$MODEL_OUT/models.json"

# IIS: .onnx needs an explicit MIME map (web.config covers models/ and subdirs)
cat > "$MODEL_OUT/web.config" <<'WC'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <system.webServer>
    <staticContent>
      <mimeMap fileExtension=".onnx" mimeType="application/octet-stream" />
      <mimeMap fileExtension=".txt" mimeType="text/plain" />
    </staticContent>
  </system.webServer>
</configuration>
WC

# -- summary --
echo ""
echo "plugin release package done: $PLUGIN_OUT"
for rid in $RIDS; do
  echo "  $rid -> privacy-masking-$VERSION-$rid.zip"
done
echo "  manifest.json + index.entry.json + release-notes.txt + models/ (model.onnx $(($(stat -f%z "$MODEL_OUT/cluener_baseline/model.onnx") / 1024 / 1024)) MB)"
echo ""
echo "upload: copy $PLUGIN_OUT, models/ AND index.json to server updated/plugin/ (single plugin: index.json is ready to upload as-is;"
echo "        multi-plugin deployments: merge each index.entry.json into the top-level index.json instead)."
echo "local test: sync to src/AgentBrook.Service/updates/plugin/ and start AgentBrook.Service (port 5300)."
