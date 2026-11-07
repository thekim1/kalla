fetch('http://api.test/api/page/77').then(r => r.json()).then(d => postMessage('ok ' + d.title)).catch(e => postMessage('error ' + e));
