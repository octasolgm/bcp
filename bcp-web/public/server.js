/**
 * Azure App Service static host — reads Application Settings as process.env
 * and serves /assets/app-config.json at runtime (no rebuild needed).
 *
 * Startup command (Linux or Windows Node stack):
 *   node server.js
 *
 * Set WEBSITES_PORT=8080 on Linux if needed.
 */
const http = require('http');
const fs = require('fs');
const path = require('path');

const ROOT = __dirname;
const PORT = Number(process.env.PORT || process.env.WEBSITES_PORT || 8080);

const MIME = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'application/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.ico': 'image/x-icon',
  '.png': 'image/png',
  '.svg': 'image/svg+xml',
  '.woff': 'font/woff',
  '.woff2': 'font/woff2',
  '.txt': 'text/plain; charset=utf-8',
};

function runtimeConfig() {
  const host = process.env.WEBSITE_HOSTNAME;
  return {
    supabaseUrl: process.env.supabaseUrl || '',
    supabaseAnonKey: process.env.supabaseAnonKey || '',
    ndApiUrl: process.env.ndApiUrl || '',
    appUrl: process.env.appUrl || (host ? `https://${host}` : ''),
  };
}

function sendFile(res, filePath) {
  const ext = path.extname(filePath).toLowerCase();
  const type = MIME[ext] || 'application/octet-stream';
  res.writeHead(200, { 'Content-Type': type });
  fs.createReadStream(filePath).pipe(res);
}

const server = http.createServer((req, res) => {
  const urlPath = (req.url || '/').split('?')[0];

  if (urlPath === '/assets/app-config.json') {
    res.writeHead(200, {
      'Content-Type': 'application/json; charset=utf-8',
      'Cache-Control': 'no-store',
    });
    res.end(JSON.stringify(runtimeConfig()));
    return;
  }

  let filePath = path.join(ROOT, decodeURIComponent(urlPath));
  if (urlPath === '/') filePath = path.join(ROOT, 'index.html');

  if (fs.existsSync(filePath) && fs.statSync(filePath).isFile()) {
    sendFile(res, filePath);
    return;
  }

  // SPA fallback — Angular routes
  const index = path.join(ROOT, 'index.html');
  if (fs.existsSync(index)) {
    sendFile(res, index);
    return;
  }

  res.writeHead(404, { 'Content-Type': 'text/plain' });
  res.end('Not found');
});

server.listen(PORT, () => {
  console.log(`bcp-web listening on ${PORT}`);
});
