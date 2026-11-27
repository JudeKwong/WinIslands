from pathlib import Path
p = Path(r'E:\MyFiles\Program\WinIsland\website\index.html')
s = p.read_text(encoding='utf-8')
s = s.replace('assets/promos/winislands-hero-v4-1920x1080.jpg', 'assets/promos/winislands-hero-v5-1920x1080.jpg')
s = s.replace('assets/promos/winislands-components-v6-1920x1080.jpg', 'assets/promos/winislands-components-v7-1920x1080.jpg')
s = s.replace('assets/promos/winislands-lyrics-v3-1920x1080.jpg', 'assets/promos/winislands-lyrics-v4-1920x1080.jpg')
s = s.replace('assets/promos/winislands-api-v3-1920x1080.jpg', 'assets/promos/winislands-api-v4-1920x1080.jpg')
s = s.replace('assets/promos/winislands-share-v3-1200x1200.jpg', 'assets/promos/winislands-share-v4-1200x1200.jpg')
old = '''    <div class="section-head">
      <h2>官网宣传视觉</h2>
      <p>Apple 风格的系列主视觉，可直接用于官网与社交分享。</p>
    </div>
'''
if old in s:
    s = s.replace(old, '')
    print('removed section-head')
else:
    print('section-head not found')
p.write_text(s, encoding='utf-8')
print('updated')
