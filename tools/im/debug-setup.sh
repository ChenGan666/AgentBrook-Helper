#!/usr/bin/env bash
# IM 调试环境搭建（S8 调试）：打包通道插件 + 生成试点工作区 im/config.json + 输出启动命令
# 用法: debug-setup.sh <pilot-workspace> <mock-bridge-port> [bridge-token]
set -euo pipefail

WS="${1:?用法: debug-setup.sh <pilot-workspace> <mock-bridge-port> [bridge-token]}"
PORT="${2:?需要 mock bridge 端口}"
TOKEN="${3:-debug-bridge-token}"
SRC="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../src" && pwd)"

mkdir -p "$WS/im/plugins/im-telegram" "$WS/im/plugins/im-bridge"

for name in Im.Telegram Im.Bridge; do
  proj="AgentBrook.Plugin.$name"
  dotnet build "$SRC/$proj/$proj.csproj" -c Debug >/dev/null 2>&1
  dll="$SRC/$proj/bin/Debug/net10.0/$proj.dll"
  [ -f "$dll" ] || { echo "DLL 缺失: $dll"; exit 1; }
  dir="$WS/im/plugins/im-${name#Im.}"
  dir=$(echo "$dir" | tr "A-Z" "a-z")
  mkdir -p "$dir"
  cp "$dll" "$dir/"
  sha=$(shasum -a 256 "$dll" | awk '{print $1}')
  capability=$([ "$name" = "Im.Telegram" ] && echo telegram-channel || echo bridge-channel)
  cat > "$dir/manifest.json" <<MEOF
{
  "id": "im-${name#im.}",
  "version": "1.0.0",
  "entry": "$proj.dll",
  "minContractVersion": "0.1.0-preview",
  "capabilityId": "$capability",
  "entrySha256": "$sha"
}
MEOF
  echo "[setup] 打包 $dir"
done

mkdir -p "$WS/im"
cat > "$WS/im/config.json" <<CEOF
{
  "ImEnabled": true,
  "StorageMode": "versioned",
  "channels": [
    {
      "Type": "bridge",
      "ChannelId": "bridge-wx",
      "InstanceId": "inst-1",
      "Enabled": true,
      "DisplayName": "微信(调试)",
      "SecretRef": "im-bridge+inst-1/wx-token",
      "BridgeUrl": "http://127.0.0.1:$PORT"
    }
  ],
  "DebugSecrets": {
    "im-bridge+inst-1/wx-token": "$TOKEN"
  }
}
CEOF
echo "[setup] config.json 已写入（bridge 通道 → 127.0.0.1:${PORT}）"

cat <<HEOF

[setup] 完成。调试启动步骤：
  1) 启动 mock bridge:
     "$SRC/AgentBrook.Im.TestHost/bin/Debug/net10.0/AgentBrook.Im.TestHost" \
       mock-bridge "$WS/im/mockbridge/bridge.db" $PORT "$TOKEN" --auto-drain &
  2) 启动 Helper（调试）:
     AGENTBROOK_Agent__WorkspaceRoot="$WS" dotnet run --project "$SRC/AgentBrook.Helper" -c Debug
  3) 推送测试入站消息（模拟桥推入）:
     curl -X POST http://127.0.0.1:47852/im/bridge/bridge-wx/inbound \
       -H "X-Im-Bridge-Token: $TOKEN" -H "Content-Type: application/json" \
       -d '{"accountId":"wxid_debug","chatId":"wxid_peer","chatType":"single","senderId":"wxid_peer","senderName":"调试者","text":"调试消息","messageId":"dbg-1"}'
HEOF
