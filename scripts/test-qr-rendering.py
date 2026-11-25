"""Measure antialiasing seams in synthetic GiroCode PDFs, before and after PDF/A."""
import argparse
from concurrent.futures import ThreadPoolExecutor
from importlib.util import module_from_spec, spec_from_file_location
from io import BytesIO
from pathlib import Path
import subprocess
import tempfile
import time
from types import SimpleNamespace

from PIL import Image, ImageDraw
from pypdf import PdfReader
from pypdf.generic import ContentStream
from pypdfium2 import PdfDocument, PDFIUM_INFO

ROOT = Path(__file__).resolve().parent.parent
SPEC = spec_from_file_location('kig_fixture', ROOT / 'scripts/test-kig-invoice.py')
KIG = module_from_spec(SPEC)
SPEC.loader.exec_module(KIG)
LEFT, TOP, SIZE = 435, 650, 108


def pdfs(publish, tools):
    profiles, _ = KIG.LOCAL.real_mode_fixtures()
    profile = profiles[0]
    profile['bank'] = KIG.dutch_account()
    source = KIG.fixture(profile['bank'])
    options = SimpleNamespace(tools=tools, publish=publish)
    with KIG.LOCAL.local(options, profiles, 'local-real') as (port, work):
        with ThreadPoolExecutor(1) as pool:
            request = pool.submit(KIG.convert, port, profile, source)
            prepared = None
            # Ghostscript and Mustang run after this intermediate file is complete.
            while prepared is None and not request.done():
                for file in work.glob('invoicebridge-http-*/prepared.pdf'):
                    try:
                        candidate = file.read_bytes()
                    except OSError:
                        continue
                    if candidate.rstrip().endswith(b'%%EOF'):
                        prepared = candidate
                if prepared is None:
                    time.sleep(.05)
            response = request.result()
        assert prepared is not None, 'prepared stage captured'
        assert response[:2] == (200, 'application/pdf'), f'final HTTP {response[0]}'
        return prepared, response[2]


def module_count(prepared):
    reader = PdfReader(BytesIO(prepared))
    operations = ContentStream(reader.pages[0].get_contents(), reader).operations
    widths = [float(args[2]) for args, op in operations if op == b're'
              and len(args) == 4 and 0 < float(args[2]) <= 4
              and LEFT <= float(args[0]) <= LEFT + SIZE]
    assert len(widths) > 100 and max(widths) - min(widths) < .01, 'baseline module geometry'
    return round(SIZE / widths[0])


def render(pdf, dpi, printing=False, renderer='PDFium', ghostscript=None):
    if renderer == 'Poppler':
        result = subprocess.run(['pdftoppm', '-gray', '-singlefile', '-r', str(dpi), '-aaVector', 'yes', '-'],
                                input=pdf, check=True, capture_output=True, timeout=30)
        return Image.open(BytesIO(result.stdout)).copy()
    if renderer == 'Ghostscript':
        with tempfile.TemporaryDirectory(prefix='invoicebridge-qr-render-') as directory:
            file = Path(directory) / 'synthetic.pdf'
            file.write_bytes(pdf)
            result = subprocess.run([str(ghostscript), '-q', '-dSAFER', '-dBATCH', '-dNOPAUSE', '-sDEVICE=pgmraw',
                                     '-dGraphicsAlphaBits=4', '-dTextAlphaBits=4', '-r' + str(dpi),
                                     '-sOutputFile=-', str(file)], check=True, capture_output=True, timeout=30)
        return Image.open(BytesIO(result.stdout)).copy()
    with PdfDocument(pdf) as document:
        bitmap = document[0].render(scale=dpi / 72, grayscale=True, optimize_mode='print' if printing else None)
        try:
            return bitmap.to_pil().copy()
        finally:
            bitmap.close()


def finder_pixels(image, dpi, modules, qr_top=TOP, qr_left=LEFT):
    scale = dpi / 72
    step = SIZE / modules
    start, end = 4, modules - 11
    # The filled 3x3 core of each finder contains four internal module edges.
    # Trim its perimeter to exclude legitimate edge antialiasing.
    for x, y in ((start, start), (end, start), (start, end)):
        left, top = (qr_left + (x + 2) * step) * scale, (qr_top + (y + 2) * step) * scale
        right, bottom = left + 3 * step * scale, top + 3 * step * scale
        box = (round(left + 1), round(top + 1), round(right - 1), round(bottom - 1))
        yield from image.crop(box).tobytes()


def module_centers(image, dpi, modules, qr_top=TOP, qr_left=LEFT):
    scale = dpi / 72
    step = SIZE / modules
    return [image.getpixel((int((qr_left + (x + .5) * step) * scale), int((qr_top + (y + .5) * step) * scale))) < 128
            for y in range(modules) for x in range(modules)]


def comparison_png(before, after, dpi, destination, area=(LEFT, TOP, LEFT + SIZE, TOP + SIZE)):
    box = tuple(round(v * dpi / 72) for v in area)
    crops = [image.crop(box) for image in (before, after)]
    crops = [crop.resize((crop.width * 2, crop.height * 2), Image.Resampling.NEAREST) for crop in crops]
    width, height = crops[0].size
    canvas = Image.new('L', (width * 2 + 24, height + 40), 255)
    draw = ImageDraw.Draw(canvas)
    for x, crop, label in ((8, crops[0], 'Before'), (width + 16, crops[1], 'After')):
        draw.text((x, 8), label, fill=0)
        canvas.paste(crop, (x, 26))
    canvas.save(destination)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--before', required=True, type=Path)
    parser.add_argument('--after', type=Path)
    parser.add_argument('--tools', type=Path, default=ROOT/'artifacts/Kig.InvoiceBridge-pre-checkin-clean-win-x64/tools')
    parser.add_argument('--comparison', type=Path)
    renderers = ('PDFium', 'Poppler', 'Ghostscript')
    parser.add_argument('--renderer', choices=(*renderers, 'all'), default='all')
    args = parser.parse_args()
    before = pdfs(args.before, args.tools)
    after = pdfs(args.after, args.tools) if args.after else None
    modules = module_count(before[0])
    ghostscript = next(args.tools.resolve().rglob('gswin64c.exe'), None)
    for renderer in renderers if args.renderer == 'all' else (args.renderer,):
        print(f'PDFium {PDFIUM_INFO}' if renderer == 'PDFium' else renderer, flush=True)
        for label, dpi, printing in (('75% screen', 72, False), ('100% screen', 96, False),
                                     ('125% screen', 120, False), ('150% screen', 144, False),
                                     ('simulated print', 300, True), ('simulated print', 600, True)):
            for stage, baseline_pdf, fixed_pdf in zip(('prepared', 'final'), before, after or (None, None)):
                original = render(baseline_pdf, dpi, printing, renderer, ghostscript)
                original_pixels = list(finder_pixels(original, dpi, modules))
                baseline_seams = sum(pixel > 8 for pixel in original_pixels)
                if fixed_pdf is None:
                    print(f'{stage}, {label} {dpi} dpi: {baseline_seams}/{len(original_pixels)} light pixels', flush=True)
                    continue
                fixed = render(fixed_pdf, dpi, printing, renderer, ghostscript)
                fixed_pixels = list(finder_pixels(fixed, dpi, modules))
                fixed_seams = sum(pixel > 8 for pixel in fixed_pixels)
                print(f'{stage}, {label} {dpi} dpi: {baseline_seams} -> {fixed_seams}/{len(fixed_pixels)} light pixels',
                      flush=True)
                assert fixed_seams == 0, 'solid finder core'
                if renderer == 'Ghostscript' and stage == 'final' and dpi == 96:
                    assert baseline_seams > 0, 'baseline defect reproduced'
                    if args.comparison:
                        comparison_png(original, fixed, dpi, args.comparison)
                if stage == 'final' and dpi == 300:
                    assert module_centers(original, dpi, modules) == module_centers(fixed, dpi, modules), 'same QR grid'


if __name__ == '__main__':
    main()
