#!/usr/bin/env bash
# 腾讯云 IP 分配服务 · 一次性初始化脚本
# 在服务器上生成 .env（口令从运行中的 easytier 容器提取）与 10 个随机邀请码。
# 密钥全程只在服务器本地生成，不经过任何上传通道。
set -euo pipefail
cd ~/easytier-ip-alloc

SECRET=$(sudo docker inspect easytier --format '{{join .Args " "}}' | sed -n 's/.*--network-secret \([^ ]*\).*/\1/p')
if [ -z "$SECRET" ]; then echo "错误：未能从 easytier 容器提取口令"; exit 1; fi

ADMIN=$(openssl rand -hex 16)
umask 077
printf 'NETWORK_NAME=qdy\nNETWORK_SECRET=%s\nADMIN_KEY=%s\n' "$SECRET" "$ADMIN" > .env

python3 - <<'PYEOF' > data/codes.json
import json, secrets
print(json.dumps([{"code": secrets.token_hex(8), "note": f"群友{i:02d}", "revoked": False}
                  for i in range(1, 11)], ensure_ascii=False, indent=2))
PYEOF

chmod 600 .env data/codes.json
echo "初始化完成：codes=$(python3 -c 'import json;print(len(json.load(open("data/codes.json"))))') 个，口令长度 ${#SECRET}"
