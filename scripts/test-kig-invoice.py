"""Local Windows regression for the narrow KIG layout, using invented fixtures."""
import argparse
from decimal import Decimal
from importlib.util import module_from_spec, spec_from_file_location
from io import BytesIO
import json
from pathlib import Path
import subprocess
import time
from xml.etree import ElementTree as ET

from pypdf import PdfReader, PdfWriter
from reportlab.pdfgen.canvas import Canvas

SPEC = spec_from_file_location('local_profiles', Path(__file__).with_name('test-local-profiles.py'))
LOCAL = module_from_spec(SPEC)
SPEC.loader.exec_module(LOCAL)
HTTP = LOCAL.HTTP
QR_SPEC = spec_from_file_location('pdf_girocode', Path(__file__).with_name('test-pdf-girocode.py'))
QR = module_from_spec(QR_SPEC)
QR_SPEC.loader.exec_module(QR)


def dutch_account(digits='0000000001'):
    bban = 'TSTX' + digits
    numeric = ''.join(char if char.isdigit() else str(ord(char)-55) for char in bban)
    return dict(iban=f'NL{98 - int(numeric + "232100") % 97:02d}{bban}', bic='TSTXNL00',
                beneficiary='Beispiel Test GmbH', bankName='Fiktive Testbank')


def fixture(bank, *, occupied=False, occupied_at=(444, 700)):
    output = BytesIO()
    canvas = Canvas(output, pagesize=(595, 842))
    canvas.setFont('Helvetica', 8)
    rows = [
        (64, 155, 'Beispiel Test GmbH · Musterstr. 1 · 12345 Testort'),
        (64, 171, 'Firma'), (64, 191, 'NetProof GmbH'), (64, 211, 'Musterstraße 2'),
        (64, 231, 'DE-23456 Musterort'), (64, 297, 'Rechnung'), (485, 297, '01.02.2026'),
        (64, 318, 'Rechnungsnr.: 20260201-00001'), (64, 338, 'Referenz: Musterauftrag'),
        (64, 391, 'Anzahl'), (104, 391, 'Artikel'), (396, 391, 'Einzelpreis/EUR'),
        (484, 391, 'Gesamtpreis/EUR'), (64, 413, '2'), (104, 413, 'Testdienst'),
        (440, 413, '28,00'), (524, 413, '56,00'), (104, 430, '50% vom Verkaufserlös'),
        (330, 458, 'Nettobetrag in EUR:'), (524, 458, '56,00'),
        (330, 473, 'zzgl. 19% Mwst.:'), (524, 473, '10,64'),
        (330, 488, 'Rechnungsbetrag in EUR:'), (524, 488, '66,64'),
        (64, 527, 'Der Rechnungsbetrag ist sofort fällig. Bitte überweisen Sie den Betrag auf folgendes Konto:'),
        (64, 542, f"Kontoinhaber: {bank['beneficiary']} · IBAN: {bank['iban']} · BIC: {bank['bic']}"),
        (64, 571, 'Umsatzsteuer-Identifikationsnummer: DE123456789'),
        (64, 787, 'Beispiel Test GmbH · Musterstrasse 1 · 12345 Testort'),
        (64, 800, 'Register Nr. 1000 · Steuernummer: 11/222/33333'),
    ]
    if occupied:
        rows.append((*occupied_at, 'KEEP'))
    for x, top, label in rows:
        canvas.drawString(x, 842 - top, label)
    canvas.save()
    return output.getvalue()


def two_pages(pdf):
    writer = PdfWriter()
    page = PdfReader(BytesIO(pdf)).pages[0]
    writer.add_page(page)
    writer.add_page(page)
    output = BytesIO()
    writer.write(output)
    return output.getvalue()


def convert(port, profile, pdf, *, qr=True):
    return HTTP.exchange(port, profile['apiKey'], pdf, route=f'/v1/convert?giroCode={str(qr).lower()}')[0]


def inspect_xml(pdf, bank, expected):
    reader = PdfReader(BytesIO(pdf))
    assert len(reader.pages) == 1, 'output page count'
    assert set(reader.attachments) == {'factur-x.xml'}, 'hybrid attachment'
    xml, = reader.attachments['factur-x.xml']
    tree = ET.fromstring(xml)
    for name, value in {'IBANID': bank['iban'], 'BICID': bank['bic'], 'AccountName': bank['beneficiary'],
                        'CategoryCode': 'S', 'TypeCode': '58', **expected}.items():
        values = {tag.text for tag in tree.findall('.//{*}' + name)}
        if name == 'TypeCode':
            assert tree.find('.//{*}SpecifiedTradeSettlementPaymentMeans/{*}TypeCode').text == value, name
        elif isinstance(value, Decimal):
            assert values and {Decimal(v) for v in values} == {value}, name
        else:
            assert values == {value}, name
    assert not tree.findall('.//{*}ActualDeliverySupplyChainEvent'), 'no delivery date inferred'
    return xml


def check_qr(publish, base, pdf, bank, amount, reference, qr):
    crop = QR.qr_crop(QR.render(pdf, 150, 'pdftoppm'), 150)
    if not qr:
        assert crop.getextrema() == (255, 255), 'QR area stays blank'
        return
    raster = base / 'qr.gray'
    try:
        raster.write_bytes(crop.tobytes())
        result = subprocess.run(['dotnet', str(publish.resolve() / 'invoicebridge.dll'), '--decode-qr-gray',
                                 str(raster), str(crop.width), str(crop.height)],
                                cwd=base, capture_output=True, timeout=15)
        assert result.returncode == 0, 'independent QR decoding'
        decoded = json.loads(result.stdout)
        expected = '\n'.join(('BCD', '002', '1', 'SCT', bank['bic'], bank['beneficiary'], bank['iban'],
                              f'EUR{amount:.2f}', '', '', reference))
        assert decoded['payload'] == expected and decoded['errorCorrection'] == 'M', 'EPC payload'
    finally:
        raster.unlink(missing_ok=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--publish', type=Path, default=LOCAL.ROOT/'artifacts/http-kig')
    parser.add_argument('--tools', type=Path, default=LOCAL.ROOT/'artifacts/Kig.InvoiceBridge-pre-checkin-clean-win-x64/tools')
    args = parser.parse_args()
    profiles, _ = LOCAL.real_mode_fixtures()
    kig = profiles[0]
    kig['bank'] = dutch_account()
    pdf = fixture(kig['bank'])
    bank11 = {**kig['bank'], 'bic': kig['bank']['bic'] + 'XXX'}
    expected = {
        'PaymentReference': '20260201-00001', 'LineTotalAmount': Decimal('56'), 'TaxBasisTotalAmount': Decimal('56'),
        'TaxTotalAmount': Decimal('10.64'), 'RateApplicablePercent': Decimal('19'),
        'GrandTotalAmount': Decimal('66.64'), 'DuePayableAmount': Decimal('66.64'),
    }

    def verify(port, base, profile, invoice, printed_bank, qr):
        started = time.perf_counter()
        response = convert(port, profile, invoice, qr=qr)
        elapsed = time.perf_counter() - started
        assert response[:2] == (200, 'application/pdf'), f'conversion ({qr}): {response[0]}'
        xml = inspect_xml(response[2], printed_bank, expected)
        checks = HTTP.request(port, profile['apiKey'], response[2], route='/v1/validate')
        HTTP.validation(checks, 'passed')
        check_qr(args.publish, base, response[2], printed_bank, expected['GrandTotalAmount'],
                 expected['PaymentReference'], qr)
        print(f'PASS profile BIC{len(profile["bank"]["bic"])}, printed BIC{len(printed_bank["bic"])}, '
              f'QR={qr}: XSD, Mustang, veraPDF, image, printed BIC in XML and applicable EPC '
              f'({elapsed:.2f} s conversion)', flush=True)
        return xml

    with LOCAL.local(args, profiles, 'local-real') as (port, base):
        for profile, bad, code, status in [
            (kig, fixture(dutch_account('0000000002')), 'payment_conflict', 422),
            (kig, fixture({**kig['bank'], 'bic': kig['bank']['bic'] + '012'}), 'payment_conflict', 422),
            (kig, fixture({**kig['bank'], 'bic': kig['bank']['bic'][:-1] + '1XXX'}), 'payment_conflict', 422),
            (kig, fixture(kig['bank'], occupied=True), 'unsupported_pdf', 422),
            (profiles[1], pdf, 'tenant_template_mismatch', 403),
            (kig, two_pages(pdf), 'unsupported_pdf', 422),
            (kig, b'%PDF-1.4\nunknown', 'unsupported_pdf', 422),
        ]:
            response = convert(port, profile, bad)
            assert response[0] == status, f'{code}: {response[0]} {response[2][:300]!r}'
            HTTP.problem(response, code, status)
        print('PASS conflicting IBAN, different BIC branch and base, occupied QR area, wrong tenant, collection, unknown PDF',
              flush=True)
        documents = [verify(port, base, kig, pdf, kig['bank'], qr) for qr in (True, False)]
        assert ET.tostring(ET.fromstring(documents[0])) == ET.tostring(ET.fromstring(documents[1])), 'same XML'
        verify(port, base, kig, fixture(bank11), bank11, True)
    kig11 = {**kig, 'bank': bank11}
    with LOCAL.local(args, [kig11, profiles[1]], 'local-real') as (port, base):
        verify(port, base, kig11, pdf, kig['bank'], True)
    print('PASS temporary files removed', flush=True)


if __name__ == '__main__':
    main()
