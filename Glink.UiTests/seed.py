import sqlite3, pathlib, uuid, datetime, shutil, wave, sys

root = pathlib.Path(__file__).resolve().parents[1]
data = root / (sys.argv[1] if len(sys.argv) > 1 else '.review-data')
(data / 'media').mkdir(parents=True, exist_ok=True)
db = sqlite3.connect(str(data / 'moodia.db'))
db.executescript('''CREATE TABLE categories (Id TEXT PRIMARY KEY, Name TEXT NOT NULL, AccentHex TEXT NOT NULL, SortOrder INTEGER NOT NULL);
CREATE TABLE items (Id TEXT PRIMARY KEY, Type INTEGER NOT NULL, Content TEXT, FilePath TEXT, FileName TEXT, SizeBytes INTEGER NOT NULL, CategoryId TEXT, CreatedAt TEXT NOT NULL, Note TEXT, IsFavorite INTEGER NOT NULL DEFAULT 0, PosX REAL, PosY REAL, ZIndex INTEGER NOT NULL DEFAULT 0);''')
cats = [str(uuid.uuid4()), str(uuid.uuid4())]
db.executemany('INSERT INTO categories VALUES (?,?,?,?)', [(cats[0], '交互测试', '#E8956B', 0), (cats[1], '归档测试', '#8FBF7F', 1)])
texts = {
    1: '雨后的街角',
    2: '把灵感留在眼前，等它慢慢长成作品。',
    5: '给下一次旅行的备忘\n\n早晨去海边，带相机和一本空白笔记。\n帰り道に小さな喫茶店へ。 ☕',
    6: 'https://example.com/references/' + 'a-long-unbroken-reference-' * 12,
    7: '  保留空格与段落\n\n中文、日本語、English — 一个很长的观察：' + '让列表安静地承载内容，而不是让边框占据视线。' * 8 + '  ',
    8: '',  # A saved inline edit may leave an empty text object.
}
for i in range(9):
    kind = [2, 0, 0, 1, 3, 0, 0, 0, 0][i]
    rel, name, size = None, None, 0
    if kind:
        ext = {1:'.png',2:'.wav',3:'.mp4'}[kind]
        src = root / 'Glink.ImportTests' / 'Fixtures' / ('拖放回归' + ext)
        rel, name = 'media/sample' + ext, '测试素材' + ext
        shutil.copyfile(str(src), str(data / rel))
        if kind == 2:
            with wave.open(str(data / rel), 'wb') as audio:
                audio.setnchannels(1)
                audio.setsampwidth(2)
                audio.setframerate(22050)
                audio.writeframes(b'\0\0' * 22050 * 90)
        size = (data / rel).stat().st_size
    text = texts[i] if not kind else None
    date = (datetime.datetime.now() - datetime.timedelta(days=12 if i > 6 else 0, minutes=i * 23)).isoformat()
    db.execute('INSERT INTO items VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)', (str(uuid.uuid4()),kind,text,rel,name,size,cats[i%2],date,None,int(i in (2, 7)),3000+i*100,2000+i*100, i))
db.commit()
db.close()
print(data)
