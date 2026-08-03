import sqlite3, json, sys
sys.stdout.reconfigure(encoding='utf-8')
db = r"Z:\final\code\TimeActivity\bin\Debug\net8.0-windows\timeactivity.db"
conn = sqlite3.connect(db)
c = conn.cursor()
rows = c.execute("SELECT Id, Date, substr(SummaryText,1,50), SummaryType, AutoType, CreatedAt FROM AISummaries ORDER BY Date DESC, CreatedAt DESC").fetchall()
for r in rows:
    print(f"Id={r[0]} | Date={r[1]} | Text={r[2]}... | Type={r[3]} | Auto={r[4]} | Created={r[5]}")
conn.close()
