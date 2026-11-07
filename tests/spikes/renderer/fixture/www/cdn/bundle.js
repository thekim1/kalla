import { render } from './util.js';
const n = Number((location.pathname.match(/(\d+)\.html$/) || [0, 1])[1]);
const app = document.getElementById('app');
const preflighted = fetch('http://api.test/api/page/' + n, { headers: { 'x-spike': '1' } }).then(r => r.json());
const xhr = new Promise((resolve, reject) => {
  const x = new XMLHttpRequest();
  x.open('GET', 'http://api.test/api/page/' + (n + 100));
  x.onload = () => resolve(JSON.parse(x.responseText));
  x.onerror = () => reject(new Error('xhr'));
  x.send();
});
const redirectedApi = fetch('http://api.test/api/redirect').then(r => r.json()).then(d => 'API-REDIRECT-OK ' + d.title, e => 'API-REDIRECT-FAIL ' + e);
const post = fetch('http://api.test/api/echo', { method: 'POST', headers: { 'content-type': 'application/json' }, body: '{"q":1}' })
  .then(r => r.json()).then(d => 'POST-OK ' + d.echo, e => 'POST-FAIL ' + e);
const lazy = import('./lazy.js');
Promise.all([preflighted, xhr, lazy, redirectedApi, post]).then(([data, more, mod, red, p]) => {
  render(app, `<article><h1>${data.title}</h1><p>${data.text}</p><p>${more.title}</p>`
    + `<p>${mod.lazyText}</p><p>${window.__classic}</p><p>${red}</p><p>${p}</p>`
    + `<p>${window.__redirected || 'REDIRECTED-MODULE-MISSING'}</p><a href="/spa/${n + 1}.html">Nästa</a></article>`);
}, e => render(app, '<p>BUNDLE-ERROR ' + e + '</p>'));
