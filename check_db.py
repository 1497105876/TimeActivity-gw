import sqlite3, sys
sys.stdout.reconfigure(encoding='utf-8')

db = r'Z:\final\code\TimeActivity\bin\Debug\net8.0-windows\timeactivity.db'
conn = sqlite3.connect(db)
c = conn.cursor()

print("=== 当前分类 ===")
c.execute("SELECT Id, Name, Color, SortOrder FROM Categories ORDER BY SortOrder")
for row in c.fetchall():
    print(f"  {row[0]:>2d}  {row[1]:<10s}  {row[2]}  {row[3]}")

print("\n=== 当前规则数 ===")
c.execute("SELECT COUNT(*) FROM Rules")
print(f"  总数: {c.fetchone()[0]}")
c.execute("SELECT COUNT(*) FROM Rules WHERE IsCustom=0")
print(f"  预置: {c.fetchone()[0]}")
c.execute("SELECT COUNT(*) FROM Rules WHERE IsCustom=1")
print(f"  自定义: {c.fetchone()[0]}")

print("\n=== 各分类规则数 ===")
c.execute("""
    SELECT r.CategoryId, cat.Name, COUNT(*) as cnt
    FROM Rules r
    LEFT JOIN Categories cat ON r.CategoryId = cat.Id
    GROUP BY r.CategoryId
    ORDER BY r.CategoryId
""")
for row in c.fetchall():
    print(f"  {row[0]:>2d}  {row[1] or 'NULL':<10s}  {row[2]}")

conn.close()
