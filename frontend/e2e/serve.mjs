// Serves the production build the way the API does: static files, the Content-Security-Policy from appsettings.json
// (so a spec runs the bundle under the policy it ships with), and index.html for every unknown path.
import {readFileSync, statSync} from 'node:fs';
import {createServer} from 'node:http';
import {extname, join, normalize} from 'node:path';

const root = join(import.meta.dirname, '..', 'dist', 'basket', 'browser');
const settings = join(
  import.meta.dirname,
  '..',
  '..',
  'backend',
  'src',
  'Basket.Api',
  'appsettings.json'
);
const csp = JSON.parse(readFileSync(settings, 'utf8')).Security.ContentSecurityPolicy;
const port = Number(process.env.PORT ?? 4301);
const types = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json',
  '.webmanifest': 'application/manifest+json',
  '.png': 'image/png',
  '.svg': 'image/svg+xml',
  '.ico': 'image/x-icon',
  '.woff2': 'font/woff2',
};

function fileFor(pathname) {
  const file = join(root, normalize(decodeURIComponent(pathname)));
  try {
    return file.startsWith(root) && statSync(file).isFile() ? file : join(root, 'index.html');
  } catch {
    return join(root, 'index.html');
  }
}

createServer((req, res) => {
  const file = fileFor(new URL(req.url, 'http://x').pathname);
  res.writeHead(200, {
    'content-type': types[extname(file)] ?? 'application/octet-stream',
    'content-security-policy': csp,
  });
  res.end(readFileSync(file));
}).listen(port, '127.0.0.1');
