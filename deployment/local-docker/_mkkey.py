import sqlite3, datetime, uuid
db = "/srv/share/docker/jellyfin/data/jellyfin.db"
con = sqlite3.connect(db)
cur = con.cursor()
cur.execute("DELETE FROM ApiKeys WHERE Name='tmpdl'")
token = uuid.uuid4().hex
now = datetime.datetime(2026, 9, 18, 12, 0, 0).strftime("%Y-%m-%d %H:%M:%S")
cur.execute("INSERT INTO ApiKeys (DateCreated, DateLastActivity, Name, AccessToken) VALUES (?,?,?,?)",
            (now, now, "tmpdl", token))
con.commit()
con.close()
print(token)
