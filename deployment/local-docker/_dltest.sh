#!/bin/bash
K=6c46145d332c4175934f3d885db75372
BASE="http://localhost:8098"
ITEM=ED3F09DE-D973-8A43-E0BF-D6B55C73BAD2
HDR="Authorization: MediaBrowser Token=\"$K\""

echo "=== create temp user ==="
U=$(curl -s -X POST -H "$HDR" -H "Content-Type: application/json" -d '{"Name":"tmpdl"}' "$BASE/Users/New")
echo "raw: $U"
USERID=$(echo "$U" | python3 -c "import sys,json;print(json.load(sys.stdin)['Id'])")
echo "user=$USERID"

echo "=== set policy ==="
curl -s -o /dev/null -w 'policy: %{http_code}\n' -X POST -H "$HDR" -H "Content-Type: application/json" \
  -d '{"IsAdministrator":false,"EnableContentDownloading":true,"EnableAllFolders":true,"EnableMediaPlayback":true,"EnableLiveTvAccess":true}' \
  "$BASE/Users/$USERID/Policy"

echo "=== authenticate ==="
A=$(curl -s -X POST -H "Content-Type: application/json" \
  -d '{"Username":"tmpdl","Pw":""}' "$BASE/Users/AuthenticateByName")
TOKEN=$(echo "$A" | python3 -c "import sys,json;print(json.load(sys.stdin)['AccessToken'])")
echo "token=${TOKEN:0:8}..."

UH="Authorization: MediaBrowser Token=\"$TOKEN\""

echo
echo "=== 1) create conversion job (1.5 Mbps) ==="
RESP=$(curl -s -X POST -H "$UH" -H "Content-Type: application/json" \
  -d '{"maxBitrate":1500000,"maxHeight":720,"container":"mp4"}' \
  "$BASE/Items/$ITEM/Download")
echo "$RESP"
JOB=$(echo "$RESP" | python3 -c "import sys,json;print(json.load(sys.stdin).get('id',''))")
echo "job=$JOB"

if [ -n "$JOB" ]; then
  echo
  echo "=== 2) poll status ==="
  for i in $(seq 1 60); do
    S=$(curl -s -H "$UH" "$BASE/Items/$ITEM/Download/$JOB")
    ST=$(echo "$S" | python3 -c "import sys,json;print(json.load(sys.stdin).get('status'))")
    echo "  $S"
    if [ "$ST" = "Ready" ] || [ "$ST" = "Failed" ] || [ "$ST" = "Cancelled" ]; then break; fi
    sleep 3
  done

  echo
  echo "=== 3) download the converted file ==="
  curl -s -D - -o /tmp/dl.mp4 --max-time 300 -H "$UH" "$BASE/Items/$ITEM/Download/$JOB/File" | head -6
  ls -la /tmp/dl.mp4
  ffprobe -v error -show_entries format=format_name,duration,bit_rate -show_entries stream=codec_name,width,height -of default=noprint_wrappers=1 /tmp/dl.mp4 2>&1 | head -12

  echo
  echo "=== 4) range request ==="
  curl -s -D - -o /dev/null -r 0-1023 -H "$UH" "$BASE/Items/$ITEM/Download/$JOB/File" | head -5
  rm -f /tmp/dl.mp4

  echo
  echo "=== 5) original (no conversion) ==="
  ORESP=$(curl -s -X POST -H "$UH" -H "Content-Type: application/json" -d '{"original":true}' "$BASE/Items/$ITEM/Download")
  echo "$ORESP"
fi

echo
echo "=== cleanup ==="
curl -s -o /dev/null -w 'delete user: %{http_code}\n' -X DELETE -H "$HDR" "$BASE/Users/$USERID"

