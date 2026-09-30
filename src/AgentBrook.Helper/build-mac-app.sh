#!/bin/zsh
# 构建 macOS .app 包（带正确的 bundle 身份，麦克风/语音识别权限归因必需）
set -e
cd "$(dirname "$0")"
dotnet build -c Release || exit 1
APP=macos/AgentBrook.Helper.app
rm -rf "$APP/Contents"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R bin/Release/net10.0/* "$APP/Contents/MacOS/"
cat > "$APP/Contents/Info.plist" << 'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Brook 助手</string>
  <key>CFBundleDisplayName</key><string>Brook 助手</string>
  <key>CFBundleIdentifier</key><string>com.agentbrook.helper</string>
  <key>CFBundleExecutable</key><string>AgentBrook.Helper</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>1.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSSpeechRecognitionUsageDescription</key>
  <string>用于在本机识别你的语音指令</string>
  <key>NSMicrophoneUsageDescription</key>
  <string>用于录制你的语音指令并本机识别</string>
</dict>
</plist>
PLIST
codesign --force --deep --sign - "$APP"
echo "✔ $APP 构建完成（open $APP 启动）"
