from pathlib import Path
p = Path(r'E:\MyFiles\Program\WinIsland\website\index.html')
s = p.read_text(encoding='utf-8')
repls = {
    'assets/promos/winislands-hero-v6-1920x1080.jpg': 'assets/promos/winislands-hero-text-v1-1920x1080.jpg',
    'assets/promos/winislands-components-v8-1920x1080.jpg': 'assets/promos/winislands-components-text-v1-1920x1080.jpg',
    'assets/promos/winislands-lyrics-v5-1920x1080.jpg': 'assets/promos/winislands-lyrics-text-v1-1920x1080.jpg',
    'assets/promos/winislands-api-v5-1920x1080.jpg': 'assets/promos/winislands-api-text-v1-1920x1080.jpg',
    'assets/promos/winislands-share-v5-1200x1200.jpg': 'assets/promos/winislands-share-text-v1-1200x1200.jpg',
}
for old, new in repls.items():
    s = s.replace(old, new)
s = s.replace('<figure class="promo-item promo-wide">', '<figure class="promo-item">')
p.write_text(s, encoding='utf-8')
print('updated images and removed wide items')
