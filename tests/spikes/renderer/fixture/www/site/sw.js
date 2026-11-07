self.addEventListener('fetch', e => e.respondWith(new Response('SW-INTERCEPTED')));
