import sqlite3
import sys
sys.stdout.reconfigure(encoding='utf-8')

# === TimeActivity 数据库 ===
print("=" * 60)
print("TimeActivity 数据库")
print("=" * 60)
conn = sqlite3.connect(r'Z:\final\code\TimeActivity\bin\Debug\net8.0-windows\timeactivity.db')
c = conn.cursor()

# 进程名 + 窗口标题 + 分类 + 出现次数
c.execute("""
    SELECT ProcessName, WindowTitle, Category, COUNT(*) as cnt
    FROM Activities
    GROUP BY ProcessName, WindowTitle, Category
    ORDER BY cnt DESC
""")
rows = c.fetchall()
print(f"\n总记录数（去重后进程+标题组合）: {len(rows)}")

# 按进程名聚合
c.execute("""
    SELECT ProcessName, COUNT(*) as cnt
    FROM Activities
    GROUP BY ProcessName
    ORDER BY cnt DESC
""")
procs = c.fetchall()
print(f"唯一进程数: {len(procs)}")
print("\n--- 所有进程名（按出现次数排序）---")
for p, cnt in procs:
    print(f"  {p:40s} {cnt:>6d}次")

# 按分类聚合
c.execute("""
    SELECT Category, COUNT(*) as cnt, SUM(Duration) as total_sec
    FROM Activities
    WHERE IsIdle = 0
    GROUP BY Category
    ORDER BY total_sec DESC
""")
cats = c.fetchall()
print("\n--- 分类统计（非空闲）---")
for cat, cnt, sec in cats:
    print(f"  {cat:10s}  记录数={cnt:>6d}  总时长={sec:>8d}秒 ({sec/3600:.1f}h)")

conn.close()

# === ManicTime 数据库 ===
print("\n\n" + "=" * 60)
print("ManicTime 数据库")
print("=" * 60)
conn2 = sqlite3.connect(r'C:\Users\highness\AppData\Local\Finkit\ManicTime\ManicTimeReports.db')
c2 = conn2.cursor()

# 先看有哪些表
c2.execute("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name")
tables = c2.fetchall()
print(f"\n表列表: {[t[0] for t in tables]}")

# 找活动数据表
for t in tables:
    tname = t[0]
    c2.execute(f"PRAGMA table_info('{tname}')")
    cols = c2.fetchall()
    col_names = [col[1] for col in cols]
    c2.execute(f"SELECT COUNT(*) FROM '{tname}'")
    cnt = c2.fetchone()[0]
    if cnt > 0:
        print(f"\n表 {tname}: {cnt} 行, 列: {col_names}")
        # 打印前3行看看结构
        c2.execute(f"SELECT * FROM '{tname}' LIMIT 3")
        samples = c2.fetchall()
        for s in samples:
            print(f"  样本: {s}")

conn2.close()
