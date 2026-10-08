#!/bin/zsh
# 构建 macOS .app 包（bundle 身份、麦克风/语音识别权限归因必需）。
# 默认一次产出三个包（各自独立目录）：
#   macos/AgentBrook.Helper-arm64.app   → Apple Silicon（M 系列芯片）专用
#   macos/AgentBrook.Helper-x64.app     → Intel 芯片专用
#   macos/AgentBrook.Helper.app         → universal 通用包（两种芯片都能跑）
# 均为 self-contained（自带 .NET 运行时，目标机零依赖）。
# MODE=arm64 | x64 | universal 可只构建其中一种。
set -e
cd "$(dirname "$0")"
MODE=${MODE:-all}

publish() {
  local rid=$1 dir=$2
  echo "==> dotnet publish（$rid，self-contained）"
  dotnet publish -c Release -r $rid --self-contained true -o $dir || exit 1
}

write_plist() {
  local app=$1
  cat > "$app/Contents/Info.plist" << 'PLIST'
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
  <key>CFBundleShortVersionString</key><string>1.0</string>
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
  write_plist "$app"
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
  write_plist "$app"
  codesign --force --deep --sign - "$app"
}

if [ "$MODE" = "all" ] || [ "$MODE" = "arm64" ]; then
  publish osx-arm64 macos/publish/arm64
  build_one macos/AgentBrook.Helper-arm64.app arm64 macos/publish/arm64
  echo "✔ macos/AgentBrook.Helper-arm64.app（Apple Silicon）"
fi

if [ "$MODE" = "all" ] || [ "$MODE" = "x64" ]; then
  publish osx-x64 macos/publish/x64
  build_one macos/AgentBrook.Helper-x64.app x64 macos/publish/x64
  echo "✔ macos/AgentBrook.Helper-x64.app（Intel）"
fi

if [ "$MODE" = "all" ] || [ "$MODE" = "universal" ]; then
  [ -d macos/publish/arm64 ] || publish osx-arm64 macos/publish/arm64
  [ -d macos/publish/x64 ] || publish osx-x64 macos/publish/x64
  build_universal macos/AgentBrook.Helper.app macos/publish/arm64 macos/publish/x64
  echo "✔ macos/AgentBrook.Helper.app（universal，两种芯片通用）"
fi

# ── 发布前安全清理：默认发布不携带任何大模型密钥 ──
# appsettings.json 的 ApiKey 替换为占位符（目标机在设置 → 模型设置中自行填写）；
# 清理会改动包内文件，之后对每个包重新签名。
# 兜底守卫：包内任何文件携带 sk- 形态密钥 → 构建中止。
for app in macos/AgentBrook.Helper-arm64.app macos/AgentBrook.Helper-x64.app macos/AgentBrook.Helper.app
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

if grep -rlE 'sk-[A-Za-z0-9]{16,}' macos/*.app/Contents/MacOS >/dev/null 2>&1; then
  echo "✖ 发布包中仍检测到疑似 API 密钥，构建中止（请检查 appsettings.json / models.json / mcp-user.json）"
  exit 1
fi

rm -rf macos/publish

echo ""
echo "分发指引："
echo "  Intel Mac        → 拷贝 AgentBrook.Helper-x64.app"
echo "  Apple Silicon Mac → 拷贝 AgentBrook.Helper-arm64.app"
echo "  不确定芯片型号    → 拷贝 AgentBrook.Helper.app（universal）"
echo "  目标机首次运行若显示禁止图标/无法验证开发者（拷贝带隔离属性，临时签名无法在线验证）："
echo "    xattr -dr com.apple.quarantine AgentBrook.Helper-xxx.app"
echo "    chmod -R u+x AgentBrook.Helper-xxx.app/Contents/MacOS"
echo "  建议用「压缩成 zip → 拷贝 → 解压」的方式传输，避免丢权限。"
