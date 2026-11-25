"""Synthetic visual and HTTP acceptance for the captioned PDF GiroCode."""
import argparse
from ctypes import create_string_buffer
from decimal import Decimal
from importlib.util import module_from_spec, spec_from_file_location
from io import BytesIO
from pathlib import Path
from xml.etree import ElementTree as ET

from PIL import ImageChops
from pypdf import PdfReader
from pypdf.generic import ContentStream
from pypdfium2 import PdfDocument
from pypdfium2.raw import FPDFText_GetFontInfo

SPEC = spec_from_file_location('qr_rendering', Path(__file__).with_name('test-qr-rendering.py'))
VISUAL = module_from_spec(SPEC)
SPEC.loader.exec_module(VISUAL)
KIG = VISUAL.KIG
AREA = (430, 635, 548, 762)
QR_LEFT, QR_TOP = 433, 651.5
FRAME_LEFT, FRAME_RIGHT = 431.2, 544.3
SCAN_HINT = 'Mit Banking App scannen'


def captions(pdf, reference, *, raster_body=False):
    with PdfDocument(pdf) as doc:
        page = doc[0]
        text = page.get_textpage()
        for caption in ('GiroCode', SCAN_HINT):
            search = text.search(caption, match_case=True)
            found = search.get_next()
            assert found is not None and search.get_next() is None, 'caption complete and unique'
            start, _ = found
            glyphs = [text.get_charbox(index) for index, char in enumerate(caption, start) if char != ' ']
            left = min(glyph[0] for glyph in glyphs)
            top = page.get_height() - max(glyph[3] for glyph in glyphs)
            right = max(glyph[2] for glyph in glyphs)
            bottom = page.get_height() - min(glyph[1] for glyph in glyphs)
            if caption == 'GiroCode':
                length = FPDFText_GetFontInfo(text.raw, start, None, 0, None)
                name = create_string_buffer(length)
                FPDFText_GetFontInfo(text.raw, start, name, length, None)
                assert b'Bold' in name.value, 'embedded title font is bold'
                assert 635 < top < bottom < 648.5 and abs((left + right - FRAME_LEFT - FRAME_RIGHT) / 2) < .6, 'centered title above frame'
            else:
                assert QR_LEFT + VISUAL.SIZE < left < FRAME_RIGHT < right < 548, f'hint fits right edge: {(left, right)}'
                assert 660 < top < bottom < 750 and glyphs[-1][1] > glyphs[0][1], 'hint reads from bottom to top'
                assert abs((top + bottom) / 2 - 704.85) < .7, 'hint centered in right edge'
                hint_bounds = (top, bottom)
        block_text = text.get_text_bounded(AREA[0], page.get_height() - AREA[3], AREA[2], page.get_height() - AREA[1])
        assert reference not in block_text, 'no visible reference in QR block'
        if not raster_body:
            assert reference in text.get_text_range(), 'reference remains in invoice body'
        return hint_bounds


def module_count(prepared):
    page = PdfReader(BytesIO(prepared)).pages[0]
    transforms = ContentStream(page.get_contents(), page.pdf).operations
    sizes = [float(args[0]) for args, op in transforms if op == b'cm' and len(args) == 6
             and float(args[4]) == QR_LEFT and 1.5 <= float(args[0]) <= 4]
    assert len(sizes) == 1, 'one compound QR module grid'
    return round(VISUAL.SIZE / sizes[0])


def frame_and_quiet_zone(image, modules, dpi, hint_bounds):
    scale = dpi / 72
    qr = KIG.QR.qr_crop(image, dpi)
    dark = qr.point(lambda ink: 255 if ink < 128 else 0).getbbox()
    assert dark is not None, 'QR has ink'
    x1, y1, x2, y2 = dark
    quiet = min(x1, y1, qr.width - x2, qr.height - y2)
    minimum = round(4 * VISUAL.SIZE / modules * scale) - 2
    assert quiet >= minimum, 'four-module quiet zone'
    for strip in ((0, 0, qr.width, minimum), (0, qr.height - minimum, qr.width, qr.height),
                  (0, 0, minimum, qr.height), (qr.width - minimum, 0, qr.width, qr.height)):
        assert qr.crop(strip).getextrema() == (255, 255), 'white quiet zone'
    top, bottom = hint_bounds
    for y1, y2 in ((top - 2, top - 1), (bottom + 1, bottom + 2)):
        gap = image.crop(tuple(round(v * scale) for v in (543.9, y1, 544.7, y2)))
        assert gap.getextrema() == (255, 255), 'clear space between the hint and the border ends'
    for x1, y1, x2, y2 in ((438, 648.1, 539, 648.9), (438, 760.8, 539, 761.6),
                           (430.8, 660, 431.6, 750), (543.9, 655, 544.7, top - 5),
                           (543.9, bottom + 5, 544.7, 752)):
        line = image.crop(tuple(round(v * scale) for v in (x1, y1, x2, y2)))
        assert min(line.tobytes()) < 80, 'each black border edge visible'


def compare(before, after, modules, tools, path, hint_bounds):
    gs = next(tools.resolve().rglob('gswin64c.exe'))
    for renderer in ('PDFium', 'Poppler', 'Ghostscript'):
        for dpi in (72, 96, 120, 144, 300, 600):
            old = VISUAL.render(before, dpi, dpi >= 300, renderer, gs)
            new = VISUAL.render(after, dpi, dpi >= 300, renderer, gs)
            bounds = tuple(round(value * dpi / 72) for value in AREA)
            diff = ImageChops.difference(old, new)
            diff.paste(0, bounds)
            assert diff.getbbox() is None, f'changes outside approved area at {renderer} {dpi} dpi: {diff.getbbox()}'
            assert all(pixel <= 8 for pixel in VISUAL.finder_pixels(new, dpi, modules, QR_TOP, QR_LEFT)), 'solid finder cores'
            if dpi == 300:
                assert VISUAL.module_centers(old, dpi, modules, QR_TOP, QR_LEFT) == VISUAL.module_centers(new, dpi, modules, QR_TOP, QR_LEFT), 'same QR module pattern'
                frame_and_quiet_zone(new, modules, dpi, hint_bounds)
            if renderer == 'Ghostscript' and dpi == 96 and path:
                VISUAL.comparison_png(old, new, dpi, path, AREA)
        print(f'PASS {renderer}: layout bounds and solid finder cores at 72, 96, 120, 144, 300 and 600 dpi', flush=True)


def check_rendered_reference(source, pdf, ghostscript):
    box = tuple(round(v * 150 / 72) for v in (116, 357, 152, 373))
    masks = [VISUAL.render(data, 150, renderer='Ghostscript', ghostscript=ghostscript).crop(box)
             .point(lambda ink: 255 if ink < 128 else 0)
             for data in (source, pdf)]
    bounds = [mask.getbbox() for mask in masks]
    assert all(bounds) and all(abs(a-b) <= 2 for a, b in zip(*bounds)), 'invoice number visible at original position'
    # The approved raster-rebuild path can shift pixels by one at this resolution.
    difference = min(ImageChops.difference(masks[0], ImageChops.offset(masks[1], x, y)).histogram()[255]
                     for x in (-1, 0, 1) for y in (-1, 0, 1))
    assert difference <= .3 * masks[0].histogram()[255], f'invoice number glyph difference: {difference}'


def http_checks(args, tenants=('KIG', 'Manuel')):
    profiles, graph = KIG.LOCAL.real_mode_fixtures()
    a, b = profiles
    a['bank'] = KIG.dutch_account()
    ghostscript = next(args.tools.resolve().rglob('gswin64c.exe'))
    with KIG.LOCAL.local(args, profiles, 'local-real') as (port, base):
        for profile, source, amount, reference in (
            (a, KIG.fixture(a['bank']), Decimal('66.64'), '20260201-00001'),
            (b, graph, Decimal('456.01'), '112684'),
        ):
            if profile['id'] not in tenants:
                continue
            attachments = []
            for qr in (True, False):
                response = KIG.convert(port, profile, source, qr=qr)
                assert response[:2] == (200, 'application/pdf'), f'conversion HTTP {response[0]}'
                pdf = response[2]
                checks = KIG.HTTP.request(port, profile['apiKey'], pdf, route='/v1/validate')
                KIG.HTTP.validation(checks, 'passed')
                reader = PdfReader(BytesIO(pdf))
                attachments.append(reader.attachments['factur-x.xml'][0])
                xml = ET.fromstring(attachments[-1])
                bank = {field: xml.findtext('.//{*}' + tag) for field, tag in (
                    ('iban', 'IBANID'), ('bic', 'BICID'), ('beneficiary', 'AccountName'))}
                due = Decimal(xml.findtext('.//{*}DuePayableAmount'))
                remittance = xml.findtext('.//{*}PaymentReference')
                assert bank == {field: profile['bank'][field] for field in bank}, 'correct structured payment data'
                assert due == amount and remittance == reference, 'correct structured amount and remittance'
                KIG.check_qr(args.publish, base, pdf, bank, due, remittance, qr)
                if profile['id'] == 'Manuel':
                    check_rendered_reference(source, pdf, ghostscript)
                if qr:
                    captions(pdf, reference, raster_body=profile['id'] == 'Manuel')
                else:
                    text = reader.pages[0].extract_text()
                    assert SCAN_HINT not in text and 'GiroCode' not in text, 'no caption when disabled'
            assert attachments[0] == attachments[1], 'same XML with or without QR'
            print(f'PASS {profile["id"]}: caption, QR on/off, EPC, PDF/A and ZUGFeRD', flush=True)
        occupied = KIG.fixture(a['bank'], occupied=True, occupied_at=(431, 642))
        conflict = KIG.convert(port, a, occupied)
        KIG.HTTP.problem(conflict, 'unsupported_pdf', 422)
        unchanged = KIG.convert(port, a, occupied, qr=False)
        assert unchanged[:2] == (200, 'application/pdf')
        assert 'KEEP' in PdfReader(BytesIO(unchanged[2])).pages[0].extract_text(), 'existing frame-area content preserved'
        print('PASS occupied frame area: 422 with QR, existing content preserved without QR', flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--before', type=Path, required=True)
    parser.add_argument('--publish', type=Path, required=True)
    parser.add_argument('--tools', type=Path, default=KIG.LOCAL.ROOT/'artifacts/Kig.InvoiceBridge-pre-checkin-clean-win-x64/tools')
    parser.add_argument('--comparison', type=Path)
    args = parser.parse_args()
    before = VISUAL.pdfs(args.before, args.tools)[1]
    prepared, after = VISUAL.pdfs(args.publish, args.tools)
    hint_bounds = captions(after, '20260201-00001')
    page = PdfReader(BytesIO(after)).pages[0]
    assert sum(op == b'c' for _, op in ContentStream(page.get_contents(), page.pdf).operations) >= 4, 'rounded frame curves in final PDF'
    compare(before, after, module_count(prepared), args.tools, args.comparison, hint_bounds)
    http_checks(args)
