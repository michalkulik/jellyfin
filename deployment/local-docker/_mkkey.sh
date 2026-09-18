#!/bin/bash
set -e
BASE="http://localhost:8097"
KEY=$(docker exec jellyfin python3 - <<'EOF'
import sqlite3, datetime, uuid
db = "/config/data/jellyfin.db"
con = sqlite3.connect(db)
cur = con.cursor()
token = uuid.uuid4().hex
now = datetime.datetime(2026, 9, 18, 12, 0, 0).strftime("%Y-%m-%d %H:%M:%S")
cur.execute("INSERT INTO ApiKeys (DateCreated, DateLastActivity, Name, AccessToken) VALUES (?,?,?,?)",
            (now, now, "tmpdl", token))
con.commit()
print(token)
con.close()
EOF
)
echo "key=$KEY"
echo "$KEY" > /tmp/dlkey
