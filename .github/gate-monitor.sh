#!/usr/bin/env bash
# One look at the wallets' default MinterGate from outside: it must answer, be Live, keep up with the network (a public
# node's height) and have peers and heap room. Prints "ok" or what is wrong; the workflow alerts on a change only.
#   bash .github/gate-monitor.sh [GATE] [NODE]
gate=${1:-https://188.127.237.145:8851}
node=${2:-https://api-minter.mnst.club}
problems=()
if ! info=$(curl -sf -m 20 "$gate/gate/v1/info"); then
  echo "the gate does not answer at $gate"; exit 0
fi
state=$(jq -r .state <<<"$info"); verified=$(jq -r .verified_height <<<"$info"); peers=$(jq -r .peers <<<"$info")
heap=$(jq -r .memory.live_heap_bytes <<<"$info"); limit=$(jq -r .memory.limit_bytes <<<"$info")
[ "$state" = "Live" ] || problems+=("state $state ($(jq -r '.stop_reason // ""' <<<"$info"))")
[ "$peers" -gt 0 ] || problems+=("no peers")
[ "$limit" -gt 0 ] && [ $((heap * 100 / limit)) -ge 90 ] && problems+=("heap $((heap / 1048576)) of $((limit / 1048576)) MB")
if head=$(curl -sf -m 20 "$node/v2/status" | jq -r .latest_block_height) && [ -n "$head" ] && [ "$head" != "null" ]; then
  lag=$((head - verified))
  [ "$lag" -le 20 ] || problems+=("behind the network by $lag blocks (gate $verified, node $head)")
fi
if [ ${#problems[@]} -eq 0 ]; then echo "ok"; else (IFS='; '; echo "${problems[*]}"); fi
