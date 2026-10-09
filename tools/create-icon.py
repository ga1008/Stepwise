from pathlib import Path
from PIL import Image, ImageDraw

root = Path(__file__).resolve().parents[1]
asset = root / 'src' / 'Stepwise' / 'Assets'
asset.mkdir(exist_ok=True)
im = Image.new('RGBA', (256, 256))
d = ImageDraw.Draw(im)
d.rounded_rectangle((8, 8, 248, 248), radius=60, fill='#156B58')
d.rounded_rectangle((60, 58, 116, 83), radius=9, fill='#C7E6CD')
d.rounded_rectangle((60, 112, 157, 137), radius=9, fill='#E6F3E8')
d.rounded_rectangle((60, 166, 198, 191), radius=9, fill='white')
d.ellipse((170, 56, 199, 85), fill='#EEB9A4')
im.save(asset / 'stepwise.ico', sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
im.save(asset / 'stepwise.png')
