"""Start a private Windows test process; fixtures cannot be supplied from real invoices."""
import argparse
from contextlib import contextmanager
import hashlib
from importlib.util import spec_from_file_location, module_from_spec
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import time
from xml.etree import ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent
SPEC = spec_from_file_location('http_tests', ROOT / 'scripts/http-container.py')
HTTP = module_from_spec(SPEC)
SPEC.loader.exec_module(HTTP)


def real_mode_fixtures():
    profiles = HTTP.test_profiles()
    for i, profile in enumerate(profiles, 1):
        # Invented, nonzero test BLZs satisfy local-real syntax, not account verification.
        bban = f'{i:08d}{i:010d}'
        check = 98 - int(bban + '131400') % 97
        profile['bank']['iban'] = f'DE{check:02d}{bban}'
    graphic = HTTP.fixture(graphic=True)
    profiles[1]['approvedGraphicSha256'] = hashlib.sha256(graphic).hexdigest()
    return profiles, graphic


def unused_port():
    with socket.socket(socket.AF_INET, socket.SOCK_STREAM) as s:
        s.bind(('127.0.0.1', 0))
        return s.getsockname()[1]


@contextmanager
def local(args, profiles, mode, *, bad_binding=False, container=False, reject=False):
    with tempfile.TemporaryDirectory(prefix='invoicebridge-profile-test-') as folder:
        base = Path(folder)
        file = base / 'fixture.local.json'
        file.write_text(json.dumps(profiles), encoding='utf-8')
        port = unused_port()
        env = {k:v for k,v in os.environ.items() if not k.upper().startswith(('INVOICEBRIDGE_', 'ASPNETCORE_URLS',
                'DOTNET_URLS', 'KESTREL__ENDPOINTS', 'ASPNETCORE_KESTREL__ENDPOINTS', 'DOTNET_RUNNING_IN_CONTAINER'))}
        env.update(INVOICEBRIDGE_PROFILE_MODE=mode, INVOICEBRIDGE_PROFILES=str(file),
                   INVOICEBRIDGE_TOOLS=str(args.tools.resolve()), TMP=str(base), TEMP=str(base), TMPDIR=str(base),
                   ASPNETCORE_URLS=f'http://{"0.0.0.0" if bad_binding else "127.0.0.1"}:{port}')
        if container:
            env['INVOICEBRIDGE_CONTAINER'] = '1'
        proc = subprocess.Popen(['dotnet', str(args.publish.resolve() / 'InvoiceBridge.Http.dll')],
                                env=env, cwd=base, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                                creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        try:
            if reject:
                assert proc.wait(timeout=10) == 2, 'invalid startup was not rejected'
                yield port, base
                return
            for _ in range(50):
                assert proc.poll() is None, 'local server exited during startup'
                try:
                    if HTTP.request(port)[0] == 204:
                        break
                except OSError:
                    pass
                time.sleep(.1)
            else:
                raise RuntimeError('alternate port not available')
            yield port, base
            assert not list(base.glob('invoicebridge-http-*')), 'request temp files survived'
        finally:
            if proc.poll() is None:
                proc.terminate()
                proc.wait(timeout=10)


def login(port, profile):
    body = json.dumps({'apiKey':profile['apiKey']}).encode()
    origin = f'http://127.0.0.1:{port}'
    reply, headers = HTTP.exchange(port, pdf=body, media='application/json', route='/v1/session/login',
                                    extra={'Origin': origin})
    assert reply[0] == 200 and json.loads(reply[2]) == {'tenant':profile['id']}
    cookie = headers['Set-Cookie'].split(';')[0]
    status, _, data = HTTP.exchange(port, route='/v1/session', extra={'Cookie':cookie})[0]
    assert status == 200 and json.loads(data) == {'tenant':profile['id']}
    return {'Origin':origin, 'Cookie':cookie}


def read_output(args, base, result, profile):
    assert result[0] == 200 and result[1] == 'application/pdf', f'conversion HTTP {result[0]}'
    pdf = base / 'result.pdf'
    xml = base / 'result.xml'
    pdf.write_bytes(result[2])
    tools = args.tools.resolve()
    java = tools / 'java/bin/java.exe'
    result = subprocess.run([str(java), f'-Djava.io.tmpdir={base}', '-jar', str(tools / HTTP.MUSTANG),
                             '--action', 'extract', '--source', str(pdf), '--out', str(xml), '--disable-file-logging'],
                            cwd=base, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=45)
    assert result.returncode == 0, 'result XML extraction'
    tree = ET.parse(xml).getroot()
    for name, value in (('IBANID',profile['bank']['iban']), ('BICID',profile['bank']['bic']),
                        ('AccountName',profile['bank']['beneficiary']), ('GrandTotalAmount','456.01'), ('CategoryCode','S')):
        assert {tag.text for tag in tree.findall('.//{*}'+name)} == {value}, 'result XML mismatch: '+name
    pdf.unlink()
    xml.unlink()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--publish', type=Path, default=ROOT/'artifacts/http-real-mode')
    parser.add_argument('--tools', type=Path, default=ROOT/'artifacts/Kig.InvoiceBridge-pre-checkin-clean-win-x64/tools')
    args = parser.parse_args()
    profiles, graphic = real_mode_fixtures()
    for values, mode, options in [
        (profiles,'synthetic',{}), (HTTP.test_profiles(),'local-real',{}),
        (profiles,'local-real',{'bad_binding':True}), (profiles,'local-real',{'container':True}),
    ]:
        with local(args, values, mode, reject=True, **options):
            pass
    print('PASS synthetic/real profile guards, real-mode container and non-loopback rejection', flush=True)
    with local(args, profiles, 'local-real') as (port, base):
        assert HTTP.request(port, route='/')[0] == 200, 'home on configured port'
        for file in ('tenants.real.local.json', 'tenants.example.json', 'fixture.local.json'):
            assert HTTP.request(port, route='/'+file)[0] == 404, 'private config publicly reachable'
        for profile, alternate in zip(profiles, reversed(profiles)):
            headers = login(port, profile)
            synthetic = HTTP.fixture([profile['bank']]) if profile['id'] == 'KIG' else graphic
            result = HTTP.exchange(port, pdf=synthetic, extra=headers)[0]
            read_output(args, base, result, profile)
            HTTP.problem(HTTP.exchange(port, pdf=HTTP.fixture([alternate['bank']]), extra=headers)[0], 'payment_conflict', 422)
            HTTP.problem(HTTP.exchange(port, pdf=synthetic, extra={**headers,'Origin':f'http://127.0.0.1:{unused_port()}'} )[0],
                         'cross_site_request', 403)
            HTTP.problem(HTTP.exchange(port, pdf=graphic + b'% unknown', extra=headers)[0], 'payment_unclear', 422)
            HTTP.problem(HTTP.exchange(port, pdf=HTTP.fixture([profile['bank']], seller='Anderer Aussteller Testweg 1 12345 Testort'),
                                       extra=headers)[0], 'unsupported_pdf', 422)
            print('PASS',profile['id'],'alternate port, session, approved conversion, XML, bank conflict, unknown graphic and supplier rejected',flush=True)
    profiles[0]['bank']['beneficiary'] = 'Synthetischer anderer Kontoinhaber'
    with local(args, profiles, 'local-real') as (port, base):
        headers = login(port, profiles[0])
        result = HTTP.exchange(port,pdf=HTTP.fixture([profiles[0]['bank']]),extra=headers)[0]
        HTTP.problem(result,'unsupported_pdf',422)
        assert 'Kontoinhaber' in json.loads(result[2])['title'], 'separate seller diagnostic'
    print('PASS local-real profile permits login with another holder; sample parser rejects unsupported holder', flush=True)


if __name__ == '__main__':
    try:
        main()
    except AssertionError as error:
        print('FAIL', str(error), file=sys.stderr)
        sys.exit(1)
