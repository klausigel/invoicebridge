#!/usr/bin/env python3
"""Exercise the published CLI and validate the final PDF on each platform."""

import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import time
import uuid
from xml.etree import ElementTree as ET


PROJECT = Path(__file__).resolve().parent.parent
PDF = 'Rechnung_112684_ZUGFeRD.pdf'
SAMPLE = 'sample_112684.pdf'
MUSTANG = 'Mustang-CLI-2.26.0.jar'
VERA = 'verapdf/bin/cli-1.30.3.jar'
IMAGE = 'sha256:1e077b0bd53740001af848a7dccfb82d04365664263ed9d50755f72d71f36ed8'
DOCKER = ['docker', '--context', 'desktop-linux']


def require(ok, stage):
    if not ok:
        raise RuntimeError(stage)


def run(command, work, extra=None):
    env = {**os.environ, 'TMP': str(work), 'TEMP': str(work), 'TMPDIR': str(work), **(extra or {})}
    return subprocess.run(command, cwd=work, env=env, capture_output=True, timeout=240)


def xml_tree(node):
    return (node.tag, sorted(node.attrib.items()), (node.text or '').strip(),
            [xml_tree(child) for child in node])


def checks(app, sample, config, reference, tools, workspace):
    started = time.monotonic()
    windows = os.name == 'nt'
    java = str(tools / 'java/bin/java.exe') if windows else '/usr/bin/java'
    faulty = str(Path(os.environ['SystemRoot']) / 'System32/where.exe') if windows else '/usr/bin/false'
    bank = json.loads(config.read_text(encoding='utf-8-sig'))['Bank']
    with tempfile.TemporaryDirectory(prefix='invoicebridge-regression-', dir=workspace) as temp:
        work = Path(temp)
        inputs = work / 'invoices'
        inputs.mkdir()
        shutil.copyfile(sample, inputs / SAMPLE)
        if windows:
            # Exercise the deployed apphost with its usual adjacent default config.
            app = shutil.copytree(app, work / 'windows-app')
        converter = [str(app / 'invoicebridge.exe')] if windows else ['dotnet', str(app / 'invoicebridge.dll')]
        for failure in (False, True):
            output = work / ('failure' if failure else 'success')
            output.mkdir()
            settings = dict(SourceDirectory=str(inputs), OutputDirectory=str(output),
                            ToolsDirectory=str(tools), SourceFileSelection='InvoiceNumberToken', Bank=bank)
            if failure:
                settings['GhostscriptExecutable'] = faulty
            use_default_config = windows and not failure
            settings_file = (app if use_default_config else work) / 'invoicebridge.json'
            settings_file.write_text(json.dumps(settings), encoding='utf-8')
            arguments = ['112684'] if use_default_config else ['112684', '--config', str(settings_file)]
            call = run(converter + arguments, work)
            statuses = call.stdout.decode('utf-8', errors='replace').splitlines()
            if failure:
                message = call.stderr.decode('utf-8', errors='replace')
                require(call.returncode == 21 and 'Werkzeugschritt ghostscript fehlgeschlagen' in message,
                        f'forced-converter-error exit={call.returncode}')
                require('PASS xsd' in statuses and 'PASS xml-validation' in statuses, 'failure-stage')
                require(not list(output.iterdir()), 'failure-cleanup')
                print('PASS forced-ghostscript-error exit=21, no final PDF or work files', flush=True)
                continue
            require(call.returncode == 0, f'conversion exit={call.returncode}, passed={len(statuses)}')
            for status in ('xsd', 'xml-validation', 'hybrid-validation', 'xml-roundtrip-and-fields', 'rendered-qr-and-image'):
                require('PASS ' + status in statuses, status)
                print('PASS ' + status, flush=True)
            duration = next((line for line in statuses if re.fullmatch(r'OK conversion_seconds=\d+\.\d+', line)), None)
            require(duration is not None, 'conversion-timing')
            print(duration, flush=True)
            final = output / PDF
            require(set(output.iterdir()) == {final} and final.is_file(), 'success-cleanup')
            print('PASS success-cleanup (only final PDF)', flush=True)

            jvm = [java, f'-Djava.io.tmpdir={work}']
            exported = work / 'extracted.xml'
            result = run(jvm + ['-jar', str(tools / MUSTANG), '--action', 'extract', '--source', str(final),
                                '--out', str(exported), '--disable-file-logging'], work)
            require(result.returncode == 0 and exported.is_file(), 'final-pdf-xml-extraction')
            require(xml_tree(ET.parse(exported).getroot()) == xml_tree(ET.parse(reference).getroot()),
                    'windows-r4-xml-comparison')
            print('PASS final-pdf XML extraction and complete Windows r4 XML comparison', flush=True)
            report = run(jvm + ['-XX:+IgnoreUnrecognizedVMOptions', '-classpath', str((tools / VERA).parent / '*'),
                                 '--add-exports=java.base/sun.security.pkcs=ALL-UNNAMED',
                                 'org.verapdf.apps.GreenfieldCliWrapper', '--format', 'xml', '--flavour', '3b', str(final)], work)
            require(report.returncode == 0 and b'<' in report.stdout, 'final-pdf-verapdf-execution')
            result_xml = ET.fromstring(report.stdout[report.stdout.index(b'<'):])
            results = result_xml.findall('.//validationReport')
            require(len(results) == 1 and results[0].get('isCompliant') == 'true', 'final-pdf-pdfa-3b')
            details = results[0].find('details')
            require(details is not None, 'final-pdf-verapdf-counts')
            print('PASS final-pdf PDF/A-3b rules=' + str(int(details.get('passedRules')))
                  + ' checks=' + str(int(details.get('passedChecks'))), flush=True)
    require(not work.exists(), 'test-fixture-cleanup')
    print(f'PASS complete regression_seconds={time.monotonic() - started:.3f}', flush=True)


def linux(app, source, config, reference, tools, image):
    started = time.monotonic()
    name = 'invoicebridge-regression-' + uuid.uuid4().hex
    invoke = DOCKER + ['run', '-d', '--name', name, '--pull', 'never', '--network', 'none', '--read-only',
                       '--log-driver', 'none', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges',
                       '--tmpfs', '/scratch:rw,exec,mode=1777,size=256m', '--tmpfs', '/tmp:rw,mode=1777,size=64m',
                       '--user', '1654:1654', '-e', 'HOME=/scratch', image, 'sleep', '600']
    subprocess.run(invoke, stdout=subprocess.DEVNULL, check=True)
    try:
        command = DOCKER + ['exec', '-i', name, 'tar', '-xf', '-', '-C', '/scratch']
        with subprocess.Popen(command, stdin=subprocess.PIPE) as transfer:
            with tarfile.open(fileobj=transfer.stdin, mode='w|') as archive:
                for file in app.iterdir():
                    if file.is_file() and (file.suffix == '.dll' or file.name.endswith(('.deps.json', '.runtimeconfig.json'))):
                        archive.add(file, 'app/' + file.name)
                archive.add(source, 'input/' + SAMPLE)
                archive.add(config, 'invoicebridge.json')
                archive.add(reference, 'reference.xml')
                archive.add(tools / MUSTANG, 'tools/' + MUSTANG)
                archive.add(tools / VERA, 'tools/' + VERA)
                archive.add(Path(__file__), 'test-platforms.py')
            transfer.stdin.close()
            require(transfer.wait(timeout=60) == 0, 'container-input-transfer')
        subprocess.run(DOCKER + ['exec', name, 'python3', '/scratch/test-platforms.py', '--platform', 'inside'], check=True)
    finally:
        subprocess.run(DOCKER + ['rm', '-f', name], stdout=subprocess.DEVNULL, check=True)
    print(f'PASS container_total_seconds={time.monotonic() - started:.3f}', flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--platform', choices=('windows', 'linux', 'inside'), required=True)
    parser.add_argument('--publish', type=Path, default=PROJECT / 'artifacts/cross-platform-publish')
    parser.add_argument('--image', default=IMAGE)
    parser.add_argument('--sample', type=Path, help='Local, approved sample PDF')
    parser.add_argument('--reference', type=Path, default=PROJECT / 'artifacts/review-result-20261102-r4' / Path(PDF).with_suffix('.evidence') / 'extracted.xml')
    parser.add_argument('--config', type=Path, default=PROJECT / 'invoicebridge.json')
    parser.add_argument('--tools', type=Path, help='Local validator/tool directory')
    args = parser.parse_args()
    if args.platform == 'inside':
        ram = Path('/scratch')
        checks(ram / 'app', ram / 'input' / SAMPLE, ram / 'invoicebridge.json', ram / 'reference.xml', ram / 'tools', ram)
        return
    if args.sample is None:
        candidates = [p for p in (PROJECT.parent / 'examples').glob('*') if p.is_file()
                      and p.suffix.lower() == '.pdf' and re.search(r'(?<!\d)112684(?!\d)', p.stem)]
        require(len(candidates) == 1, 'provide exactly one local sample with --sample')
        args.sample = candidates[0]
    source, reference, config = (p.resolve() for p in (args.sample, args.reference, args.config))
    for path, label in ((source, 'sample'), (reference, 'reference'), (config, 'config')):
        require(path.is_file(), f'local {label} missing; provide --{label}')
    default_tools = (PROJECT / 'artifacts/Kig.InvoiceBridge-20261102-r4-win-x64/tools'
                     if args.platform == 'windows' else PROJECT / '.tools')
    tools = (args.tools or default_tools).resolve()
    require((tools / MUSTANG).is_file() and (tools / VERA).is_file(), 'local validators missing; provide --tools')
    if args.platform == 'windows':
        require(os.name == 'nt', 'windows-runner-required')
        checks(args.publish.resolve(), source, config, reference, tools, None)
    else:
        linux(args.publish.resolve(), source, config, reference, tools, args.image)


if __name__ == '__main__':
    try:
        main()
    except RuntimeError as error:
        print('FAIL ' + str(error), file=sys.stderr)
        sys.exit(1)
    except Exception as error:
        print('FAIL ' + type(error).__name__, file=sys.stderr)
        sys.exit(1)
