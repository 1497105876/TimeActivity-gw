import sqlite3
conn = sqlite3.connect(r'Z:\final\code\TimeActivity\bin\Debug\net8.0-windows\timeactivity.db')
c = conn.cursor()
c.execute("SELECT Id, ProcessName, TitleKeyword, CategoryId, IsCustom FROM Rules ORDER BY CategoryId, Id")
rows = c.fetchall()
for r in rows:
    print(f"  Id={r[0]}  proc={r[1]:30s}  title={str(r[2]) or '':20s}  catId={r[3]}  custom={r[4]}")
print(f"\n共 {len(rows)} 条规则")
conn.close()
