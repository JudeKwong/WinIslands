from pathlib import Path
p = Path(r'E:\MyFiles\Program\WinIsland\website\css\style.css')
s = p.read_text(encoding='utf-8')
old = '''.promo-gallery {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 28px;
}
.promo-item { margin: 0; }
.promo-item img {
  display: block;
  width: 100%;
  height: auto;
  border-radius: 28px;
  box-shadow: 0 24px 80px rgba(0, 0, 0, 0.35);
}
.promo-wide { grid-column: 1 / -1; }
@media (max-width: 720px) {
  .promo-gallery { grid-template-columns: 1fr; gap: 20px; }
  .promo-item img { border-radius: 18px; }
}'''
new = '''.promo-gallery {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 18px;
  align-items: start;
}
.promo-item { margin: 0; }
.promo-item img {
  display: block;
  width: 100%;
  height: auto;
  aspect-ratio: 16 / 10;
  object-fit: cover;
  border-radius: 20px;
  box-shadow: 0 18px 48px rgba(0, 0, 0, 0.28);
}
.promo-wide { grid-column: auto; }
@media (max-width: 1100px) {
  .promo-gallery { grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 16px; }
}
@media (max-width: 720px) {
  .promo-gallery { grid-template-columns: 1fr; gap: 14px; }
  .promo-item img { border-radius: 16px; }
}'''
if old not in s:
    raise SystemExit('old CSS block not found')
s = s.replace(old, new)
p.write_text(s, encoding='utf-8')
print('updated promo gallery CSS')
