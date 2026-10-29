"""Regenerate Dodo's geometric icon (Pillow required only for this optional tool)."""
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "assets"
SCALE = 4
im = Image.new("RGBA", (256*SCALE, 256*SCALE))
d = ImageDraw.Draw(im)
def box(v): return tuple(round(x*SCALE) for x in v)
def ellipse(v, fill): d.ellipse(box(v), fill=fill)
def line(v, fill, width): d.line([(x*SCALE,y*SCALE) for x,y in v],fill=fill,width=width*SCALE,joint="curve")
sage,ink,paper,wing,beak = "#DFE9D8","#293B39","#FFF9E8","#8AA99B","#E7AC59"
d.rounded_rectangle(box((8,8,248,248)),radius=60*SCALE,fill=sage)
# A folded sheet supports the bird without adding detail to its silhouette.
d.rounded_rectangle(box((55,42,184,216)),radius=16*SCALE,fill=paper)
d.polygon([(x*SCALE,y*SCALE) for x,y in [(157,42),(184,69),(157,69)]],fill="#C1D0BC")
line([(107,186),(107,204),(96,204)],ink,8)
line([(143,186),(143,204),(154,204)],ink,8)
ellipse((65,91,172,191),ink)
ellipse((71,97,167,186),wing)
ellipse((106,57,184,139),ink)
ellipse((112,63,178,133),paper)
# The oversized, rounded beak gives Dodo its quietly goofy character.
d.rounded_rectangle(box((160,94,218,128)),radius=15*SCALE,fill=ink)
d.rounded_rectangle(box((165,99,213,122)),radius=10*SCALE,fill=beak)
ellipse((148,82,158,94),ink)
ellipse((95,119,146,165),"#B7CABB")
line([(111,67),(106,55),(117,58)],ink,5)
im=im.resize((256,256),Image.Resampling.LANCZOS)
im.save(OUT/"dodo.png")
im.save(OUT/"Dodo.ico",sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])

svg='''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" role="img" aria-label="Dodo, a little bird on a folded sheet">
<rect x="8" y="8" width="240" height="240" rx="60" fill="#dfe9d8"/>
<path d="M71 42h86l27 27v131q0 16-16 16H71q-16 0-16-16V58q0-16 16-16" fill="#fff9e8"/>
<path d="M157 42v27h27" fill="#c1d0bc"/>
<g stroke="#293b39" stroke-linecap="round" stroke-linejoin="round"><path d="M107 186v18H96m47-18v18h11" fill="none" stroke-width="8"/>
<ellipse cx="119" cy="141" rx="51" ry="47" fill="#8aa99b" stroke-width="6"/>
<ellipse cx="145" cy="98" rx="36" ry="38" fill="#fff9e8" stroke-width="6"/>
<rect x="163" y="97" width="52" height="28" rx="13" fill="#e7ac59" stroke-width="6"/>
<path d="m111 67-5-12 11 3" fill="none" stroke-width="5"/></g>
<ellipse cx="153" cy="88" rx="5" ry="6" fill="#293b39"/><ellipse cx="120" cy="142" rx="25" ry="23" fill="#b7cabb"/>
</svg>'''
(OUT/'dodo.svg').write_text(svg,encoding='utf-8')
bird=svg[svg.index('<rect'):svg.rindex('</svg>')]
cover=f'''<svg xmlns="http://www.w3.org/2000/svg" width="1200" height="420" viewBox="0 0 1200 420" role="img" aria-label="Dodo. A little bird for your everyday notes and to-dos.">
<rect width="1200" height="420" rx="28" fill="#f6f5ee"/>
<path d="M0 359Q310 300 590 355t610-30v95H0" fill="#e3ebde"/>
<g transform="translate(824 64) scale(1.1)">{bird}</g>
<text x="76" y="88" fill="#5a7569" font-family="Segoe UI,Arial,sans-serif" font-size="15" letter-spacing="4">SMALL NOTES. ROOM TO BREATHE.</text>
<text x="70" y="218" fill="#293b39" font-family="Segoe UI,Arial,sans-serif" font-size="112" font-weight="700" letter-spacing="-5">Dodo<tspan fill="#d7a257">.</tspan></text>
<text x="76" y="272" fill="#52645d" font-family="Segoe UI,Arial,sans-serif" font-size="27">A little bird for your everyday notes and to-dos.</text>
<text x="76" y="355" fill="#466454" font-family="Segoe UI,Arial,sans-serif" font-size="17">NATIVE WINDOWS · LOCAL FIRST · QUIETLY USEFUL</text>
</svg>'''
(OUT/'dodo-cover.svg').write_text(cover,encoding='utf-8')
