"""Launch and exercise InvoiceBridge HTTP without baking any inputs into an image."""
import argparse
from concurrent.futures import ThreadPoolExecutor
from contextlib import contextmanager
from http.client import HTTPConnection
import io
import json
from pathlib import Path
import re
import secrets
import socket
import string
import subprocess
import sys
import tarfile
import threading
import time
import uuid
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


def request(port, key=None, pdf=None, media='application/pdf'):
    headers = {'Content-Type': media}
    if key is not None:
        headers['X-Api-Key'] = key
    client = HTTPConnection('127.0.0.1', port, timeout=130)
    try:
        client.request('GET' if pdf is None else 'POST', '/healthz' if pdf is None else '/v1/convert', pdf, headers)
        result = client.getresponse()
        return result.status, result.getheader('Content-Type', ''), result.read()
    finally:
        client.close()


@contextmanager
def service(args, profiles, port=0, failure=False):
    container = 'invoicebridge-http-' + uuid.uuid4().hex
    docker('run', '-d', '--pull', 'never', '--name', container, '--read-only', '--log-driver', 'none',
           '--tmpfs', '/scratch:rw,exec,mode=1777,size=256m', '--tmpfs', '/jobs:rw,noexec,mode=1777,size=128m',
           '--tmpfs', '/tmp:rw,noexec,mode=1777,size=32m', '--user', '1654:1654', '--cap-drop', 'ALL',
           '--security-opt', 'no-new-privileges', '--publish', f'127.0.0.1:{port or ""}:8080',
           '-e', 'HOME=/scratch', '-e', 'TMPDIR=/jobs', '-e', 'DOTNET_EnableDiagnostics=0', '-e', 'INVOICEBRIDGE_CONTAINER=1',
           '-e', 'INVOICEBRIDGE_PROFILES=/scratch/profiles.json', '-e', 'INVOICEBRIDGE_TOOLS=/scratch/tools',
           *(['-e', 'INVOICEBRIDGE_GHOSTSCRIPT=/usr/bin/false'] if failure else []), args.image, 'sleep', 'infinity')
    try:
        files = {'tools/' + name: args.tools / name for name in (MUSTANG, VERA)}
        for file in args.publish.iterdir():
            if file.is_file() and (file.suffix == '.dll' or file.name.endswith(('.deps.json', '.runtimeconfig.json'))):
                files['app/' + file.name] = file
        require('app/InvoiceBridge.Http.dll' in files, 'HTTP publish missing')
        put(container, files, {'profiles.json': profiles})
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


def conflict_pdf(iban):
    stream = f'BT /F1 12 Tf 40 780 Td (IBAN: {iban}) Tj ET'.encode('ascii')
    objects = [b'<< /Type /Catalog /Pages 2 0 R >>', b'<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
               b'<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>',
               b'<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>',
               b'<< /Length %d >>\nstream\n'%len(stream) + stream + b'\nendstream']
    pdf = bytearray(b'%PDF-1.4\n')
    offsets = [0]
    for n, body in enumerate(objects, 1):
        offsets.append(len(pdf))
        pdf.extend(f'{n} 0 obj\n'.encode() + body + b'\nendobj\n')
    xref = len(pdf)
    pdf.extend(b'xref\n0 6\n0000000000 65535 f \n')
    for offset in offsets[1:]:
        pdf.extend(f'{offset:010d} 00000 n \n'.encode())
    pdf.extend(f'trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n'.encode())
    return bytes(pdf)


def clean(container):
    require(not docker('exec', container, 'ls', '-A', '/jobs').strip(), 'temporary jobs not removed')


def problem(response, code, status):
    require(response[0] == status and response[1].startswith('application/problem+json')
            and json.loads(response[2]).get('code') == code, f'expected HTTP {status}: {code}')


def verify(container, response, profile, other, reference, independent):
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
        for name in ('LineTotalAmount', 'TaxBasisTotalAmount', 'TaxTotalAmount', 'GrandTotalAmount',
                     'TotalPrepaidAmount', 'DuePayableAmount', 'RateApplicablePercent', 'CategoryCode'):
            require(bool(values(reference, name)) and values(root, name) == values(reference, name), 'financial field ' + name)
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
    finally:
        docker('exec', container, 'rm', '-f', path, extracted)


def test(args):
    profiles = test_profiles()
    profile_bytes = json.dumps(profiles).encode()
    a, b = profiles
    sample = args.sample.read_bytes()
    reference = ET.parse(args.reference).getroot()
    started = time.monotonic()
    with service(args, profile_bytes) as (container, port):
        clean(container)
        for key in (None, secrets.token_urlsafe(36)):
            problem(request(port, key, sample), 'api_key_required', 401)
            clean(container)
        problem(request(port, a['apiKey'], conflict_pdf(b['bank']['iban'])), 'payment_conflict', 422)
        clean(container)
        print('PASS missing and invalid key: 401; readable account conflict: 422; jobs empty', flush=True)
        for own, other in ((a, b), (b, a)):
            response = request(port, own['apiKey'], sample)
            clean(container)
            verify(container, response, own, other, reference, True)
        gate = threading.Barrier(2)
        def simultaneous(profile):
            gate.wait(timeout=5)
            return request(port, profile['apiKey'], sample)
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
            verify(container, response, own, other, reference, False)
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
        problem(request(port, b['apiKey'], sample), 'tool_failed', 500)
        clean(container)
        print('PASS forced tool failure: 500, no PDF, jobs empty', flush=True)
    print(f'PASS HTTP acceptance total_seconds={time.monotonic() - started:.3f}', flush=True)


def main():
    p = argparse.ArgumentParser()
    p.add_argument('mode', choices=('test', 'serve', 'profiles'))
    p.add_argument('--publish', type=Path, default=ROOT / 'artifacts/http-publish')
    p.add_argument('--tools', type=Path, default=ROOT / '.tools')
    p.add_argument('--image', default=IMAGE)
    p.add_argument('--port', type=int, default=8080)
    p.add_argument('--profiles', type=Path)
    p.add_argument('--sample', type=Path)
    p.add_argument('--reference', type=Path)
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
        require(args.sample is not None and args.reference is not None, 'provide --sample and --reference')
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
