import sqlite3, os

db_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "timeactivity.db")
if not os.path.exists(db_path):
    print("数据库不存在")
    exit()

conn = sqlite3.connect(db_path)
c = conn.cursor()

# 查本周活动数据
print("=== 本周进程统计 ===")
rows = c.execute("""
    SELECT ProcessName, SUM(Duration) as Total, COUNT(*) as Cnt
    FROM Activities 
    WHERE IsIdle=0 AND date(StartTime) >= date('now','weekday 1','-7 days') AND date(StartTime) <= date('now','weekday 1','-1 days')
    GROUP BY ProcessName ORDER BY Total DESC
""").fetchall()
for r in rows:
    print(f"  {r[0]}: {r[1]}s ({r[2]}条)")

print("\n=== 本月进程统计 ===")
rows = c.execute("""
    SELECT ProcessName, SUM(Duration) as Total, COUNT(*) as Cnt
    FROM Activities 
    WHERE IsIdle=0 AND date(StartTime) >= date('now','start of month','-1 month') AND date(StartTime) <= date('now','start of month','-1 day')
    GROUP BY ProcessName ORDER BY Total DESC
""").fetchall()
for r in rows:
    print(f"  {r[0]}: {r[1]}s ({r[2]}条)")

print("\n=== 最近10条活动 ===")
rows = c.execute("SELECT ProcessName, StartTime, Duration, IsIdle FROM Activities ORDER BY Id DESC LIMIT 10").fetchall()
for r in rows:
    print(f"  {r[0]} | {r[1]} | {r[2]}s | idle={r[3]}")

conn.close()
