import sqlite3
conn = sqlite3.connect(r'Z:\final\code\TimeActivity\bin\Debug\net8.0-windows\timeactivity.db')
c = conn.cursor()
c.execute("SELECT Key,Value FROM Settings WHERE Key LIKE '%creenshot%' OR Key LIKE '%witch%'")
print(c.fetchall())
conn.close()
