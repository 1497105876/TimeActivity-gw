import sqlite3
import sys
sys.stdout.reconfigure(encoding='utf-8')

conn = sqlite3.connect(r'C:\Users\highness\AppData\Local\Finkit\ManicTime\ManicTimeReports.db')
c = conn.cursor()

# Ar_CommonGroup 表：Key 格式 "proc.exe;appname"，Name 是显示名
c.execute("SELECT CommonId, Key, Name, Color FROM Ar_CommonGroup WHERE ReportGroupType=1 ORDER BY Name")
rows = c.fetchall()
print(f"Ar_CommonGroup 应用总数: {len(rows)}")
print()

# 提取进程名
procs = set()
for _, key, name, color in rows:
    if key and ';' in key:
        proc = key.split(';')[0].lower()
        procs.add(proc)
    elif key:
        procs.add(key.lower())

print(f"唯一进程名: {len(procs)}")
print()

# 按 Name 排序输出
print("=" * 80)
print("ManicTime 所有应用（CommonId | 进程名 | 显示名 | 颜色）")
print("=" * 80)
for cid, key, name, color in sorted(rows, key=lambda x: x[2].lower()):
    proc = ""
    if key and ';' in key:
        proc = key.split(';')[0]
    elif key:
        proc = key
    print(f"  {cid:>5d}  {proc:45s}  {name:40s}  #{color}")

conn.close()
