from pathlib import Path
p = Path(r'E:\MyFiles\Program\WinIsland\website\index.html')
s = p.read_text(encoding='utf-8')
repls = {
    'assets/promos/winislands-hero-v5-1920x1080.jpg': 'assets/promos/winislands-hero-v6-1920x1080.jpg',
    'assets/promos/winislands-components-v7-1920x1080.jpg': 'assets/promos/winislands-components-v8-1920x1080.jpg',
    'assets/promos/winislands-lyrics-v4-1920x1080.jpg': 'assets/promos/winislands-lyrics-v5-1920x1080.jpg',
    'assets/promos/winislands-api-v4-1920x1080.jpg': 'assets/promos/winislands-api-v5-1920x1080.jpg',
    'assets/promos/winislands-share-v4-1200x1200.jpg': 'assets/promos/winislands-share-v5-1200x1200.jpg',
    'WinIslands 高级主视觉：深色玻璃质感灵动岛。': 'WinIslands 高级主视觉：深色玻璃质感灵动岛。',
    'WinIslands 组件视觉：多枚玻璃胶囊的组合。': 'WinIslands 组件视觉：多枚玻璃胶囊的组合。',
}
for old, new in repls.items():
    s = s.replace(old, new)
p.write_text(s, encoding='utf-8')
print('updated to clean Apple-style images')
