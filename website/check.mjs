import { readFileSync, readdirSync, existsSync, statSync } from 'node:fs';
import path from 'node:path';
const root = path.resolve('dist');
const files = [];
function visit(dir) { for (const name of readdirSync(dir)) { const file=path.join(dir,name); if(statSync(file).isDirectory()) visit(file); else files.push(file); } }
visit(root);
const errors = [];
for (const page of ['index.html', '404.html', 'terms/index.html', 'support/index.html', 'privacy/index.html']) {
  if (!existsSync(path.join(root, page))) errors.push(`Missing deployment page: ${page}`);
}
if (!existsSync(path.join(root, '_redirects')) || readFileSync(path.join(root, '_redirects'), 'utf8').trim() !== '/* /404.html 404') errors.push('Missing custom Netlify 404 route');
for (const file of files.filter(p=>p.endsWith('.html'))) {
  const html = readFileSync(file,'utf8');
  if ((html.match(/<h1\b/g)||[]).length !== 1) errors.push(`${file}: expected one main heading`);
  if (!html.includes('name="description"') || !html.includes('rel="canonical"')) errors.push(`${file}: missing metadata`);
  for (const [,href] of html.matchAll(/(?:href|src)="([^"]+)"/g)) {
    if (/^(?:https?:|mailto:|data:)/.test(href)) continue;
    const url = new URL(href, 'http://local/' + path.relative(root,file).replaceAll('\\','/'));
    let target=path.join(root,decodeURIComponent(url.pathname));
    if (existsSync(target) && statSync(target).isDirectory()) target=path.join(target,'index.html');
    if (!existsSync(target)) {errors.push(`${file}: missing target ${href}`);continue;}
    if (url.hash && target.endsWith('.html') && !readFileSync(target,'utf8').includes(`id="${url.hash.slice(1)}"`)) errors.push(`${file}: missing anchor ${href}`);
  }
  if (/client_secret|access_token|refresh_token/.test(html)) errors.push(`${file}: unexpected credential-shaped content`);
}
const home=readFileSync(path.join(root,'index.html'),'utf8');
if (!home.includes('https://github.com/csgdV2/WavyMusic/releases/latest')) errors.push('Missing official download destination');
const downloads=[...home.matchAll(/href="([^"]+)" data-download="windows"/g)].map(match=>match[1]);
if(downloads.length < 3 || downloads.some(href=>!/^https:\/\/github\.com\/csgdV2\/WavyMusic\/releases\/download\/.+\.exe$/.test(href))) errors.push('Download buttons must link directly to GitHub EXE assets');
if(!home.includes('/assets/wavy-home.png')||!home.includes('/assets/app-icon.png')) errors.push('Real app screenshots are missing');
if (/search-visual|queue-visual|eq-visual|mock-player|mock-track/.test(home)) errors.push('Illustrated app mockups must not appear in the website');
if (files.some(f=>/google-oauth|client_secret|\.protected/.test(f))) errors.push('Unexpected app credentials in website output');
if (errors.length) { console.error(errors.join('\n'));process.exitCode=1; }
else console.log(`PASS: ${files.filter(f=>f.endsWith('.html')).length} HTML pages; internal links, anchors, metadata, download destination and credential exclusion.`);
