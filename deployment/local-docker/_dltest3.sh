#!/bin/bash
BASE="http://localhost:8097"
ITEM=5C6D9F0B-C322-C83C-EB81-A89BAF543AA9
KEY=$(cat /tmp/dlkey)
H="Authorization: MediaBrowser Token=\"$KEY\""
A="Accept: application/json; profile=\"CamelCase\""
OUT=/tmp/dlresult.txt
: > $OUT

curl -s -X POST -H "$H" -H "$A" -H "Content-Type: application/json" -d '{"maxBitrate":4000000,"maxHeight":720,"container":"mp4"}' "$BASE/Items/$ITEM/Download" > /tmp/created.json
cat /tmp/created.json >> $OUT
echo >> $OUT
JOB=$(python3 -c "import json;print(json.load(open('/tmp/created.json')).get('id',''))")
echo "job=$JOB" >> $OUT

for i in $(seq 1 80); do
  curl -s -H "$H" -H "$A" "$BASE/Items/$ITEM/Download/$JOB" > /tmp/stat.json
  ST=$(python3 -c "import json;print(json.load(open('/tmp/stat.json')).get('status'))" 2>/dev/null)
  echo "POLL[$i] $ST $(cat /tmp/stat.json)" >> $OUT
  case "$ST" in Ready|Failed|Cancelled) break;; esac
  sleep 3
done

curl -s -D /tmp/hdr.txt -o /tmp/e.mp4 --max-time 180 -H "$H" "$BASE/Items/$ITEM/Download/$JOB/File"
grep -iE 'HTTP/|content-length|content-disposition|accept-ranges|content-type' /tmp/hdr.txt >> $OUT
echo "FILE_SIZE: $(stat -c%s /tmp/e.mp4 2>/dev/null)" >> $OUT
ffprobe -v error -show_entries format=format_name,duration,bit_rate -show_entries stream=codec_name,width,height -of default=noprint_wrappers=1 /tmp/e.mp4 >> $OUT 2>&1
curl -s -D /tmp/hdr2.txt -o /dev/null -r 0-1023 -H "$H" "$BASE/Items/$ITEM/Download/$JOB/File"
grep -iE 'HTTP/|content-range|content-length' /tmp/hdr2.txt >> $OUT
curl -s -X POST -H "$H" -H "$A" -H "Content-Type: application/json" -d '{"original":true}' "$BASE/Items/$ITEM/Download" >> $OUT
echo >> $OUT
rm -f /tmp/e.mp4
echo DONE >> $OUT
