#!/bin/bash
# Starts a fresh one-validator devnet inside WSL (Ubuntu): wipes /root/minterwallet-devnet, writes a new genesis and runs
# upstream minter-go-node v3.3.0 with --testnet (chain ID 2) in the background. API v2: http://<WSL IP>:38843/v2/
# (from Windows use the address printed at the end; 127.0.0.1 is not forwarded here). Stop: pkill -f minter-linux
# The chain starts at height 120 with v330; P2P (36656) and RPC (36657) listen on the WSL IP too, and a state sync
# snapshot is made every 100 blocks (a gate can start from one).
# Build first on Windows: tools/devnet/build.ps1
set -e
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
HOME_DIR=/root/minterwallet-devnet
pkill -f minter-linux || true
for i in $(seq 1 10); do pgrep -f minter-linux >/dev/null || break; sleep 1; done
rm -rf "$HOME_DIR" /root/devnet-bin
mkdir -p /root/devnet-bin
cp "$REPO/tools/devnet/bin/minter-linux" "$REPO/tools/devnet/bin/devnet-linux" /root/devnet-bin/
chmod +x /root/devnet-bin/*
/root/devnet-bin/devnet-linux -home "$HOME_DIR"
nohup /root/devnet-bin/minter-linux node --home-dir="$HOME_DIR" --testnet > "$HOME_DIR/node.log" 2>&1 &
for i in $(seq 1 30); do
  if curl -s http://127.0.0.1:38843/v2/status | grep -q latest_block_height; then break; fi
  sleep 1
done
curl -s http://127.0.0.1:38843/v2/status | head -c 200; echo
echo "API: http://$(hostname -I | cut -d' ' -f1):38843"
