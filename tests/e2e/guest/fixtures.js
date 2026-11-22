// Guest-side fixtures for tests/e2e/ui-smoke.ps1 -DevSource. Runs inside Windows Sandbox with the host's node.exe.
//
//   node fixtures.js serve <folder> <port>          serve <folder> on http://127.0.0.1:<port>/ (the dev collection's audio)
//   node fixtures.js container <blobEndpoint> <name> create a public-read container in Azurite (well-known dev key)
//
// Loopback only: nothing listens on another interface and nothing leaves the guest.
'use strict';
const http = require('http');
const fs = require('fs');
const path = require('path');
const crypto = require('crypto');

const [mode, a, b] = process.argv.slice(2);

if (mode === 'serve') {
  const root = path.resolve(a);
  http.createServer((req, res) => {
    const file = path.join(root, decodeURIComponent(new URL(req.url, 'http://x').pathname));
    if (!file.startsWith(root + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) {
      res.writeHead(404).end();
      return;
    }
    res.writeHead(200, { 'Content-Type': 'audio/mpeg', 'Content-Length': fs.statSync(file).size });
    fs.createReadStream(file).pipe(res);
  }).listen(Number(b), '127.0.0.1', () => console.log(`serving ${root} on ${b}`));
} else if (mode === 'container') {
  // Azurite's published development account; not a secret.
  const account = 'devstoreaccount1';
  const key = Buffer.from('Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==', 'base64');
  const url = new URL(`${a.replace(/\/$/, '')}/${b}?restype=container`);
  const date = new Date().toUTCString();
  const headers = { 'x-ms-blob-public-access': 'blob', 'x-ms-date': date, 'x-ms-version': '2021-12-02' };
  const canonicalHeaders = Object.keys(headers).sort().map(h => `${h}:${headers[h]}\n`).join('');
  // Shared Key: VERB + 11 empty standard headers (Content-Length is empty for 0), headers, resource.
  const toSign = `PUT\n\n\n\n\n\n\n\n\n\n\n\n${canonicalHeaders}/${account}${url.pathname}\nrestype:container`;
  headers.Authorization = `SharedKey ${account}:${crypto.createHmac('sha256', key).update(toSign, 'utf8').digest('base64')}`;
  headers['Content-Length'] = 0;
  const req = http.request(url, { method: 'PUT', headers }, res => {
    let body = '';
    res.on('data', d => body += d);
    res.on('end', () => {
      // 201 created; 409 already exists (a rerun) is fine too.
      console.log(`container ${b}: HTTP ${res.statusCode}`);
      process.exit(res.statusCode === 201 || res.statusCode === 409 ? 0 : (console.error(body), 1));
    });
  });
  req.on('error', e => { console.error(e.message); process.exit(1); });
  req.end();
} else {
  console.error('usage: node fixtures.js serve <folder> <port> | container <blobEndpoint> <name>');
  process.exit(2);
}
