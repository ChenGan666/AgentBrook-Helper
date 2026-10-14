#!/bin/zsh
# 构建 macOS .app 包（bundle 身份、麦克风/语音识别权限归因必需）。
# 默认一次产出三个包：
#   /Users/chengan/Dev/AI/AgentBrook3.0/src/publish/osx-arm64/AgentBrook.Helper-arm64.app
#   /Users/chengan/Dev/AI/AgentBrook3.0/src/publish/osx-x64/AgentBrook.Helper-x64.app
#   /Users/chengan/Dev/AI/AgentBrook3.0/src/publish/osx-universal/AgentBrook.Helper.app
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
  # 最终产物根：src/publish/updated/<平台>/（文件名仅含版本号，整文件夹可直接拷贝到更新服务器）
  local final_dir="$PUBLISH_BASE/updated/$rid"
  mkdir -p "$final_dir"
  local zip_path="$final_dir/AgentBrook.Helper-$VERSION.zip"
  rm -f "$zip_path"
  (cd "$(dirname "$app")" && zip -ry "$zip_path" "$(basename "$app")" >/dev/null)
  # 更新说明随平台文件夹分发（服务器按目录读取 release-notes.txt 作为该版本更新说明）
  cp "$(pwd)/release-notes.txt" "$final_dir/release-notes.txt"
  local update_dir="$(pwd)/../AgentBrook.Service/updates/$rid"
  mkdir -p "$update_dir"
  cp "$zip_path" "$update_dir/AgentBrook.Helper-$VERSION.zip"
  cp "$(pwd)/release-notes.txt" "$update_dir/release-notes.txt"
  echo "  -> $zip_path"
  echo "  -> $final_dir/release-notes.txt"
  echo "  -> $update_dir/AgentBrook.Helper-$VERSION.zip"
}

if [ "$MODE" = "all" ] || [ "$MODE" = "arm64" ]; then
  publish osx-arm64 "$PUBLISH_BASE/osx-arm64/publish"
  build_one "$PUBLISH_BASE/osx-arm64/AgentBrook.Helper-arm64.app" arm64 "$PUBLISH_BASE/osx-arm64/publish"
  echo "✔ $PUBLISH_BASE/osx-arm64/AgentBrook.Helper-arm64.app（Apple Silicon）"
fi

if [ "$MODE" = "all" ] || [ "$MODE" = "x64" ]; then
  publish osx-x64 "$PUBLISH_BASE/osx-x64/publish"
  build_one "$PUBLISH_BASE/osx-x64/AgentBrook.Helper-x64.app" x64 "$PUBLISH_BASE/osx-x64/publish"
  echo "✔ $PUBLISH_BASE/osx-x64/AgentBrook.Helper-x64.app（Intel）"
fi

if [ "$MODE" = "all" ] || [ "$MODE" = "universal" ]; then
  [ -d "$PUBLISH_BASE/osx-arm64/publish" ] || publish osx-arm64 "$PUBLISH_BASE/osx-arm64/publish"
  [ -d "$PUBLISH_BASE/osx-x64/publish" ] || publish osx-x64 "$PUBLISH_BASE/osx-x64/publish"
  build_universal "$PUBLISH_BASE/osx-universal/AgentBrook.Helper.app" "$PUBLISH_BASE/osx-arm64/publish" "$PUBLISH_BASE/osx-x64/publish"
  echo "✔ $PUBLISH_BASE/osx-universal/AgentBrook.Helper.app（universal，两种芯片通用）"
fi

# ── 发布前安全清理：默认发布不携带任何大模型密钥 ──
# appsettings.json 的 ApiKey 替换为占位符（目标机在设置 → 模型设置中自行填写）。
# 清理会改动包内文件，之后对每个包做最终签名并重新打包：
#   1. xattr -cr 清掉隔离/Finder 扩展属性（残留会让 Gatekeeper 判"已损坏或不完整"）；
#   2. --deep 对 Contents/MacOS 里的散装 Mach-O 覆盖不全，逐个补签
#      （实测漏签会让 codesign --deep 校验失败 → 带隔离属性时必弹"已损坏"）；
#   3. 签名自检不过直接中止构建；
#   4. 打包（zip）放在最终签名之后，保证 zip 内签名状态与最终产物一致。
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
  echo "==> 最终签名：$app"
  xattr -cr "$app" 2>/dev/null
  find "$app/Contents/MacOS" -type f -exec sh -c \
    'file -b "$1" | grep -q Mach-O && codesign --force --sign - "$1" >/dev/null 2>&1' _ {} \;
  codesign --force --deep --sign - "$app"
  if ! codesign --verify --deep --strict "$app"; then
    echo "✖ $app 签名校验失败，构建中止"
    exit 1
  fi
  case "$app" in
    *arm64*) package_app "$app" osx-arm64 ;;
    *x64*)   package_app "$app" osx-x64 ;;
    *)       package_app "$app" osx-universal ;;
  esac
  # 打包完成后即清理该 .app：防止遗留旧包被下次运行重打包成"版本号新、内容旧"的误导性产物
  rm -rf "$app"
done

if grep -rlE 'sk-[A-Za-z0-9]{16,}' "$PUBLISH_BASE"/*/AgentBrook.Helper-*.app/Contents/MacOS >/dev/null 2>&1; then
  echo "✖ 发布包中仍检测到疑似 API 密钥，构建中止（请检查 appsettings.json / models.json / mcp-user.json）"
  exit 1
fi

# 清理临时 publish 目录
rm -rf "$PUBLISH_BASE"/*/publish

echo ""
echo "分发指引（最终产物根：$PUBLISH_BASE/updated/）："
echo "  Apple Silicon Mac → $PUBLISH_BASE/updated/osx-arm64/AgentBrook.Helper-$VERSION.zip"
echo "  Intel Mac         → $PUBLISH_BASE/updated/osx-x64/AgentBrook.Helper-$VERSION.zip"
echo "  不确定芯片型号    → $PUBLISH_BASE/updated/osx-universal/AgentBrook.Helper-$VERSION.zip"
echo "  各平台文件夹内即最终版本文件（文件名仅含版本号），整个平台文件夹可直接拷贝到更新服务器。"
echo "  发布前请编辑 release-notes.txt——其内容将作为该版本的更新说明展示给用户。"
echo "  目标机首次运行若显示禁止图标/无法验证开发者（拷贝带隔离属性，临时签名无法在线验证）："
echo "    xattr -dr com.apple.quarantine AgentBrook.Helper.app"
echo "    chmod -R u+x AgentBrook.Helper.app/Contents/MacOS"
echo "  建议用「压缩成 zip → 拷贝 → 解压」的方式传输，避免丢权限。"
