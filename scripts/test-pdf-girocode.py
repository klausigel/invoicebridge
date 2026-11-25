"""Synthetic acceptance of the optional PDF GiroCode, including final-page rendering."""
import argparse
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import uuid

from PIL import Image, ImageChops
from pypdf import PdfReader
from pypdf.generic import ContentStream

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('http_test', HERE / 'http-container.py')
http = importlib.util.module_from_spec(spec)
spec.loader.exec_module(http)


def render(pdf, dpi, renderer):
    result = subprocess.run([renderer, '-r', str(dpi), '-gray', '-singlefile', '-'],
                            input=pdf, capture_output=True, timeout=30, check=True)
    image = Image.open(io.BytesIO(result.stdout))
    image.load()
    return image


def qr_crop(image, dpi, box=(433, 651.5, 541, 759.5)):
    return image.crop(tuple(round(value * dpi / 72) for value in box))


def content(pdf):
    page = PdfReader(io.BytesIO(pdf)).pages[0]
    operations = ContentStream(page.get_contents(), page.pdf).operations
    vector_segments = sum(operator in (b're', b'l') for _, operator in operations)
    return len(page.images), vector_segments


def text(container, pdf):
    name = uuid.uuid4().hex + '.pdf'
    http.put(container, buffers={name: pdf})
    try:
        return http.docker('exec', container, 'gs', '-q', '-dSAFER', '-dBATCH', '-dNOPAUSE',
                           '-sDEVICE=txtwrite', '-sOutputFile=-', '/scratch/'+name).decode()
    finally:
        http.docker('exec', container, 'rm', '-f', '/scratch/'+name)


def decode(container, crop, expected):
    name = uuid.uuid4().hex + '.gray'
    http.put(container, buffers={name: crop.tobytes()})
    try:
        result = json.loads(http.docker('exec', container, 'dotnet', '/scratch/app/invoicebridge.dll',
                                        '--decode-qr-gray', '/scratch/'+name, str(crop.width), str(crop.height)))
        assert result['payload'] == expected and result['errorCorrection'] == 'M', 'EPC mismatch'
    finally:
        http.docker('exec', container, 'rm', '-f', '/scratch/'+name)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--publish', type=Path, required=True)
    parser.add_argument('--poppler', default='pdftoppm')
    parser.add_argument('--tools', type=Path, default=HERE.parent/'.tools')
    parser.add_argument('--image', default=http.IMAGE)
    args = parser.parse_args()
    profiles = http.test_profiles()
    a, b = profiles
    graphic = http.fixture(graphic=True)
    b['approvedGraphicSha256'] = hashlib.sha256(graphic).hexdigest()
    with http.service(args, json.dumps(profiles).encode()) as (container, port):
        def request(profile, pdf, option=''):
            return http.request(port, profile['apiKey'], pdf, route='/v1/convert'+option)

        readable = http.fixture([a['bank']])
        for value in ('', 'yes', 'TRUE', 'true&giroCode=false'):
            http.problem(request(a, readable, '?giroCode='+value), 'invalid_giro_code_option', 400)
        for own, other, fixture in ((a, b, readable), (b, a, graphic)):
            enabled = request(own, fixture, '' if own is a else '?giroCode=true')
            disabled = request(own, fixture, '?giroCode=false')
            xml_on = http.verify(container, enabled, own, other, True)
            xml_off = http.verify(container, disabled, own, other, True)
            assert xml_on == xml_off, 'checkbox changed invoice XML'
            text_on, text_off = text(container, enabled[2]), text(container, disabled[2])
            image_on, vectors_on = content(enabled[2])
            image_off, vectors_off = content(disabled[2])
            assert 'GiroCode' in text_on and 'GiroCode' not in text_off, 'PDF title'
            assert image_on == image_off and vectors_on - vectors_off >= 50, 'QR expected to add vector paths without image resources'
            bank = own['bank']
            expected = '\n'.join(('BCD','002','1','SCT',bank['bic'],bank['beneficiary'],bank['iban'],'EUR456.01','','','112684'))
            measurements = []
            for dpi in (96, 300):
                crop = qr_crop(render(enabled[2], dpi, args.poppler), dpi)
                decode(container, crop, expected)
                assert qr_crop(render(disabled[2], dpi, args.poppler), dpi).getextrema() == (255, 255), 'QR area should stay blank'
                x1, y1, x2, y2 = crop.point(lambda x: 255 if x < 128 else 0).getbbox()
                margin = min(x1,y1,crop.width-x2,crop.height-y2)
                assert margin >= round(crop.width * 4 / 49) - 2, 'quiet zone'
                assert crop.getextrema() == (0, 255), 'black/white contrast'
                measurements.append(f'{dpi} dpi: {crop.width}px, min. {margin}px white border')
            print(f'PASS {own["id"]}: {"; ".join(measurements)}; {vectors_on-vectors_off} additional vector segments, '
                  f'{image_on} images in either variant, EPC matches visible bank and identical XML', flush=True)
            http.clean(container)

        # Deliberately occupy the location and verify both preparation paths.
        check_occupied(container, request, a, http.fixture([a['bank']], occupied_qr=True), args.poppler)
    occupied_graphic = http.fixture(graphic=True, occupied_qr=True)
    b['approvedGraphicSha256'] = hashlib.sha256(occupied_graphic).hexdigest()
    with http.service(args, json.dumps(profiles).encode()) as (container, port):
        check_occupied(container, request, b, occupied_graphic, args.poppler)


def check_occupied(container, request, profile, fixture, renderer):
    http.problem(request(profile, fixture), 'unsupported_pdf', 422)
    disabled = request(profile, fixture, '?giroCode=false')
    assert disabled[0] == 200, 'occupied QR area blocked disabled conversion'
    left = qr_crop(render(fixture, 150, renderer), 150)
    right = qr_crop(render(disabled[2], 150, renderer), 150)
    difference = ImageChops.difference(left, right).histogram()
    # Poppler draws the original and the PDF/A embedded font with slightly
    # different antialiasing. Verify the occupied marker's geometry and ink too.
    before, after = [im.point(lambda x: 255 if x < 128 else 0) for im in (left, right)]
    bbox_before, bbox_after = before.getbbox(), after.getbbox()
    assert bbox_before and bbox_after and all(abs(x-y) <= 2 for x,y in zip(bbox_before, bbox_after)), 'existing content moved or disappeared'
    assert abs(after.histogram()[255] - before.histogram()[255]) < 0.16 * before.histogram()[255], 'occupied area lost or added ink'
    changed = sum(difference[17:]) / (left.width*left.height)
    assert changed < 0.02, 'occupied area altered'
    if profile['id'] == 'KIG':
        assert 'KEEP THIS VISIBLE' in text(container, disabled[2]), 'occupied text no longer searchable'
    http.clean(container)
    print(f'PASS {profile["id"]}: occupied QR area rejects enabled, preserved without GiroCode ({changed:.2%} Poppler pixel difference)', flush=True)


if __name__ == '__main__':
    main()
