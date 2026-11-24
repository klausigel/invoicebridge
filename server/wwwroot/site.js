'use strict';
const by = id => document.getElementById(id);
const tabs = [...document.querySelectorAll('[role=tab]')];
const payment = by('payment');
const amount = payment.elements.namedItem('amount');
const fileInputs = ['convert-file', 'validate-file', 'payment-file'];
const maxFileSize = 1024 * 1024;
const labels = {beneficiary:'Empfänger', iban:'IBAN', amount:'Betrag', bic:'BIC', reference:'Verwendungszweck'};
let revision = 0, paymentRevision = 0, downloadUrl, confirmed, controller = new AbortController();

function showTab(index, focus) {
  tabs.forEach((tab, i) => {
    tab.setAttribute('aria-selected', String(i === index));
    tab.tabIndex = i === index ? 0 : -1;
    by(tab.getAttribute('aria-controls')).hidden = i !== index;
  });
  if (focus) tabs[index].focus();
}
tabs.forEach((tab, index) => {
  tab.addEventListener('click', () => showTab(index, false));
  tab.addEventListener('keydown', event => {
    const targets = {ArrowRight:(index+1)%tabs.length, ArrowLeft:(index+tabs.length-1)%tabs.length, Home:0, End:tabs.length-1};
    if (!(event.key in targets)) return;
    event.preventDefault();
    showTab(targets[event.key], true);
  });
});

function status(id, message = '', state = '') {
  const node = by(id);
  node.textContent = message;
  node.dataset.state = state;
}
function resetDownload() {
  if (downloadUrl) URL.revokeObjectURL(downloadUrl);
  downloadUrl = undefined;
  by('download').removeAttribute('href');
  by('download').hidden = true;
}
function resetChecks() {
  by('checks').replaceChildren();
  by('checks').hidden = true;
}
function resetConfirmation() {
  paymentRevision++;
  confirmed = undefined;
  by('confirmation').hidden = true;
  by('code').hidden = true;
  by('qr').removeAttribute('src');
  status('payment-status');
}
function hasFiles(event) {
  return event.dataTransfer?.types.includes('Files');
}
function fileMedia(input, file) {
  const name = file.name.toLowerCase();
  const unknown = !file.type || file.type === 'application/octet-stream';
  if (name.endsWith('.pdf') && (unknown || file.type === 'application/pdf')) return 'application/pdf';
  if (input.id === 'payment-file' && name.endsWith('.xml')
      && (unknown || file.type === 'application/xml' || file.type === 'text/xml')) return 'application/xml';
  return '';
}
function selectFile(input, files, dropped = false) {
  const message = input.id + '-message';
  const file = files[0];
  let error;
  if (files.length > 1) error = 'Bitte genau eine Datei auswählen.';
  else if (file && !fileMedia(input, file))
    error = input.id === 'payment-file' ? 'Bitte eine PDF- oder XML-Datei auswählen.' : 'Bitte eine PDF-Datei auswählen.';
  else if (file && (file.size === 0 || file.size > maxFileSize))
    error = 'Die Datei muss zwischen 1 Byte und 1 MiB groß sein.';
  if (error || !file) {
    input.value = '';
    status(message, error || '', error ? 'error' : '');
    return;
  }
  if (dropped) {
    const transfer = new DataTransfer();
    transfer.items.add(file);
    input.files = transfer.files;
  }
  status(message, 'Ausgewählt: ' + file.name);
}
for (const id of fileInputs) {
  const input = by(id);
  const area = input.closest('.upload');
  let depth = 0;
  input.addEventListener('change', () => selectFile(input, input.files));
  area.addEventListener('dragenter', event => {
    if (!hasFiles(event)) return;
    event.preventDefault();
    depth++;
    area.setAttribute('data-dragging', '');
  });
  area.addEventListener('dragover', event => {
    if (!hasFiles(event)) return;
    event.preventDefault();
    event.dataTransfer.dropEffect = 'copy';
  });
  area.addEventListener('dragleave', () => {
    depth = Math.max(0, depth - 1);
    if (!depth) area.removeAttribute('data-dragging');
  });
  area.addEventListener('drop', event => {
    depth = 0;
    area.removeAttribute('data-dragging');
    if (!hasFiles(event)) return;
    event.preventDefault();
    selectFile(input, event.dataTransfer.files, true);
  });
}
for (const type of ['dragover', 'drop'])
  window.addEventListener(type, event => {
    if (hasFiles(event)) event.preventDefault();
  });

// The editable text is German; API amounts are canonical decimal strings.
// Convert digit strings directly so that no rounding or separator guessing is needed.
function canonicalAmount(text, german) {
  const pattern = german
    ? /^(\d{1,9}|\d{1,3}(?:\.\d{3}){1,2})(?:,(\d{1,2}))?$/
    : /^(\d{1,9})(?:\.(\d{1,2})0*)?$/;
  const parts = pattern.exec(text.trim());
  if (!parts) return null;
  const whole = parts[1].replace(/\./g, '').replace(/^0+(?=\d)/, '');
  return whole + '.' + (parts[2] || '').padEnd(2, '0');
}
function formatAmount(canonical) {
  const [whole, cents] = canonical.split('.');
  return whole.replace(/\B(?=(\d{3})+(?!\d))/g, '.') + ',' + cents;
}
function amountMessage(message = '') {
  status('amount-message', message, message ? 'error' : '');
  if (message) amount.setAttribute('aria-invalid', 'true');
  else amount.removeAttribute('aria-invalid');
}
function finishAmount() {
  const canonical = canonicalAmount(amount.value, true);
  if (canonical) amount.value = formatAmount(canonical);
  amountMessage(!canonical && amount.value.trim()
    ? 'Bitte deutschen Betrag eingeben, z. B. 1.234,56.' : '');
  return canonical;
}
amount.addEventListener('blur', finishAmount);
amount.addEventListener('input', () => amountMessage());

function resetWork() {
  revision++;
  controller.abort();
  controller = new AbortController();
  resetDownload();
  resetChecks();
  for (const id of fileInputs) selectFile(by(id), []);
  payment.reset();
  amountMessage();
  resetConfirmation();
  for (const id of ['convert-status','validate-status','import-status','session-status']) status(id);
  showTab(0, false);
}
payment.addEventListener('input', resetConfirmation);

async function post(route, body, media) {
  if (body instanceof File && (body.size === 0 || body.size > maxFileSize))
    throw new Error('Bitte eine Datei zwischen 1 Byte und 1 MiB auswählen.');
  const current = revision;
  const response = await fetch(route, {method:'POST', headers:{'Content-Type':media},
    body, credentials:'same-origin', cache:'no-store', signal:controller.signal});
  if (response.status === 401 && current === revision)
    showLogin('Die Sitzung ist abgelaufen. Bitte erneut anmelden.');
  return response;
}

const signIn = by('sign-in');
const workspace = by('workspace');
const session = by('session');
const logout = by('logout');
function showLogin(message = '', focus = true) {
  resetWork();
  by('session-loading').hidden = true;
  session.hidden = true;
  workspace.hidden = true;
  by('tenant').textContent = '';
  signIn.replaceChildren(by('sign-in-template').content.cloneNode(true));
  signIn.hidden = false;
  const key = by('key');
  status('login-status', message, message ? 'error' : '');
  if (focus) key.focus();
  signIn.querySelector('form').addEventListener('submit', async event => {
    event.preventDefault();
    const button = signIn.querySelector('button');
    const body = JSON.stringify({apiKey:key.value.trim()});
    key.value = '';
    button.disabled = true;
    status('login-status','Anmeldung läuft …');
    try {
      const response = await fetch('/v1/session/login', {method:'POST', body,
        headers:{'Content-Type':'application/json'}, credentials:'same-origin', cache:'no-store'});
      const data = await json(await checked(response));
      showWorkspace(data.tenant, true);
    } catch (error) {
      status('login-status',error instanceof TypeError ? 'Der lokale Dienst ist nicht erreichbar.' : error.message,'error');
      key.focus();
    } finally { button.disabled = false; }
  });
}
function showWorkspace(tenant, focus) {
  by('session-loading').hidden = true;
  signIn.hidden = true;
  signIn.replaceChildren();
  by('tenant').textContent = tenant;
  workspace.hidden = false;
  session.hidden = false;
  if (focus) by('workspace-title').focus();
}
logout.addEventListener('click', async () => {
  logout.disabled = true;
  try {
    await checked(await fetch('/v1/session/logout', {method:'POST', credentials:'same-origin', cache:'no-store'}));
    showLogin();
  } catch {
    status('session-status','Abmelden fehlgeschlagen. Bitte erneut versuchen.','error');
  } finally { logout.disabled = false; }
});
async function restoreSession() {
  try {
    const response = await fetch('/v1/session', {credentials:'same-origin', cache:'no-store'});
    const data = await json(response);
    if (response.ok && data?.tenant) showWorkspace(data.tenant, false);
    else if (response.status === 401) showLogin(data?.code === 'session_expired' ? data.title : '');
    else throw new Error();
  } catch { showLogin('Die Sitzung konnte nicht geprüft werden. Ist der lokale Dienst erreichbar?'); }
}
async function json(response) {
  return response.headers.get('content-type')?.includes('json') ? await response.json() : null;
}
async function checked(response) {
  if (!response.ok) throw new Error((await json(response))?.title || 'Die Anfrage konnte nicht verarbeitet werden.');
  return response;
}
async function busy(button, output, message, work) {
  const current = revision;
  button.disabled = true;
  status(output, message);
  try { await work(() => current === revision); }
  catch (error) {
    if (current === revision && error.name !== 'AbortError')
      status(output, error instanceof TypeError ? 'Der lokale Dienst ist nicht erreichbar.' : error.message, 'error');
  } finally { button.disabled = false; }
}
function submit(id, handler) {
  by(id).addEventListener('submit', event => {
    event.preventDefault();
    handler(event.submitter || by(id).querySelector('[type=submit]'));
  });
}

submit('convert', button => {
  resetDownload();
  const file = by('convert-file').files[0];
  busy(button,'convert-status','Datei wird umgewandelt und geprüft …', async current => {
    const result = await checked(await post('/v1/convert',file,'application/pdf'));
    if (!result.headers.get('content-type')?.startsWith('application/pdf')) throw new Error('Der Dienst hat keine PDF geliefert.');
    const pdf = await result.blob();
    if (!current()) return;
    downloadUrl = URL.createObjectURL(pdf);
    by('download').href = downloadUrl;
    by('download').hidden = false;
    status('convert-status','Die geprüfte ZUGFeRD-PDF ist zum Download bereit.','ok');
  });
});
submit('validate', button => {
  resetChecks();
  const file = by('validate-file').files[0];
  busy(button,'validate-status','Formale Prüfungen laufen …', async current => {
    const response = await post('/v1/validate',file,'application/pdf');
    const result = await json(response);
    if (!result?.checks) throw new Error(result?.title || 'Die Prüfung konnte nicht gestartet werden.');
    if (!current()) return;
    const states = {passed:'Bestanden',invalid:'Nicht bestanden',error:'Technischer Fehler',pending:'Nicht ausgeführt'};
    for (const check of result.checks) {
      const item = document.createElement('li'), state = document.createElement('b');
      state.textContent = states[check.state] || states.pending;
      item.append(check.name,state);
      by('checks').append(item);
    }
    by('checks').hidden = false;
    const messages = {passed:'Die Rechnung hat alle formalen Prüfungen bestanden.',invalid:'Die Datei hat die formale Prüfung nicht bestanden.',
      error:'Die Prüfung konnte technisch nicht vollständig ausgeführt werden.'};
    status('validate-status',messages[result.status],result.status === 'passed' ? 'ok' : 'error');
  });
});
submit('import', button => {
  const input = by('payment-file');
  const file = input.files[0];
  payment.reset();
  amountMessage();
  resetConfirmation();
  const version = paymentRevision;
  const media = fileMedia(input, file);
  busy(button,'import-status','Zahlungsdaten werden gelesen …', async current => {
    const result = await json(await checked(await post('/v1/payment/read',file,media)));
    if (!current()) return;
    if (version !== paymentRevision) {
      status('import-status','Manuelle Änderungen wurden beibehalten. Die Datei kann erneut gelesen werden.');
      return;
    }
    for (const name of Object.keys(labels)) payment.elements.namedItem(name).value = result.fields[name] ?? '';
    const imported = canonicalAmount(String(result.fields.amount ?? ''), false);
    amount.value = imported ? formatAmount(imported) : '';
    if (!imported && result.fields.amount) amountMessage('Der Rechnungsbetrag konnte nicht eindeutig übernommen werden.');
    const incomplete = ['beneficiary','iban','amount','reference'].some(name => !payment.elements.namedItem(name).value);
    status('import-status', incomplete ? 'Lesbare Angaben übernommen. Bitte die leeren Pflichtfelder ergänzen.'
      : 'Zahlungsdaten übernommen. Bitte vor dem Erzeugen prüfen.','ok');
  });
});

function review(data) {
  const list = by('payment-review');
  list.replaceChildren();
  for (const [name,label] of Object.entries(labels)) {
    const term = document.createElement('dt'), detail = document.createElement('dd');
    term.textContent = label;
    detail.textContent = name === 'amount' ? formatAmount(data[name])+' EUR' : data[name] || '–';
    list.append(term,detail);
  }
}
submit('payment', () => {
  resetConfirmation();
  const canonical = finishAmount();
  const data = Object.fromEntries([...new FormData(payment)].map(([name,value]) => [name,value.trim()]));
  if (!canonical) {
    amount.focus();
    return;
  }
  data.amount = canonical;
  data.iban = data.iban.replace(/\s/g,'').toUpperCase();
  data.bic = data.bic.replace(/\s/g,'').toUpperCase();
  confirmed = data;
  review(data);
  by('confirmation').hidden = false;
  by('make-code').focus();
});
const qr = by('qr');
const code = by('code');
function sizeQr() {
  if (!qr.naturalWidth || code.hidden) return;
  const style = getComputedStyle(code);
  const available = code.clientWidth - parseFloat(style.paddingLeft) - parseFloat(style.paddingRight);
  const pixels = Math.max(1, Math.floor(available / qr.naturalWidth)) * qr.naturalWidth;
  if (qr.width !== pixels) qr.width = pixels;
}
qr.addEventListener('load', sizeQr);
new ResizeObserver(sizeQr).observe(code);
by('make-code').addEventListener('click', event => {
  if (!confirmed) return;
  const data = confirmed;
  const version = paymentRevision;
  by('code').hidden = true;
  busy(event.currentTarget,'payment-status','GiroCode wird erzeugt …', async current => {
    const result = await json(await checked(await post('/v1/payment/qr',JSON.stringify(data),'application/json')));
    if (!current() || version !== paymentRevision) return;
    review(result.payment);
    by('qr').src = 'data:image/png;base64,' + result.png;
    by('code').hidden = false;
    status('payment-status','GiroCode erstellt.','ok');
  });
});
window.addEventListener('pagehide', () => {
  resetWork();
  const field = by('key');
  if (field) field.value = '';
});
window.addEventListener('pageshow', event => {
  if (event.persisted) {
    workspace.hidden = true;
    session.hidden = true;
    restoreSession();
  }
});
restoreSession();
