#!/usr/bin/env bash
# IM 独立图隔离构建/测试脚本（S0 基线 D-S0-1；implementation-plan §9.1）。
#
# 全部 restore/build/test 在"获准的隔离源码副本"执行：副本按相对布局同步 src/（剔除 bin/obj）
# 与根 nuget.config，保证 AgentBrook.Plugin.Abstractions 的只读 ProjectReference 同构；
# 运行中的 Helper 与开发树的旧工程 bin/obj 不被触碰。
#
# 用法:
#   tools/im/validate.sh restore          # 仅还原
#   tools/im/validate.sh build            # 还原+构建（Release）
#   tools/im/validate.sh test [FILTER]    # 构建+离线测试（默认 RequiresExternal!=true）
#   tools/im/validate.sh all [FILTER]     # restore+build+test
#
# 环境变量:
#   RUNID          证据运行号（默认 iter-<时间戳>）
#   AB_IM_COPYROOT 覆盖副本根（默认 $TMPDIR/AgentBrook.IM.Validation/copy）
#   AB_IM_EVROOT   覆盖证据根（默认 $TMPDIR/AgentBrook.IM.Validation/<RUNID>）
set -euo pipefail

REPO="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
RUNID="${RUNID:-iter-$(date +%Y%m%d-%H%M%S)}"
BASE="${TMPDIR%/}/AgentBrook.IM.Validation"
COPYROOT="${AB_IM_COPYROOT:-$BASE/copy}"
EVROOT="${AB_IM_EVROOT:-$BASE/$RUNID}"
MODE="${1:-build}"
FILTER="${2:-RequiresExternal!=true}"

mkdir -p "$COPYROOT" "$EVROOT/results"
echo "[im-validate] repo=$REPO"
echo "[im-validate] copy=$COPYROOT"
echo "[im-validate] evidence=$EVROOT runid=$RUNID mode=$MODE"

# ── 同步源码副本（只增不删的源码快照；--delete 使副本与源一致）──
rsync -a --delete \
  --exclude 'bin/' --exclude 'obj/' --exclude '.DS_Store' --exclude 'publish/' \
  "$REPO/src/" "$COPYROOT/src/"
cp "$REPO/nuget.config" "$COPYROOT/nuget.config"

cd "$COPYROOT"

SLEXN="src/AgentBrook.Im.slnx"
LOG () { echo "[im-validate] $*"; }

case "$MODE" in
  restore)
    dotnet restore "$SLEXN" 2>&1 | tee "$EVROOT/restore.log"
    ;;
  build)
    dotnet restore "$SLEXN" 2>&1 | tee "$EVROOT/restore.log"
    dotnet build "$SLEXN" -c Release --no-restore 2>&1 | tee "$EVROOT/build.log"
    ;;
  test)
    dotnet restore "$SLEXN" 2>&1 | tee "$EVROOT/restore.log"
    dotnet build "$SLEXN" -c Release --no-restore 2>&1 | tee "$EVROOT/build.log"
    LOG "dotnet test filter=\"$FILTER\""
    dotnet test "$SLEXN" -c Release --no-build \
      --filter "$FILTER" \
      --logger "trx;LogFilePrefix=im-offline" \
      --results-directory "$EVROOT/results" 2>&1 | tee "$EVROOT/test.log"
    ;;
  all)
    dotnet restore "$SLEXN" 2>&1 | tee "$EVROOT/restore.log"
    dotnet build "$SLEXN" -c Release --no-restore 2>&1 | tee "$EVROOT/build.log"
    dotnet test "$SLEXN" -c Release --no-build \
      --filter "$FILTER" \
      --logger "trx;LogFilePrefix=im-offline" \
      --results-directory "$EVROOT/results" 2>&1 | tee "$EVROOT/test.log"
    ;;
  *)
    echo "未知模式：$MODE（restore|build|test|all）" >&2
    exit 2
    ;;
esac

LOG "完成：证据在 $EVROOT"
