#!/bin/zsh
# IM 通道插件发布（S8/S9，对齐 tools/plugin-publish/build-privacy-plugin.sh 形态）。
# 独立于 Helper 主发布——发布产物上传服务端 updates/plugin/。
#
# 输出根（与主发布一致）：src/publish/updated/plugin/
#   index.json                                  # 合并式（upsert by id；保留 privacy-masking 等既有条目）
#   im-telegram/{version}/
#     im-telegram-{version}-{rid}.zip   x4 RID  # win-x64/win-arm64/osx-x64/osx-arm64
#     manifest.json / index.entry.json / release-notes.txt
#   im-bridge/{version}/（同构）
#
# index 条目带 "category":"im"（S8）：Helper 设置页据此显示在「IM 连接」板块。
# Usage:
#   bash tools/im/publish-im-plugins.sh
#   VERSION=1.0.0 bash tools/im/publish-im-plugins.sh
set -euo pipefail
cd "$(dirname "$0")/../.."

VERSION="${VERSION:-1.0.0}"
PUBLISH_BASE="${PUBLISH_BASE:-$(pwd)/src/publish}"
OUT_BASE="$PUBLISH_BASE/updated/plugin"
RIDS="win-x64 win-arm64 osx-x64 osx-arm64"
PLUGINS=("im-telegram:AgentBrook.Plugin.Im.Telegram:Telegram:💬" "im-bridge:AgentBrook.Plugin.Im.Bridge:Bridge:🔗" "im-weixin:AgentBrook.Plugin.Im.Weixin:WeChat:💚")

command -v dotnet >/dev/null || { echo "need dotnet SDK"; exit 1; }
command -v python3 >/dev/null || { echo "need python3"; exit 1; }

echo "==> IM 插件版本: $VERSION"
mkdir -p "$OUT_BASE"

ENTRIES_FILE="$OUT_BASE/.im-entries.json"
rm -f "$ENTRIES_FILE"

for spec in "${PLUGINS[@]}"; do
  id="${spec%%:*}"
  rest="${spec#*:}"
  proj_name="${rest%%:*}"
  rest2="${rest#*:}"
  display="${rest2%%:*}"
  icon="${rest2##*:}"
  proj="src/$proj_name/$proj_name.csproj"
  plugin_out="$OUT_BASE/$id/$VERSION"
  rm -rf "$plugin_out"
  mkdir -p "$plugin_out"

  SHA_CSV=""; SIZE_CSV=""; RID_CSV=""
  for rid in $RIDS; do
    echo "==> dotnet publish $id ($rid)..."
    out="dist/im-plugin-publish/$id/$rid"
    rm -rf "$out"
    dotnet publish "$proj" -c Release -f net10.0 -r "$rid" --self-contained false -o "$out" >/dev/null

    # 排除宿主共享契约程序集（回退默认 ALC，不随包发布）+ 实现层 DLL（插件只依赖契约）
    for shared in AgentBrook.Plugin.Abstractions AgentBrook.Runtime.Abstractions \
                  AgentBrook.Messaging.Abstractions AgentBrook.Plugin.Hosting.Abstractions \
                  AgentBrook.Runtime AgentBrook.Messaging AgentBrook.Plugin.Hosting \
                  AgentBrook.Agent.IM AgentBrook.Agent.Core AgentBrook.Helper; do
      rm -f "$out/$shared.dll" "$out/$shared.pdb"
    done
    rm -f "$out/AgentBrook.Helper.dll" "$out/AgentBrook.Im.TestFixtures.dll" \
          "$out/AgentBrook.Im.TestHost.dll" "$out/AgentBrook.Runtime.Tests.dll" \
          "$out/AgentBrook.Messaging.Tests.dll" "$out/AgentBrook.Plugin.Hosting.Tests.dll" \
          "$out/AgentBrook.Plugin.Im.Telegram.Tests.dll" "$out/AgentBrook.Plugin.Im.Bridge.Tests.dll" \
          "$out/AgentBrook.Im.EndToEnd.Tests.dll" "$out/AgentBrook.Im.Integration.Tests.dll" \
          "$out/AgentBrook.Helper.Im.Tests.dll"

    # manifest.json（新宿主形态：entrySha256 按 RID DLL 计算；PluginManager.FindEntryDll 兼容）
    entry_dll="$proj_name.dll"
    dll_sha=$(shasum -a 256 "$out/$entry_dll" | awk '{print $1}')
    cat > "$out/manifest.json" <<MEOF
{
  "id": "$id",
  "version": "$VERSION",
  "entry": "$entry_dll",
  "minContractVersion": "0.1.0-preview",
  "capabilityId": "$([ "$id" = "im-telegram" ] && echo telegram-channel || ([ "$id" = "im-weixin" ] && echo weixin-channel || echo bridge-channel))",
  "entrySha256": "$dll_sha"
}
MEOF

    zip_name="$id-$VERSION-$rid.zip"
    (cd "$out" && zip -qr "$plugin_out/$zip_name" .)
    zip_sha=$(shasum -a 256 "$plugin_out/$zip_name" | awk '{print $1}')
    zip_size=$(stat -f%z "$plugin_out/$zip_name")
    SHA_CSV="$SHA_CSV$zip_sha,"
    SIZE_CSV="$SIZE_CSV$zip_size,"
    RID_CSV="$RID_CSV$rid,"
    echo "  ok $zip_name ($zip_size bytes)"
  done

  # manifest.json + index.entry.json（python3 生成；category=im + per-plugin icon）
  python3 - "$plugin_out" "$VERSION" "$id" "$display" "$SHA_CSV" "$SIZE_CSV" "$RID_CSV" "$icon" << 'PY'
import json, os, sys
plugin_out, version, pid, display, sha_csv, size_csv, rid_csv, icon = sys.argv[1:9]
shas = [x for x in sha_csv.split(",") if x]
sizes = [int(x) for x in size_csv.split(",") if x]
rids = [x for x in rid_csv.split(",") if x]

files = [{"path": f"{pid}-{version}-{r}.zip", "sha256": h} for r, h in zip(rids, shas)]
with open(os.path.join(plugin_out, "manifest.json"), "w", encoding="utf-8") as f:
    json.dump({"id": pid, "version": version, "files": files}, f, ensure_ascii=False, indent=2)

packages = {r: {"url": f"{pid}/{version}/{pid}-{version}-{r}.zip", "sha256": h, "size": s}
            for r, h, s in zip(rids, shas, sizes)}
entry = {"id": pid, "name": display, "latest": version, "minHost": "1.1.0", "icon": icon,
         "category": "im",
         "capabilities": ["im-channel"], "packages": packages}
with open(os.path.join(plugin_out, "index.entry.json"), "w", encoding="utf-8") as f:
    json.dump(entry, f, ensure_ascii=False, indent=2)
print("  manifest.json + index.entry.json generated")
PY

  cat > "$plugin_out/release-notes.txt" << NEOF
IM 通道插件 $id ${VERSION}（category=im，Helper 设置 → IM 连接板块安装）
NEOF
  echo "  release-notes.txt written"
done

# 顶层 index.json 合并（upsert by id；保留既有条目如 privacy-masking）
python3 - "$OUT_BASE" "$ENTRIES_FILE" << 'PY'
import json, os, sys
out_base, entries_file = sys.argv[1], None
entries_file = os.path.join(out_base, "index.json")
existing = {"schemaVersion": 1, "plugins": []}
if os.path.exists(entries_file):
    try:
        existing = json.load(open(entries_file, encoding="utf-8"))
    except Exception:
        pass

new_entries = []
for pid in ("im-telegram", "im-bridge", "im-weixin"):
    p = os.path.join(out_base, pid, "1.0.0", "index.entry.json")
    if not os.path.exists(p):
        p = os.path.join(out_base, pid, os.listdir(os.path.join(out_base, pid))[0], "index.entry.json")
    new_entries.append(json.load(open(p, encoding="utf-8")))

plugins = [dict(e, category=e.get("category", "plugin")) for e in existing.get("plugins", [])
           if e.get("id") not in {n["id"] for n in new_entries}]
plugins.extend(new_entries)
existing["schemaVersion"] = 1
existing["plugins"] = plugins
with open(entries_file, "w", encoding="utf-8") as f:
    json.dump(existing, f, ensure_ascii=False, indent=2)
print("index.json 合并完成：", [e["id"] for e in plugins])
PY

echo ""
echo "IM 插件发布包完成: $OUT_BASE"
echo "upload: 将 $OUT_BASE 各目录与 index.json 上传服务端 updates/plugin/（合并式 index.json 直接可用）"
echo "local test: 同步到 src/AgentBrook.Service/updates/plugin/ 并启动 AgentBrook.Service（端口 5300）"
