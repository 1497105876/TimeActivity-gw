import sqlite3
import sys
sys.stdout.reconfigure(encoding='utf-8')

conn = sqlite3.connect(r'C:\Users\highness\AppData\Local\Finkit\ManicTime\ManicTimeReports.db')
c = conn.cursor()

# 看看那些看起来像安装包的进程，在 ManicTime 里实际使用了多久
# Ar_ApplicationByDay: ReportId, Hour, CommonId, TotalSeconds
c.execute("""
    SELECT cg.Key, cg.Name, SUM(abd.TotalSeconds) as total_sec, COUNT(DISTINCT abd.Hour) as days
    FROM Ar_CommonGroup cg
    JOIN Ar_ApplicationByDay abd ON cg.CommonId = abd.CommonId
    WHERE cg.ReportGroupType = 1
    GROUP BY cg.CommonId
    HAVING total_sec < 300
    ORDER BY total_sec DESC
    LIMIT 50
""")
rows = c.fetchall()
print("=== 使用时长 < 5分钟的应用（可能是安装包/一次性工具）===")
for key, name, sec, days in rows:
    proc = key.split(';')[0] if key and ';' in key else (key or '')
    print(f"  {proc:50s}  {name:40s}  {sec:>5d}s  {days}天")

print("\n\n")

# 看看使用时长 > 5分钟但 < 1小时的
c.execute("""
    SELECT cg.Key, cg.Name, SUM(abd.TotalSeconds) as total_sec, COUNT(DISTINCT abd.Hour) as days
    FROM Ar_CommonGroup cg
    JOIN Ar_ApplicationByDay abd ON cg.CommonId = abd.CommonId
    WHERE cg.ReportGroupType = 1
    GROUP BY cg.CommonId
    HAVING total_sec >= 300 AND total_sec < 3600
    ORDER BY total_sec DESC
    LIMIT 50
""")
rows = c.fetchall()
print("=== 使用时长 5分钟~1小时的应用（边界情况）===")
for key, name, sec, days in rows:
    proc = key.split(';')[0] if key and ';' in key else (key or '')
    print(f"  {proc:50s}  {name:40s}  {sec:>5d}s  {days}天")

print("\n\n")

# 看看使用时长 > 1小时的应用（真正常用的）
c.execute("""
    SELECT cg.Key, cg.Name, SUM(abd.TotalSeconds) as total_sec, COUNT(DISTINCT abd.Hour) as days
    FROM Ar_CommonGroup cg
    JOIN Ar_ApplicationByDay abd ON cg.CommonId = abd.CommonId
    WHERE cg.ReportGroupType = 1
    GROUP BY cg.CommonId
    HAVING total_sec >= 3600
    ORDER BY total_sec DESC
""")
rows = c.fetchall()
print(f"=== 使用时长 > 1小时的应用（真正常用），共 {len(rows)} 个 ===")
for key, name, sec, days in rows:
    proc = key.split(';')[0] if key and ';' in key else (key or '')
    hours = sec / 3600
    print(f"  {proc:50s}  {name:40s}  {hours:>6.1f}h  {days}天")

conn.close()
