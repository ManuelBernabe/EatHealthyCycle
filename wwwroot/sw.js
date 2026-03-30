const CACHE = 'eatcycle-v15';
const API_CACHE = 'eatcycle-api-v3';
const SHELL = ['/', '/css/app.css', '/js/offline.js', '/js/api.js', '/js/app.js'];

// GET API paths to cache for offline reading
function isCacheableApi(request) {
  if (request.method !== 'GET') return false;
  const p = new URL(request.url).pathname;
  return p.includes('/dietas') || p.includes('/planes') ||
    p.includes('/lista-compra') || p.includes('/peso') ||
    p.includes('/cumplimiento');
}

function isApiCall(url) {
  return url.includes('/api/') || url.includes('/auth/') ||
    url.includes('/users') || url.includes('/me/');
}

self.addEventListener('install', e => {
  self.skipWaiting();
  e.waitUntil(caches.open(CACHE).then(c => c.addAll(SHELL)));
});

self.addEventListener('activate', e => {
  e.waitUntil(
    caches.keys().then(keys =>
      Promise.all(keys.filter(k => k !== CACHE && k !== API_CACHE).map(k => caches.delete(k)))
    ).then(() => self.clients.claim())
  );
});

self.addEventListener('fetch', e => {
  if (isApiCall(e.request.url)) {
    if (isCacheableApi(e.request)) {
      // Cacheable GET API: network-first, cache on success, serve cache offline
      e.respondWith(
        fetch(e.request).then(res => {
          if (res.ok) {
            const clone = res.clone();
            caches.open(API_CACHE).then(c => c.put(e.request, clone));
          }
          return res;
        }).catch(() =>
          caches.open(API_CACHE).then(c => c.match(e.request)).then(r =>
            r || new Response('{"error":"offline"}', { status: 503, headers: { 'Content-Type': 'application/json' } })
          )
        )
      );
    } else {
      // Non-cacheable API (auth, POST, etc): network only
      e.respondWith(
        fetch(e.request).catch(() =>
          new Response('{"error":"offline"}', { status: 503, headers: { 'Content-Type': 'application/json' } })
        )
      );
    }
  } else {
    // App shell: network-first with cache fallback
    e.respondWith(
      fetch(e.request).then(res => {
        const clone = res.clone();
        caches.open(CACHE).then(c => c.put(e.request, clone));
        return res;
      }).catch(() => caches.match(e.request).then(r => r || new Response('Not found', { status: 503 })))
    );
  }
});
