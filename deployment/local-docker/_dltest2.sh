#!/bin/bash
BASE="http://localhost:8097"
ITEM=5C6D9F0B-C322-C83C-EB81-A89BAF543AA9
KEY=$(cat /tmp/dlkey 2>/dev/null || echo "")
if [ -z "$KEY" ]; then echo "no key"; exit 1; fi
H="Authorization: MediaBrowser Token=\"$KEY\""
A="Accept: application/json; profile=\"CamelCase\""

echo "=== create job (4 Mbps) ==="
RESP=$(curl -s -X POST -H "$H" -H "$A" -H "Content-Type: application/json" -d '{"maxBitrate":4000000,"maxHeight":720,"container":"mp4"}' "$BASE/Items/$ITEM/Download")
echo "$RESP"
JOB=$(echo "$RESP" | python3 -c "import sys,json;print(json.load(sys.stdin).get('id',''))")

echo "=== poll ==="
for i in $(seq 1 80); do
  S=$(curl -s -H "$H" -H "$A" "$BASE/Items/$ITEM/Download/$JOB")
  ST=$(echo "$S" | python3 -c "import sys,json;print(json.load(sys.stdin).get('status'))" 2>/dev/null)
  echo "  [$i] $ST $S"
  case "$ST" in Ready|Failed|Cancelled) break;; esac
  sleep 3
done

echo
echo "=== download file ==="
curl -s -D - -o /tmp/e.mp4 --max-time 180 -H "$H" "$BASE/Items/$ITEM/Download/$JOB/File" | head -5
ls -la /tmp/e.mp4
ffprobe -v error -show_entries format=format_name,duration,bit_rate -show_entries stream=codec_name,width,height -of default=noprint_wrappers=1 /tmp/e.mp4 2>&1 | head -10
echo "=== range ==="
curl -s -D - -o /dev/null -r 0-1023 -H "$H" "$BASE/Items/$ITEM/Download/$JOB/File" | head -4
rm -f /tmp/e.mp4
