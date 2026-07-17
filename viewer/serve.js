// Tiny static server for the island shape previewer.
// Serves the repo root so the page can fetch /shapes/<name>.txt.
//   node viewer/serve.js   ->   http://localhost:5184
const http = require('http');
const fs = require('fs');
const path = require('path');

const ROOT = path.join(__dirname, '..');
const PORT = process.env.PORT || 5184;
const MIME = {
  '.html': 'text/html', '.js': 'application/javascript', '.txt': 'text/plain',
  '.json': 'application/json', '.css': 'text/css',
};

http.createServer((req, res) => {
  // Capture endpoint: the page POSTs a PNG data URL so the WebGL canvas can
  // be screenshotted into viewer/shots/ for review.
  if (req.method === 'POST' && req.url.startsWith('/capture')) {
    let body = '';
    req.on('data', (c) => (body += c));
    req.on('end', () => {
      const m = body.match(/^data:image\/png;base64,(.+)$/);
      if (!m) { res.writeHead(400); return res.end('bad'); }
      const name = (new URL(req.url, 'http://x').searchParams.get('name') || 'shot').replace(/[^a-z0-9_-]/gi, '');
      const dir = path.join(__dirname, 'shots');
      fs.mkdirSync(dir, { recursive: true });
      fs.writeFileSync(path.join(dir, name + '.png'), Buffer.from(m[1], 'base64'));
      res.writeHead(200); res.end('ok');
    });
    return;
  }

  // Shape list for the dropdown.
  if (req.url.startsWith('/list')) {
    let names = [];
    try {
      names = fs.readdirSync(path.join(ROOT, 'shapes'))
        .filter((f) => f.endsWith('.txt'))
        .map((f) => f.replace(/\.txt$/, ''));
    } catch (e) { /* no shapes dir */ }
    res.writeHead(200, { 'Content-Type': 'application/json' });
    return res.end(JSON.stringify(names));
  }

  let url = decodeURIComponent(req.url.split('?')[0]);
  if (url === '/' || url === '') url = '/viewer/index.html';
  const fp = path.normalize(path.join(ROOT, url));
  if (!fp.startsWith(ROOT)) { res.writeHead(403); return res.end('forbidden'); }
  fs.readFile(fp, (err, data) => {
    if (err) { res.writeHead(404); return res.end('not found: ' + url); }
    res.writeHead(200, {
      'Content-Type': MIME[path.extname(fp).toLowerCase()] || 'application/octet-stream',
      'Access-Control-Allow-Origin': '*',
    });
    res.end(data);
  });
}).listen(PORT, () => console.log('island previewer on http://localhost:' + PORT));

