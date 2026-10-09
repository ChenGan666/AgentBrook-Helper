#!/bin/zsh
# 构建 macOS .app 包（bundle 身份、麦克风/语音识别权限归因必需）。
# 默认一次产出三个包：
#   <仓库>/src/publish/osx-arm64/AgentBrook.Helper-arm64.app
#   <仓库>/src/publish/osx-x64/AgentBrook.Helper-x64.app
#   <仓库>/src/publish/osx-universal/AgentBrook.Helper.app
# 同时会生成对应的 zip 分发包，并复制一份到 ../AgentBrook.Service/updates 目录，供自动更新服务使用。
# 均为 self-contained（自带 .NET 运行时，目标机零依赖）。
# MODE=arm64 | x64 | universal 可只构建其中一种。
# PUBLISH_BASE 环境变量可覆盖发布根目录。
set -e
cd "$(dirname "$0")"
MODE=${MODE:-all}

# 版本号管理：自动递增 patch，也可通过 VERSION 环境变量覆盖
VERSION_FILE="$(pwd)/version.txt"
if [ -f "$VERSION_FILE" ]; then
  # 兼容 Windows 生成的 UTF-8 BOM：先去掉 BOM
  BASE_VERSION=$(perl -pe 's/^\xEF\xBB\xBF//' "$VERSION_FILE" | head -n 1 | tr -d '[:space:]')
else
  BASE_VERSION="1.0.0"
fi
IFS='.' read -r MAJOR MINOR PATCH <<< "$BASE_VERSION"
NEW_PATCH=$((PATCH + 1))
VERSION="${VERSION:-$MAJOR.$MINOR.$NEW_PATCH}"
printf '%s\n' "$VERSION" > "$VERSION_FILE"
echo "==> 发布版本：$VERSION"

# 发布根目录，可通过环境变量覆盖
PUBLISH_BASE="${PUBLISH_BASE:-$(cd .. && pwd)/publish}"

publish() {
  local rid=$1 dir=$2
  echo "==> dotnet publish（$rid，self-contained，v$VERSION）"
  dotnet publish -c Release -r $rid --self-contained true -p:Version=$VERSION -o "$dir" || exit 1
  # 清除输出目录 appsettings.json 中的 API Key 等敏感信息
  local out_settings="$dir/appsettings.json"
  if [ -f "$out_settings" ]; then
    sed -i '' -E 's/"ApiKey"[[:space:]]*:[[:space:]]*"[^"]*"/"ApiKey": ""/g' "$out_settings"
    sed -i '' -E 's/"GITHUB_PERSONAL_ACCESS_TOKEN"[[:space:]]*:[[:space:]]*"[^"]*"/"GITHUB_PERSONAL_ACCESS_TOKEN": ""/g' "$out_settings"
    echo "  -> 已清除 appsettings.json 中的敏感信息"
  fi
}

write_plist() {
  local app=$1 version=$2
  cat > "$app/Contents/Info.plist" << PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Brook 助手</string>
  <key>CFBundleDisplayName</key><string>Brook 助手</string>
  <key>CFBundleIdentifier</key><string>com.agentbrook.helper</string>
  <key>CFBundleExecutable</key><string>AgentBrook.Helper</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>$version</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSSpeechRecognitionUsageDescription</key>
  <string>用于在本机识别你的语音指令</string>
  <key>NSMicrophoneUsageDescription</key>
  <string>用于录制你的语音指令并本机识别</string>
</dict>
</plist>
PLIST
}

# 从单一发布输出组装单架构 app
build_one() {
  local app=$1 arch=$2 src=$3
  rm -rf "$app/Contents"
  mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
  cp Assets/app-icon.icns "$app/Contents/Resources/AppIcon.icns"
  find "$src" -type f | while read -r f
  do
    rel=${f#$src/}
    case "$rel" in
      *.pdb) continue ;;
    esac
    mkdir -p "$(dirname "$app/Contents/MacOS/$rel")"
    cp "$f" "$app/Contents/MacOS/$rel"
  done
  write_plist "$app" "$VERSION"
  codesign --force --deep --sign - "$app"
}

# 组装 universal app：arm64 为基础，Mach-O 与 x64 侧 lipo 合并（同架构文件直接取用）
build_universal() {
  local app=$1 arm=$2 x64=$3
  rm -rf "$app/Contents"
  mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
  cp Assets/app-icon.icns "$app/Contents/Resources/AppIcon.icns"
  find "$arm" -type f | while read -r f
  do
    rel=${f#$arm/}
    case "$rel" in
      *.pdb) continue ;;
    esac
    out="$app/Contents/MacOS/$rel"
    mkdir -p "$(dirname "$out")"
    x="$x64/$rel"
    if file -b "$f" | grep -q "Mach-O" && [ -e "$x" ]; then
      lipo -create "$f" "$x" -output "$out" 2>/dev/null || cp "$f" "$out"
    else
      cp "$f" "$out"
    fi
  done
  # 仅存在于 x64 输出的文件（罕见）原样补入
  find "$x64" -type f | while read -r f
  do
    rel=${f#$x64/}
    case "$rel" in
      *.pdb) continue ;;
    esac
    [ -e "$app/Contents/MacOS/$rel" ] || cp "$f" "$app/Contents/MacOS/$rel"
  done
  write_plist "$app" "$VERSION"
  codesign --force --deep --sign - "$app"
}

package_app() {
  local app=$1 rid=$2
  local zip_name="AgentBrook.Helper-$VERSION-$rid.zip"
  local zip_path="$PUBLISH_BASE/$zip_name"
  local update_dir="$(pwd)/../AgentBrook.Service/updates/$rid"
  mkdir -p "$(dirname "$zip_path")"
  rm -f "$zip_path"
  (cd "$(dirname "$app")" && zip -ry "$zip_path" "$(basename "$app")" >/dev/null)
  mkdir -p "$update_dir"
  cp "$zip_path" "$update_dir/AgentBrook.Helper-$VERSION.zip"
  cp "$(pwd)/release-notes.txt" "$update_dir/release-notes.txt"
  echo "  -> $zip_path"
  echo "  -> $update_dir/AgentBrook.Helper-$VERSION.zip"
}

if [ "$MODE" = "all" ] || [ "$MODE" = "arm64" ]; then
  publish osx-arm64 "$PUBLISH_BASE/osx-arm64/publish"
  build_one "$PUBLISH_BASE/osx-arm64/AgentBrook.Helper-arm64.app" arm64 "$PUBLISH_BASE/osx-arm64/publish"
  package_app "$PUBLISH_BASE/osx-arm64/AgentBrook.Helper-arm64.app" osx-arm64
  echo "✔ $PUBLISH_BASE/osx-arm64/AgentBrook.Helper-arm64.app（Apple Silicon）"
fi

if [ "$MODE" = "all" ] || [ "$MODE" = "x64" ]; then
  publish osx-x64 "$PUBLISH_BASE/osx-x64/publish"
  build_one "$PUBLISH_BASE/osx-x64/AgentBrook.Helper-x64.app" x64 "$PUBLISH_BASE/osx-x64/publish"
  package_app "$PUBLISH_BASE/osx-x64/AgentBrook.Helper-x64.app" osx-x64
  echo "✔ $PUBLISH_BASE/osx-x64/AgentBrook.Helper-x64.app（Intel）"
fi

if [ "$MODE" = "all" ] || [ "$MODE" = "universal" ]; then
  [ -d "$PUBLISH_BASE/osx-arm64/publish" ] || publish osx-arm64 "$PUBLISH_BASE/osx-arm64/publish"
  [ -d "$PUBLISH_BASE/osx-x64/publish" ] || publish osx-x64 "$PUBLISH_BASE/osx-x64/publish"
  build_universal "$PUBLISH_BASE/osx-universal/AgentBrook.Helper.app" "$PUBLISH_BASE/osx-arm64/publish" "$PUBLISH_BASE/osx-x64/publish"
  package_app "$PUBLISH_BASE/osx-universal/AgentBrook.Helper.app" osx-universal
  echo "✔ $PUBLISH_BASE/osx-universal/AgentBrook.Helper.app（universal，两种芯片通用）"
fi

# ── 发布前安全清理：默认发布不携带任何大模型密钥 ──
# appsettings.json 的 ApiKey 替换为占位符（目标机在设置 → 模型设置中自行填写）；
# 清理会改动包内文件，之后对每个包重新签名。
# 兜底守卫：包内任何文件携带 sk- 形态密钥 → 构建中止。
for app in "$PUBLISH_BASE"/osx-*/AgentBrook.Helper-*.app "$PUBLISH_BASE"/osx-universal/AgentBrook.Helper.app
do
  [ -d "$app" ] || continue
  python3 - "$app/Contents/MacOS/appsettings.json" << 'PY'
import json, sys
p = sys.argv[1]
d = json.load(open(p, encoding="utf-8"))
llm = d.get("LLM")
if isinstance(llm, dict) and llm.get("ApiKey"):
    llm["ApiKey"] = "sk-please-fill-in-your-api-key"
    json.dump(d, open(p, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
    print(f"  已清除 {p} 中的 ApiKey（目标机在模型设置中填写）")
PY
  codesign --force --deep --sign - "$app"
done

if grep -rlE 'sk-[A-Za-z0-9]{16,}' "$PUBLISH_BASE"/*/AgentBrook.Helper-*.app/Contents/MacOS >/dev/null 2>&1; then
  echo "✖ 发布包中仍检测到疑似 API 密钥，构建中止（请检查 appsettings.json / models.json / mcp-user.json）"
  exit 1
fi

# 清理临时 publish 目录
rm -rf "$PUBLISH_BASE"/*/publish

echo ""
echo "分发指引："
echo "  Intel Mac         → 拷贝 $PUBLISH_BASE/osx-x64/AgentBrook.Helper-x64.zip"
echo "  Apple Silicon Mac → 拷贝 $PUBLISH_BASE/osx-arm64/AgentBrook.Helper-arm64.zip"
echo "  不确定芯片型号    → 拷贝 $PUBLISH_BASE/osx-universal/AgentBrook.Helper-universal.zip"
echo "  目标机首次运行若显示禁止图标/无法验证开发者（拷贝带隔离属性，临时签名无法在线验证）："
echo "    xattr -dr com.apple.quarantine AgentBrook.Helper.app"
echo "    chmod -R u+x AgentBrook.Helper.app/Contents/MacOS"
echo "  建议用「压缩成 zip → 拷贝 → 解压」的方式传输，避免丢权限。"
