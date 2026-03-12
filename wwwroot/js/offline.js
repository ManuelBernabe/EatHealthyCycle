// EatHealthyCycle Offline Support — API response cache + write queue with auto-sync

const EcOffline = (function () {
  const CACHE_PREFIX = 'ec_cache_';
  const QUEUE_KEY = 'ec_sync_queue';
  const CACHE_TTL = 24 * 60 * 60 * 1000; // 24 hours

  // --- Cache helpers ---

  function cacheKey(path) {
    return CACHE_PREFIX + path;
  }

  function getCached(path) {
    try {
      const raw = localStorage.getItem(cacheKey(path));
      if (!raw) return null;
      const entry = JSON.parse(raw);
      if (Date.now() - entry.ts > CACHE_TTL) {
        localStorage.removeItem(cacheKey(path));
        return null;
      }
      return entry.data;
    } catch { return null; }
  }

  function setCache(path, data) {
    try {
      localStorage.setItem(cacheKey(path), JSON.stringify({ data, ts: Date.now() }));
    } catch { /* localStorage full — ignore */ }
  }

  function isCacheableGet(path) {
    return /\/api\/usuarios\/\d+\/(dietas|planes|peso)/.test(path) ||
      /\/api\/(dietas|planes)\/\d+/.test(path) ||
      /\/api\/planes\/\d+\/(lista-compra|cumplimiento)/.test(path);
  }

  // --- Sync queue (for offline writes) ---

  function getQueue() {
    try { return JSON.parse(localStorage.getItem(QUEUE_KEY) || '[]'); }
    catch { return []; }
  }

  function saveQueue(q) {
    localStorage.setItem(QUEUE_KEY, JSON.stringify(q));
  }

  function enqueue(method, path, body) {
    const q = getQueue();
    q.push({ method, path, body, ts: Date.now(), id: Date.now() + '_' + Math.random().toString(36).slice(2, 6) });
    saveQueue(q);
    updateSyncBadge();
    return q.length;
  }

  function dequeue(id) {
    saveQueue(getQueue().filter(item => item.id !== id));
    updateSyncBadge();
  }

  function pendingCount() { return getQueue().length; }

  // --- Sync engine ---

  let syncing = false;

  async function syncAll() {
    if (syncing || !navigator.onLine) return;
    const queue = getQueue();
    if (queue.length === 0) return;

    syncing = true;
    let synced = 0;

    for (const item of queue) {
      try {
        const token = localStorage.getItem('token');
        const headers = {};
        if (token) headers['Authorization'] = `Bearer ${token}`;
        if (item.body !== undefined && item.body !== null) {
          headers['Content-Type'] = 'application/json';
        }

        const opts = { method: item.method, headers };
        if (item.body !== undefined && item.body !== null) {
          opts.body = JSON.stringify(item.body);
        }

        const res = await fetch(item.path, opts);
        if (res.ok || res.status === 409) {
          dequeue(item.id);
          synced++;
        } else {
          break; // stop on server error, retry later
        }
      } catch {
        break; // network still down
      }
    }

    syncing = false;

    if (synced > 0) {
      // Invalidate caches that may have changed
      Object.keys(localStorage)
        .filter(k => k.startsWith(CACHE_PREFIX + '/api/planes/') || k.startsWith(CACHE_PREFIX + '/api/lista-compra/'))
        .forEach(k => localStorage.removeItem(k));
      updateSyncBadge();
      window.dispatchEvent(new CustomEvent('ec-synced', { detail: { synced } }));
    }

    return synced;
  }

  // --- Online/offline events ---

  function isOnline() { return navigator.onLine; }

  function initListeners() {
    window.addEventListener('online', () => {
      updateOfflineBanner(false);
      syncAll();
    });
    window.addEventListener('offline', () => {
      updateOfflineBanner(true);
    });

    if (!navigator.onLine) {
      updateOfflineBanner(true);
    } else {
      setTimeout(() => syncAll(), 2000);
    }
  }

  // --- UI helpers ---

  function updateOfflineBanner(isOffline) {
    let banner = document.getElementById('ec-offline-banner');
    if (isOffline) {
      if (!banner) {
        banner = document.createElement('div');
        banner.id = 'ec-offline-banner';
        banner.style.cssText = 'position:fixed;top:0;left:0;right:0;z-index:9999;' +
          'background:#ff8c00;color:#fff;text-align:center;padding:6px 12px;' +
          'font-size:13px;font-weight:600;letter-spacing:.3px;';
        document.body.prepend(banner);
      }
      const count = pendingCount();
      banner.textContent = '⚠ Sin conexión' + (count > 0 ? ` · ${count} pendiente${count > 1 ? 's' : ''}` : '');
    } else if (banner) {
      banner.remove();
    }
  }

  function updateSyncBadge() {
    const count = pendingCount();
    const banner = document.getElementById('ec-offline-banner');
    if (banner && !isOnline()) {
      banner.textContent = '⚠ Sin conexión' + (count > 0 ? ` · ${count} pendiente${count > 1 ? 's' : ''}` : '');
    }
    window.dispatchEvent(new CustomEvent('ec-queue-update', { detail: { count } }));
  }

  function showSyncToast(message) {
    const toast = document.getElementById('toast');
    if (toast) {
      toast.textContent = message;
      toast.classList.add('show');
      setTimeout(() => toast.classList.remove('show'), 3000);
    }
  }

  return {
    getCached,
    setCache,
    isCacheableGet,
    enqueue,
    pendingCount,
    syncAll,
    isOnline,
    initListeners,
    showSyncToast,
    updateOfflineBanner,
  };
})();
