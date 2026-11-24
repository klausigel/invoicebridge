"""Launch and exercise InvoiceBridge HTTP without baking any inputs into an image."""
import argparse
from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
from http.client import HTTPConnection
import hashlib
import io
import json
from pathlib import Path
import re
import secrets
import socket
import string
import struct
import subprocess
import sys
import tarfile
import threading
import time
import uuid
import zlib
from xml.etree import ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent
DOCKER = ['docker', '--context', 'desktop-linux']
IMAGE = 'sha256:1e077b0bd53740001af848a7dccfb82d04365664263ed9d50755f72d71f36ed8'
MUSTANG = 'Mustang-CLI-2.26.0.jar'
VERA = 'verapdf/bin/cli-1.30.3.jar'


def require(condition, stage):
    if not condition:
        raise RuntimeError(stage)


def docker(*args, data=None):
    result = subprocess.run(DOCKER + list(args), input=data, capture_output=True, timeout=150)
    require(result.returncode == 0, f'Docker {args[0]} exit={result.returncode}')
    return result.stdout


def put(container, files=None, buffers=None):
    archive = io.BytesIO()
    with tarfile.open(fileobj=archive, mode='w') as stream:
        for target, source in (files or {}).items():
            stream.add(source, target)
        for target, content in (buffers or {}).items():
            entry = tarfile.TarInfo(target)
            entry.size, entry.mode = len(content), 0o600
            stream.addfile(entry, io.BytesIO(content))
    docker('exec', '-i', container, 'tar', '-xf', '-', '-C', '/scratch', data=archive.getvalue())


def exchange(port, key=None, pdf=None, media='application/pdf', route=None, extra=None):
    headers = {'Content-Type': media, **(extra or {})}
    if key is not None:
        headers['X-Api-Key'] = key
    client = HTTPConnection('127.0.0.1', port, timeout=130)
    try:
        client.request('GET' if pdf is None else 'POST', route or ('/healthz' if pdf is None else '/v1/convert'), pdf, headers)
        result = client.getresponse()
        return (result.status, result.getheader('Content-Type', ''), result.read()), result.headers
    finally:
        client.close()


def request(port, key=None, pdf=None, media='application/pdf', route=None):
    return exchange(port, key, pdf, media, route)[0]


@contextmanager
def service(args, profiles, port=0, failure=False, break_validation=False, session_seconds=None):
    container = 'invoicebridge-http-' + uuid.uuid4().hex
    docker('run', '-d', '--pull', 'never', '--name', container, '--read-only', '--log-driver', 'none',
           '--tmpfs', '/scratch:rw,exec,mode=1777,size=256m', '--tmpfs', '/jobs:rw,noexec,mode=1777,size=128m',
           '--tmpfs', '/tmp:rw,noexec,mode=1777,size=32m', '--user', '1654:1654', '--cap-drop', 'ALL',
           '--security-opt', 'no-new-privileges', '--publish', f'127.0.0.1:{port or ""}:8080',
           '-e', 'HOME=/scratch', '-e', 'TMPDIR=/jobs', '-e', 'DOTNET_EnableDiagnostics=0', '-e', 'INVOICEBRIDGE_CONTAINER=1',
           '-e', 'INVOICEBRIDGE_PROFILES=/scratch/profiles.json', '-e', 'INVOICEBRIDGE_TOOLS=/scratch/tools',
           *(['-e', f'INVOICEBRIDGE_SESSION_SECONDS={session_seconds}'] if session_seconds is not None else []),
           *(['-e', 'INVOICEBRIDGE_GHOSTSCRIPT=/usr/bin/false'] if failure else []), args.image, 'sleep', 'infinity')
    try:
        files = {'tools/' + name: args.tools / name for name in (MUSTANG, VERA)}
        buffers = {'profiles.json': profiles}
        if break_validation:
            del files['tools/' + VERA]
            buffers['tools/' + VERA] = b'invalid verifier jar'
        for file in args.publish.iterdir():
            if file.is_file() and (file.suffix == '.dll' or file.name.endswith(('.deps.json', '.runtimeconfig.json'))):
                files['app/' + file.name] = file
        for name in ('index.html', 'site.css', 'site.js'):
            asset = args.publish / 'wwwroot' / name
            if asset.is_file():
                files['app/wwwroot/' + name] = asset
        require('app/InvoiceBridge.Http.dll' in files, 'HTTP publish missing')
        put(container, files, buffers)
        # The input file is no longer needed once the process has initialized.
        docker('exec', '-d', container, 'dotnet', '/scratch/app/InvoiceBridge.Http.dll')
        mapping = docker('port', container, '8080/tcp').decode().strip()
        require(re.fullmatch(r'127\.0\.0\.1:\d+', mapping), 'loopback publish')
        actual = int(mapping.rsplit(':', 1)[1])
        for _ in range(60):
            try:
                if request(actual)[0] == 204:
                    break
            except OSError:
                pass
            time.sleep(.25)
        else:
            raise RuntimeError('HTTP startup failed; check runtime profiles and tool layout')
        docker('exec', container, 'rm', '/scratch/profiles.json')
        yield container, actual
    finally:
        docker('rm', '-f', container)


def test_profiles():
    def profile(name, discriminator):
        bban = '00000000' + f'{secrets.randbelow(10**9):09d}' + str(discriminator)
        check = 98 - int(bban + '131400') % 97
        bic = ''.join(secrets.choice(string.ascii_uppercase) for _ in range(3)) + chr(65 + discriminator) + 'DE00XXX'
        return dict(id=name, apiKey=secrets.token_urlsafe(36),
                    bank=dict(iban=f'DE{check:02d}{bban}', bic=bic, beneficiary='NetProof GmbH',
                              bankName='Testbank ' + name))
    return [profile('KIG', 1), profile('Manuel', 2)]


def fixture(banks=(), graphic=False, bank_x=432, hide_bank=False):
    """Synthetic one-page fixture for the existing coordinate-based sample parser."""
    rows = [
        (120, 368, '112684'), (120, 382, '09.10.2025'), (347, 367, '09.10.2025'),
        (70, 185, 'NetProof GmbH Testweg 1 21376 Salzhausen'), (70, 208, 'Demo-Empfaenger'),
        (70, 220, 'Teststrasse 2'), (70, 243, '12345 Testort'), (120, 699, 'DE000000000'),
        (70, 435, 'HelloExchange 1.x HV'), (70, 460, 'SYNTHETIC LINE ITEM'),
        (293, 433, '1,00'), (315, 433, 'Stck.'), (349, 433, '19,00%'), (403, 433, '383,20 EUR'),
        (510, 433, '383,20 EUR'), (510, 543, '383,20 EUR'), (510, 557, '72,81 EUR'),
        (510, 573, '456,01 EUR'), (114, 718, 'Vorkasse'),
    ]
    for index, bank in enumerate(banks):
        rows.extend([(bank_x, 285 + index * 20, 'IBAN: ' + bank['iban']),
                     (bank_x, 295 + index * 20, 'BIC: ' + bank['bic'])])
    stream = '\n'.join(f'BT /F1 8 Tf {3 if hide_bank and x == bank_x else 0} Tr {x} {842-top} Td ({text}) Tj ET'
                       for x, top, text in rows).encode('ascii')
    resources = b'/Font << /F1 4 0 R >>'
    image = None
    if graphic:
        # A synthetic unreadable pattern entirely within the known bank rectangle.
        pixels = bytes(35 if (x // 5 + y // 3) % 3 == 0 else 255 for y in range(18) for x in range(80))
        compressed = zlib.compress(pixels)
        image = (b'<< /Type /XObject /Subtype /Image /Width 80 /Height 18 /ColorSpace /DeviceGray '
                 b'/BitsPerComponent 8 /Filter /FlateDecode /Length %d >>\nstream\n' % len(compressed)
                 + compressed + b'\nendstream')
        resources += b' /XObject << /Bank 6 0 R >>'
        stream += b'\nq 125 0 0 25 435 530 cm /Bank Do Q'
    objects = [b'<< /Type /Catalog /Pages 2 0 R >>', b'<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
               b'<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << ' + resources + b' >> /Contents 5 0 R >>',
               b'<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
               b'<< /Length %d >>\nstream\n' % len(stream) + stream + b'\nendstream']
    if image is not None:
        objects.append(image)
    pdf = bytearray(b'%PDF-1.4\n')
    offsets = [0]
    for n, body in enumerate(objects, 1):
        offsets.append(len(pdf))
        pdf.extend(f'{n} 0 obj\n'.encode() + body + b'\nendobj\n')
    xref = len(pdf)
    pdf.extend(f'xref\n0 {len(offsets)}\n0000000000 65535 f \n'.encode())
    for offset in offsets[1:]:
        pdf.extend(f'{offset:010d} 00000 n \n'.encode())
    pdf.extend(f'trailer\n<< /Size {len(offsets)} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n'.encode())
    return bytes(pdf)


def clean(container):
    require(not docker('exec', container, 'ls', '-A', '/jobs').strip(), 'temporary jobs not removed')


def problem(response, code, status):
    require(response[0] == status and response[1].startswith('application/problem+json')
            and json.loads(response[2]).get('code') == code, f'expected HTTP {status}: {code}')


def verify_sessions(args):
    tests = test_profiles()
    data = json.dumps(tests).encode()
    a, b = tests

    def call(port, route, body=None, *, cookie=None, origin=True, site=None, header=None):
        extra = {}
        if cookie is not None: extra['Cookie'] = cookie
        if origin: extra['Origin'] = f'http://127.0.0.1:{port}' if origin is True else origin
        if site is not None: extra['Sec-Fetch-Site'] = site
        return exchange(port, header, body, 'application/json', route, extra)

    def login(port, key, **kwargs):
        return call(port, '/v1/session/login', json.dumps({'apiKey': key}).encode(), **kwargs)

    def logged_in(reply, desired):
        response, headers = reply
        require(response[0] == 200 and json.loads(response[2]) == {'tenant': desired}, 'tenant sign-in')
        value = headers.get('Set-Cookie', '')
        attrs = {part.strip().lower() for part in value.split(';')[1:]}
        require(re.fullmatch(r'invoicebridge_session=[0-9A-F]{64}', value.split(';')[0]) and
                {'httponly', 'samesite=strict', 'path=/'} <= attrs and 'secure' not in attrs
                and headers.get('Cache-Control') == 'no-store', 'loopback session-cookie flags')
        return value.split(';')[0]

    with service(args, data) as (container, port):
        problem(call(port, '/v1/session')[0], 'session_required', 401)
        problem(call(port, '/v1/payment/qr', b'{}')[0], 'api_key_required', 401)
        problem(login(port, 'invalid')[0], 'invalid_login', 401)
        for origin in (False, 'null', 'https://example.invalid', f'http://localhost:{port}', 'http://127.0.0.1:8189'):
            response, headers = login(port, a['apiKey'], origin=origin)
            problem(response, 'cross_site_request', 403)
            require(headers.get('Set-Cookie') is None, 'rejected login did not set cookie')
        current = logged_in(login(port, a['apiKey']), 'KIG')
        require(json.loads(call(port, '/v1/session', cookie=current)[0][2]) == {'tenant':'KIG'}, 'session lookup')
        for route in ('/v1/convert','/v1/validate','/v1/payment/read','/v1/payment/qr','/v1/session/logout'):
            for origin in (False, 'http://127.0.0.1:8189'):
                problem(call(port, route, b'{}', cookie=current, origin=origin)[0], 'cross_site_request', 403)
        problem(call(port, '/v1/session/logout', b'', cookie=current, site='same-site')[0], 'cross_site_request', 403)
        problem(call(port, '/v1/payment/qr', b'{}', cookie=current, header='invalid')[0], 'api_key_required', 401)
        require(call(port, '/v1/payment/qr', b'{}', cookie=current, site='same-origin')[0][0] == 422,
                'same-origin session reaches QR validation')
        response, headers = call(port, '/v1/session/logout', b'', cookie=current)
        require(response[0] == 204 and 'expires=' in headers.get('Set-Cookie', '').lower(), 'logout clears cookie')
        problem(call(port, '/v1/session', cookie=current)[0], 'session_expired', 401)
        problem(call(port, '/v1/payment/qr', b'{}', cookie=current)[0], 'session_required', 401)
        manuel = logged_in(login(port, b['apiKey']), 'Manuel')
        replacement = logged_in(login(port, a['apiKey'], cookie=manuel), 'KIG')
        require(replacement != manuel, 'login rotates session identifier')
        problem(call(port, '/v1/session', cookie=manuel)[0], 'session_expired', 401)
        clean(container)
    with service(args, data, session_seconds=1) as (container, port):
        current = logged_in(login(port, a['apiKey']), 'KIG')
        require(call(port, '/v1/session', cookie=current)[0][0] == 200, 'session initially live')
        time.sleep(1.2)
        problem(call(port, '/v1/payment/qr', b'{}', cookie=current)[0], 'session_required', 401)
        problem(call(port, '/v1/session', cookie=current)[0], 'session_expired', 401)
        clean(container)
    print('PASS browser sessions: login/rotation/logout, replay rejection, expiry, cookie flags, origin rejection and API-key precedence', flush=True)


def verify(container, response, profile, other, independent):
    status, content, pdf = response
    require(status == 200 and content == 'application/pdf' and pdf.startswith(b'%PDF-'), f'{profile["id"]} PDF, HTTP {status}')
    token = uuid.uuid4().hex
    base = '/scratch/' + token
    path = base + '.pdf'
    extracted = base + '.xml'
    put(container, buffers={token + '.pdf': pdf})
    jvm = ('exec', container, 'java')
    try:
        docker(*jvm, '-jar', '/scratch/tools/' + MUSTANG, '--action', 'extract', '--source', path,
               '--out', extracted, '--disable-file-logging')
        xml = docker('exec', container, 'cat', extracted)
        root = ET.fromstring(xml)
        own = profile['bank']
        rival = other['bank']
        def values(tree, name):
            return [element.text for element in tree.findall('.//{*}' + name)]
        require(values(root, 'IBANID') == [own['iban']] and values(root, 'BICID') == [own['bic']], 'tenant account fields')
        require(values(root, 'AccountName') == [own['beneficiary']], 'tenant beneficiary')
        require(all(rival[field].encode() not in xml for field in ('iban', 'bic', 'bankName')), 'cross-tenant data')
        from decimal import Decimal
        for name, expected in {
            'LineTotalAmount': '383.20', 'TaxBasisTotalAmount': '383.20', 'TaxTotalAmount': '72.81',
            'GrandTotalAmount': '456.01', 'TotalPrepaidAmount': '0', 'DuePayableAmount': '456.01',
            'RateApplicablePercent': '19',
        }.items():
            parts = values(root, name)
            require(bool(parts) and all(Decimal(value) == Decimal(expected) for value in parts), 'financial field ' + name)
        require(set(values(root, 'CategoryCode')) == {'S'}, 'tax category')
        text = docker('exec', container, 'gs', '-q', '-dSAFER', '-dBATCH', '-dNOPAUSE',
                      '-sDEVICE=txtwrite', '-sOutputFile=-', path).decode(errors='replace')
        require(('SYNTHETIC LINE ITEM' in text) == (profile['id'] == 'KIG'), 'text preservation branch')
        require('NICHT ZAHLEN' not in text and 'NICHT ZAHLEN'.encode() not in xml, 'obsolete notice')
        require(own['iban'] in text and own['bic'] in text and rival['iban'] not in text and rival['bic'] not in text,
                'visible bank and account isolation')

        if independent:
            hybrid = docker(*jvm, '-jar', '/scratch/tools/' + MUSTANG, '--action', 'validate',
                            '--source', path, '--disable-file-logging')
            summaries = ET.fromstring(hybrid[hybrid.index(b'<'):]).findall('.//summary')
            require(summaries and all(x.get('status') == 'valid' for x in summaries), 'independent hybrid')
            vera = docker(*jvm, '-XX:+IgnoreUnrecognizedVMOptions', '-classpath', '/scratch/tools/verapdf/bin/*',
                          '--add-exports=java.base/sun.security.pkcs=ALL-UNNAMED',
                          'org.verapdf.apps.GreenfieldCliWrapper', '--format', 'xml', '--flavour', '3b', path)
            reports = ET.fromstring(vera[vera.index(b'<'):]).findall('.//validationReport')
            require(len(reports) == 1 and reports[0].get('isCompliant') == 'true', 'independent PDF/A-3b')
        print(f'PASS {profile["id"]}: HTTP PDF, XML account isolation, amounts/tax'
              + (', independent Mustang and veraPDF' if independent else ' (parallel upload)'), flush=True)
        return xml
    finally:
        docker('exec', container, 'rm', '-f', path, extracted)


def validation(response, status):
    data = json.loads(response[2])
    require(response[0] == (503 if status == 'error' else 200) and data.get('status') == status,
            f'formal validation expected {status}, HTTP {response[0]}')
    return {item['name']: item['state'] for item in data['checks']}


def verify_qr(container, result, expected):
    import base64
    png = base64.b64decode(result['png'], validate=True)
    require(result['payment'] == expected and png[:8] == b'\x89PNG\r\n\x1a\n', 'QR response')
    width, height = struct.unpack('>II', png[16:24])
    require(width == height and width < 100, 'QR dimensions')
    pos, compressed = 8, bytearray()
    while pos < len(png):
        size, = struct.unpack('>I', png[pos:pos+4])
        if png[pos+4:pos+8] == b'IDAT':
            compressed.extend(png[pos+8:pos+8+size])
        pos += 12+size
    data = zlib.decompress(compressed)
    scale, stride = 6, width*3+1
    raw = bytearray()
    for y in range(height):
        require(data[y*stride] == 0, 'QR PNG filter')
        row = b''.join(bytes([value])*scale for value in data[y*stride+1:(y+1)*stride:3])
        raw.extend(row*scale)
    filename = uuid.uuid4().hex + '.gray'
    put(container, buffers={filename: raw})
    try:
        decoded = json.loads(docker('exec', container, 'dotnet', '/scratch/app/invoicebridge.dll',
                                    '--decode-qr-gray', '/scratch/' + filename, str(width*scale), str(height*scale)))
        expected_payload = '\n'.join(('BCD','002','1','SCT',expected['bic'],expected['beneficiary'],expected['iban'],
                                      'EUR'+expected['amount'],'','',expected['reference']))
        require(decoded['payload'] == expected_payload and decoded['errorCorrection'] == 'M', 'decoded EPC content')
    finally:
        docker('exec', container, 'rm', '-f', '/scratch/' + filename)


def verify_web(container, port, profile, input_pdf, hybrid, invoice_xml, graphic_pdf):
    key = profile['apiKey']
    for name in ('/', '/site.css', '/site.js'):
        result = request(port, route=name)
        require(result[0] == 200 and key.encode() not in result[2], 'public web assets: ' + name)
    for route, media, body in (('/v1/validate', 'application/pdf', hybrid), ('/v1/payment/read', 'application/xml', invoice_xml),
                               ('/v1/payment/qr', 'application/json', b'{}')):
        problem(request(port, None, body, media, route), 'api_key_required', 401)
    require(set(validation(request(port,key,hybrid,route='/v1/validate'),'passed').values()) == {'passed'}, 'all formal checks')
    require('invalid' in validation(request(port,key,input_pdf,route='/v1/validate'),'invalid').values(), 'ordinary PDF fails formally')
    require('invalid' in validation(request(port,key,b'not PDF',route='/v1/validate'),'invalid').values(), 'malformed PDF')
    for body, media, source in [(invoice_xml,'application/xml','xml'), (hybrid,'application/pdf','xml'), (input_pdf,'application/pdf','pdf')]:
        result = request(port,key,body,media,'/v1/payment/read')
        require(result[0] == 200, f'prefill {source} HTTP {result[0]}')
        data = json.loads(result[2])
        require(data['source'] == source and data['fields']['iban'] == profile['bank']['iban']
                and data['fields']['amount'] == '456.01' and data['fields']['reference'] == '112684', 'prefill values')
    result = request(port,key,graphic_pdf,route='/v1/payment/read')
    require(result[0] == 200 and json.loads(result[2])['fields']['iban'] is None, 'manual completion: absent account')
    tag = b'</ram:SpecifiedTradeSettlementPaymentMeans>'
    if tag in invoice_xml:
        doubled = invoice_xml.replace(tag, tag + b'<ram:SpecifiedTradeSettlementPaymentMeans/ >'.replace(b'/ >', b'/>'), 1)
        problem(request(port,key,doubled,'application/xml','/v1/payment/read'),'payment_ambiguous',422)
    else:
        tree = ET.fromstring(invoice_xml)
        settlement = tree.find('.//{*}ApplicableHeaderTradeSettlement')
        means = settlement.find('./{*}SpecifiedTradeSettlementPaymentMeans')
        settlement.append(means)
        problem(request(port,key,ET.tostring(tree),'application/xml','/v1/payment/read'),'payment_ambiguous',422)
    other = test_profiles()[1]['bank']
    problem(request(port,key,fixture([profile['bank'],other]),route='/v1/payment/read'),'payment_ambiguous',422)
    manual = dict(beneficiary='Synthetischer Empfaenger', iban=other['iban'], bic='', amount='12.34', reference='Manuelle Eingabe')
    result = request(port,key,json.dumps(manual).encode(),'application/json','/v1/payment/qr')
    require(result[0] == 200, 'manual QR generation')
    verify_qr(container,json.loads(result[2]),manual)
    problem(request(port,key,json.dumps(dict(manual, iban='DE00000000000000000000')).encode(),
                    'application/json','/v1/payment/qr'),'payment_invalid',422)
    clean(container)
    print('PASS web API: assets, authorization, formal checks, XML/PDF prefill, ambiguity, manual QR and decoded EPC, jobs empty', flush=True)


def test(args):
    verify_sessions(args)
    profiles = test_profiles()
    a, b = profiles
    sample = fixture([a['bank']])
    graphic_sample = fixture(graphic=True)
    b['approvedGraphicSha256'] = hashlib.sha256(graphic_sample).hexdigest()
    profile_bytes = json.dumps(profiles).encode()
    inputs = {'KIG': sample, 'Manuel': graphic_sample}
    started = time.monotonic()
    with service(args, profile_bytes) as (container, port):
        clean(container)
        for key in (None, secrets.token_urlsafe(36)):
            problem(request(port, key, sample), 'api_key_required', 401)
            clean(container)
        for key, pdf, code in (
            (a['apiKey'], fixture([b['bank']]), 'payment_conflict'),
            (a['apiKey'], fixture([a['bank'], b['bank']]), 'payment_ambiguous'),
            (a['apiKey'], fixture([a['bank']], bank_x=200), 'payment_unclear'),
            (a['apiKey'], fixture([a['bank']], graphic=True), 'payment_unclear'),
            (a['apiKey'], fixture([a['bank']], hide_bank=True), 'payment_unclear'),
            (a['apiKey'], graphic_sample, 'payment_unclear'),
            (b['apiKey'], graphic_sample + b'% changed', 'payment_unclear'),
            (b['apiKey'], fixture(), 'payment_unclear'),
        ):
            problem(request(port, key, pdf), code, 422)
            clean(container)
        print('PASS API keys: 401; conflicts, ambiguity and unclear/unauthorized blocks: 422; jobs empty', flush=True)
        for own, other in ((a, b), (b, a)):
            response = request(port, own['apiKey'], inputs[own['id']])
            clean(container)
            xml = verify(container, response, own, other, True)
            if own['id'] == 'KIG':
                hybrid = response[2]
                verify_web(container, port, own, sample, hybrid, xml, graphic_sample)
        gate = threading.Barrier(2)
        def simultaneous(profile):
            gate.wait(timeout=5)
            return request(port, profile['apiKey'], inputs[profile['id']])
        with ThreadPoolExecutor(max_workers=2) as pool:
            futures = [pool.submit(simultaneous, p) for p in profiles]
            for _ in range(20):
                if len(docker('exec', container, 'ls', '-A', '/jobs').splitlines()) == 2:
                    break
                if any(f.done() for f in futures):
                    raise RuntimeError('parallel request completed before overlap was verified')
                time.sleep(.1)
            else:
                raise RuntimeError('two concurrent job directories not observed')
            responses = [future.result() for future in futures]
        clean(container)
        for response, own, other in zip(responses, profiles, reversed(profiles)):
            verify(container, response, own, other, False)
        # Keep the upload open until its private job exists, then abort between chunks.
        with socket.create_connection(('127.0.0.1', port), timeout=5) as client:
            headers = ('POST /v1/convert HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: application/pdf\r\n'
                       + f'Content-Length: {len(sample)}\r\nX-Api-Key: {a["apiKey"]}\r\n\r\n').encode()
            client.sendall(headers + sample[:100])
            for _ in range(30):
                if docker('exec', container, 'ls', '-A', '/jobs').strip():
                    break
                time.sleep(.1)
            else:
                raise RuntimeError('partial upload did not start')
        for _ in range(30):
            if not docker('exec', container, 'ls', '-A', '/jobs').strip():
                break
            time.sleep(.1)
        clean(container)
        print('PASS aborted upload: jobs empty', flush=True)
    with service(args, profile_bytes, failure=True) as (container, port):
        problem(request(port, b['apiKey'], graphic_sample), 'tool_failed', 500)
        clean(container)
        print('PASS forced tool failure: 500, no PDF, jobs empty', flush=True)
    with service(args, profile_bytes, break_validation=True) as (container, port):
        checks = validation(request(port, a['apiKey'], hybrid, route='/v1/validate'), 'error')
        require(checks['PDF/A-3b (veraPDF)'] == 'error' and checks['ZUGFeRD-Hybridstruktur (Mustang)'] == 'passed',
                'technical error distinct from invalid document')
        clean(container)
        print('PASS technical validation failure: 503 with passed and failed tool status, jobs empty', flush=True)
    print(f'PASS HTTP acceptance total_seconds={time.monotonic() - started:.3f}', flush=True)


def main():
    p = argparse.ArgumentParser()
    p.add_argument('mode', choices=('test', 'serve', 'profiles'))
    p.add_argument('--publish', type=Path, default=ROOT / 'artifacts/http-publish')
    p.add_argument('--tools', type=Path, default=ROOT / '.tools')
    p.add_argument('--image', default=IMAGE)
    p.add_argument('--port', type=int, default=8080)
    p.add_argument('--profiles', type=Path)
    args = p.parse_args()
    if args.mode == 'profiles':
        require(args.profiles is not None and args.profiles.name.endswith('.local.json'), 'provide a new *.local.json file')
        with args.profiles.open('x', encoding='utf-8') as file:
            json.dump(test_profiles(), file, indent=2)
        return
    if args.mode == 'serve':
        require(args.profiles is not None, 'provide --profiles pointing to a private runtime configuration')
        with service(args, args.profiles.read_bytes(), args.port) as (_, port):
            print(f'InvoiceBridge http://127.0.0.1:{port}/v1/convert (Ctrl+C to stop)', flush=True)
            threading.Event().wait()
    else:
        test(args)


if __name__ == '__main__':
    try:
        main()
    except KeyboardInterrupt:
        pass
    except RuntimeError as error:
        print('FAIL ' + str(error), file=sys.stderr)
        sys.exit(1)
    except Exception as error:
        print('FAIL ' + type(error).__name__, file=sys.stderr)
        sys.exit(1)
