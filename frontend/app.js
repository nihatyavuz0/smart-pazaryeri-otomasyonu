// ==========================================================
// SMART — Hardened Frontend Controller (Zero inline JS, CSP strict)
// ==========================================================

const SESSION_TOKEN = (() => {
  const meta = document.querySelector('meta[name="smart-token"]');
  return meta ? meta.getAttribute('content') || '' : '';
})();

const state = {
  user: null,
  userProfile: null,
  security: null,
  aiConfig: null,
  syncConfig: null,
  draft: null,
  marketplaces: [],
  products: [],
  orders: [],
  logs: [],
  uploadedImages: [],
  uploadedFileNames: [],
  selectedTargetMps: [],
  currentDraft: null,
  engineUsed: '',
  activeListingTab: 'trendyol',
  activeView: 'dashboard',
  initialized: false
};

let draftSaveTimer = null;
let aiSaveTimer = null;
let syncSaveTimer = null;
const mpSaveTimers = {};

const CRED_LABELS = {
  supplierId: 'Satıcı ID (Supplier ID)',
  apiKey: 'API Key',
  apiSecret: 'API Secret',
  userAgent: 'Entegratör / User-Agent',
  merchantId: 'Mağaza ID (Merchant ID)',
  username: 'Kullanıcı Adı',
  serviceKey: 'Servis Anahtarı (Service Key)',
  appKey: 'App Key',
  appSecret: 'App Secret',
  shipmentTemplate: 'Kargo Şablon Adı',
  vendorId: 'Satıcı ID (Vendor ID)',
  clientId: 'Client ID',
  clientSecret: 'Client Secret',
  password: 'Şifre / Parola',
  shopId: 'Mağaza ID (Shop ID)',
  sellerId: 'Satıcı ID (Seller ID)',
  marketplaceId: 'Marketplace ID (TR)',
  lwaClientId: 'LWA Client ID',
  lwaClientSecret: 'LWA Client Secret',
  refreshToken: 'LWA Refresh Token'
};

const SOURCE_BADGES = {
  prompt: { label: 'Prompt', cls: 'badge-khaki' },
  competitor: { label: 'Rakip Analizi', cls: 'badge-info' },
  ai: { label: 'AI Önerisi', cls: 'badge-success' },
  default: { label: 'Zorunlu Alan', cls: 'badge-neutral' }
};

// ---------------- Init ----------------
document.addEventListener('DOMContentLoaded', () => {
  bindNavigation();
  bindGlobalDelegatedActions();
  bindAddProductEvents();
  bindInventoryEvents();
  bindSettingsEvents();
  bindBackupEvents();
  bindAccountEvents();
  loadState(true);
  startLiveUpdateSync();

  if (sessionStorage.getItem('smartJustUpdated') === '1') {
    sessionStorage.removeItem('smartJustUpdated');
    toast('Smart en güncel sürüme otomatik olarak senkronize edildi.', 'success');
  }
});

let knownServerId = null;
let knownFrontendVersion = null;
let knownDataVersion = null;
let lastLocalWriteAt = 0;
let isRemoteSyncInFlight = false;
let updateReloadPending = false;

function startLiveUpdateSync() {
  async function pollVersion() {
    if (updateReloadPending) return;
    try {
      const res = await fetch('/api/version', { cache: 'no-store', credentials: 'same-origin' });
      if (res.status === 401) {
        updateReloadPending = true;
        window.location.reload();
        return;
      }
      if (!res.ok) {
        setTimeout(pollVersion, 1200);
        return;
      }
      const info = await res.json();
      if (!knownServerId) {
        knownServerId = info.serverId;
        knownFrontendVersion = info.frontendVersion;
        knownDataVersion = info.dataVersion || null;
        if (info.appVersion) {
          const pt = document.getElementById('persist-text');
          if (pt) pt.textContent = `Kalıcı Kayıt • ${info.appVersion}`;
          const mpt = document.getElementById('mobile-persist-text');
          if (mpt) mpt.textContent = `Kalıcı Kayıt • ${info.appVersion}`;
        }
      } else if (info.serverId !== knownServerId || info.frontendVersion !== knownFrontendVersion) {
        updateReloadPending = true;
        sessionStorage.setItem('smartJustUpdated', '1');
        // If only frontend changed, flush any pending draft edit before reload
        if (info.serverId === knownServerId && state.initialized) {
          try {
            await fetch('/api/draft/save', {
              method: 'POST',
              headers: { 'Content-Type': 'application/json', 'X-Smart-Token': SESSION_TOKEN },
              body: JSON.stringify(buildDraftPayload())
            });
          } catch { }
        }
        window.location.reload();
        return;
      } else if (info.dataVersion && info.dataVersion !== knownDataVersion) {
        knownDataVersion = info.dataVersion;
        // Another browser or mobile device updated data — pull live changes smoothly without interrupting typing
        if (state.initialized && !isRemoteSyncInFlight && (Date.now() - lastLocalWriteAt) > 1200) {
          isRemoteSyncInFlight = true;
          loadState(false, true).finally(() => { isRemoteSyncInFlight = false; });
        }
      }
      setTimeout(pollVersion, 2500);
    } catch {
      // Backend is momentarily swapping during hot-update; retry quickly until it's back
      setTimeout(pollVersion, 800);
    }
  }
  setTimeout(pollVersion, 1500);
}

async function apiFetch(url, options = {}) {
  const method = (options.method || 'GET').toUpperCase();
  if (method !== 'GET') lastLocalWriteAt = Date.now();
  const headers = {
    'Content-Type': 'application/json',
    'X-Smart-Token': SESSION_TOKEN,
    ...(options.headers || {})
  };
  const res = await fetch(url, { credentials: 'same-origin', ...options, headers });
  if (res.status === 401 && !updateReloadPending) {
    updateReloadPending = true;
    window.location.reload();
    return { ok: false };
  }
  if (res.status === 403 && !updateReloadPending) {
    // Session token rotated after a live server hot-update — reload smoothly to sync token
    try {
      const vRes = await fetch('/api/version', { cache: 'no-store' });
      if (vRes.ok) {
        const vInfo = await vRes.json();
        if (knownServerId && vInfo.serverId !== knownServerId) {
          updateReloadPending = true;
          sessionStorage.setItem('smartJustUpdated', '1');
          window.location.reload();
          return { ok: true };
        }
      }
    } catch { }
  }
  const data = await res.json();
  if (!res.ok || data.ok === false) {
    throw new Error(data.error || data.message || 'İşlem başarısız oldu.');
  }
  return data;
}

function markSaved(label) {
  const now = new Date().toLocaleTimeString('tr-TR', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
  const text = label || `Kaydedildi • ${now}`;
  const el = document.getElementById('persist-text');
  if (el) el.textContent = text;
  const mel = document.getElementById('mobile-persist-text');
  if (mel) mel.textContent = text;
}

function isFocusedInside(selector) {
  const ae = document.activeElement;
  if (!ae) return false;
  const tag = ae.tagName;
  if (tag !== 'INPUT' && tag !== 'TEXTAREA' && tag !== 'SELECT') return false;
  const root = document.querySelector(selector);
  return !!(root && root.contains(ae));
}

async function loadState(isInitial = false, fromRemoteSync = false) {
  try {
    const prevDraftSig = fromRemoteSync ? JSON.stringify(state.draft || {}) : '';
    const data = await apiFetch('/api/state');
    state.user = data.user || null;
    state.userProfile = data.userProfile || null;
    state.security = data.security || null;
    state.aiConfig = data.aiConfig;
    state.syncConfig = data.syncConfig;
    state.draft = data.draft || {};
    state.marketplaces = data.marketplaces || [];
    state.products = data.products || [];
    state.orders = data.orders || [];
    state.logs = data.logs || [];

    applyUserProfileToUi();
    updateSecurityBadges();

    if (isInitial || !state.initialized) {
      restoreDraftFromServer(state.draft);
      state.initialized = true;
      const startView = (state.userProfile && state.userProfile.DefaultView) || state.draft.ActiveView || 'dashboard';
      applyViewDom(startView);
    } else if (fromRemoteSync) {
      const nextDraftSig = JSON.stringify(state.draft || {});
      if (nextDraftSig !== prevDraftSig && !draftSaveTimer && !isFocusedInside('#view-add-product')) {
        restoreDraftFromServer(state.draft);
      }
    }

    renderSidebarStatus();
    renderDashboard();
    renderTargetMpChips();
    const editingStockInput = document.activeElement && document.activeElement.id && document.activeElement.id.startsWith('stk-');
    if (!fromRemoteSync || !editingStockInput) {
      renderInventory();
    }
    if (isInitial || (state.activeView !== 'apis' && !isFocusedInside('#view-apis'))) {
      renderApiPanel();
    }
    if (isInitial || !isFocusedInside('#view-account')) {
      renderAccountView();
    }
    renderLogs();
    if (fromRemoteSync) {
      const now = new Date().toLocaleTimeString('tr-TR', { hour: '2-digit', minute: '2-digit', second: '2-digit' });
      markSaved(`Eşitlendi • ${now}`);
    }
  } catch (err) {
    if (!fromRemoteSync) {
      toast('Sunucu bağlantısı alınamadı: ' + err.message, 'error');
    }
  }
}

function restoreDraftFromServer(d) {
  if (!d) return;
  document.getElementById('product-prompt').value = d.Prompt || '';
  document.getElementById('opt-price').value = d.OptPrice || '';
  document.getElementById('opt-stock').value = d.OptStock || '';
  document.getElementById('opt-brand').value = d.OptBrand || '';
  document.getElementById('opt-barcode').value = d.OptBarcode || '';

  state.uploadedImages = Array.isArray(d.UploadedImages) ? [...d.UploadedImages] : [];
  if (Array.isArray(d.SelectedTargetMps) && d.SelectedTargetMps.length > 0) {
    state.selectedTargetMps = [...d.SelectedTargetMps];
  } else {
    state.selectedTargetMps = state.marketplaces.filter(m => m.Enabled).map(m => m.Id);
  }
  state.activeListingTab = d.ActiveListingTab || state.selectedTargetMps[0] || 'trendyol';
  state.engineUsed = d.EngineUsed || '';
  state.currentDraft = d.GeneratedDraft || null;

  renderUploadedThumbs();
  if (state.currentDraft) {
    renderGeneratedPreview(state.engineUsed || 'Smart SEO Motoru');
  } else {
    document.getElementById('generated-area').classList.add('hidden');
  }
}

function buildDraftPayload() {
  saveCurrentListingEditsFromDom();
  return {
    ActiveView: state.activeView,
    Prompt: document.getElementById('product-prompt').value,
    OptPrice: document.getElementById('opt-price').value,
    OptStock: document.getElementById('opt-stock').value,
    OptBrand: document.getElementById('opt-brand').value,
    OptBarcode: document.getElementById('opt-barcode').value,
    UploadedImages: state.uploadedImages,
    SelectedTargetMps: state.selectedTargetMps,
    ActiveListingTab: state.activeListingTab,
    EngineUsed: state.engineUsed,
    GeneratedDraft: state.currentDraft
  };
}

function scheduleDraftSave(immediate = false) {
  if (!state.initialized) return;
  clearTimeout(draftSaveTimer);
  const run = async () => {
    try {
      const payload = buildDraftPayload();
      await apiFetch('/api/draft/save', {
        method: 'POST',
        body: JSON.stringify(payload)
      });
      markSaved();
    } catch {
      // silent retry on next edit
    }
  };
  if (immediate) run();
  else draftSaveTimer = setTimeout(run, 450);
}

// ---------------- Delegated Event Router (Zero Inline JS) ----------------
function bindGlobalDelegatedActions() {
  document.body.addEventListener('click', e => {
    const navBtn = e.target.closest('[data-nav]');
    if (navBtn) {
      switchView(navBtn.dataset.nav);
      return;
    }

    const actionEl = e.target.closest('[data-action]');
    if (!actionEl) return;
    const action = actionEl.dataset.action;
    const id = actionEl.dataset.id || '';

    switch (action) {
      case 'remove-image':
        removeUploadedImage(parseInt(actionEl.dataset.idx, 10));
        break;
      case 'delete-user':
        handleDeleteUser(actionEl.dataset.username || '');
        break;
      case 'toggle-target-mp':
        toggleTargetMp(id);
        break;
      case 'select-listing-tab':
        selectListingTab(id);
        break;
      case 'ty-auto-match':
        runTrendyolCategoryMatch(0, actionEl);
        break;
      case 'ty-pick-cat':
        runTrendyolCategoryMatch(parseInt(id, 10), actionEl);
        break;
      case 'stock-step':
        adjustStockInput(id, parseInt(actionEl.dataset.delta, 10));
        break;
      case 'save-stock':
        saveStock(id);
        break;
      case 'edit-product':
        editExistingProduct(id);
        break;
      case 'quick-update-product':
        openQuickUpdateProductModal(id);
        break;
      case 'republish-live':
        republishProductLive(id, actionEl);
        break;
      case 'check-ty-batch':
        checkTrendyolBatch(id, actionEl);
        break;
      case 'simulate-order':
        simulateOrder(id);
        break;
      case 'delete-product':
        deleteProduct(id);
        break;
      case 'save-mp':
        saveMarketplace(id, false);
        break;
      case 'test-mp':
        testMarketplace(id);
        break;
    }
  });
}

// ---------------- Navigation ----------------
function bindNavigation() {
  document.querySelectorAll('.nav-item, .mnav-item').forEach(btn => {
    btn.addEventListener('click', () => {
      switchView(btn.dataset.view);
      if (window.innerWidth <= 820) window.scrollTo({ top: 0, behavior: 'smooth' });
    });
  });

  const mobileSyncBtn = document.getElementById('btn-mobile-sync');
  if (mobileSyncBtn) mobileSyncBtn.addEventListener('click', syncAllStocks);

  const mobileAccessBtn = document.getElementById('btn-mobile-access');
  if (mobileAccessBtn) mobileAccessBtn.addEventListener('click', openMobileAccessModal);

  const mobileAccessTopBtn = document.getElementById('btn-mobile-access-top');
  if (mobileAccessTopBtn) mobileAccessTopBtn.addEventListener('click', openMobileAccessModal);
}

function copyTextSafe(text, successMsg) {
  const done = () => toast(successMsg || 'Kopyalandı.', 'success');
  if (navigator.clipboard && window.isSecureContext) {
    navigator.clipboard.writeText(text).then(done).catch(() => fallbackCopy(text, done));
    return;
  }
  fallbackCopy(text, done);
}

function fallbackCopy(text, onDone) {
  try {
    const ta = document.createElement('textarea');
    ta.value = text;
    ta.setAttribute('readonly', '');
    ta.style.position = 'fixed';
    ta.style.top = '-9999px';
    document.body.appendChild(ta);
    ta.select();
    document.execCommand('copy');
    ta.remove();
    if (onDone) onDone();
  } catch {
    toast('Kopyalanacak metin: ' + text, 'info');
  }
}

function formatPinDisplay(pin) {
  const s = String(pin || '').replace(/\D+/g, '');
  return s.length === 6 ? s : '------';
}

function buildRemoteModalBody(info) {
  const urls = Array.isArray(info.mobileUrls) && info.mobileUrls.length > 0 ? info.mobileUrls : [info.localUrl];
  const lanUrl = urls[0];
  const tunnelActive = !!(info.tunnelActive && info.tunnelUrl);
  const pin = info.accessPin || '';

  const tunnelContent = tunnelActive
    ? `
      <div class="mobile-url-box">
        <span class="mobile-url-code">${esc(info.tunnelUrl)}</span>
      </div>
      <div class="remote-pin-row">
        <div class="remote-pin-meta">
          <span class="remote-pin-label">6 Haneli Güvenlik PIN</span>
          <span class="remote-pin-code" id="remote-pin-value">${esc(formatPinDisplay(pin))}</span>
        </div>
        <div class="remote-btn-row">
          <button type="button" class="btn btn-soft btn-xs" id="btn-remote-copy-pin">PIN Kopyala</button>
          <button type="button" class="btn btn-ghost btn-xs" id="btn-remote-regen-pin">PIN Yenile</button>
        </div>
      </div>
      <div class="remote-btn-row">
        <button type="button" class="btn btn-primary btn-sm" id="btn-remote-copy-url">Linki Kopyala</button>
        <button type="button" class="btn btn-khaki-soft btn-sm" id="btn-remote-copy-both">Link + PIN Kopyala</button>
        <button type="button" class="btn btn-danger-soft btn-sm" id="btn-remote-stop">Bağlantıyı Kapat</button>
      </div>
    `
    : `
      <div class="remote-pin-row">
        <div class="remote-pin-meta">
          <span class="remote-pin-label">Koruma PIN Kodu (Hazır)</span>
          <span class="remote-pin-code" id="remote-pin-value">${esc(formatPinDisplay(pin))}</span>
        </div>
        <button type="button" class="btn btn-ghost btn-xs" id="btn-remote-regen-pin">PIN Yenile</button>
      </div>
      <div class="remote-btn-row">
        <button type="button" class="btn btn-primary btn-sm" id="btn-remote-start">Her Yerden Güvenli Linki Başlat (HTTPS)</button>
      </div>
      <p class="remote-hint">Başlatıldığında ücretsiz şifreli bir Cloudflare HTTPS adresi açılır. Başka telefon, tablet, 4G/5G veya farklı bir bilgisayardan bu link ve 6 haneli PIN ile sorunsuz bağlanabilirsiniz.</p>
    `;

  return `
    <div class="remote-section remote-section-highlight">
      <div class="remote-section-head">
        <span class="remote-section-title">Her Yerden Erişim (HTTPS • 4G/5G / Farklı Ağ / Başka Tarayıcı)</span>
        <span class="badge ${tunnelActive ? 'badge-success' : 'badge-neutral'}">${tunnelActive ? 'Aktif • PIN Korumalı' : 'Kapalı'}</span>
      </div>
      <p class="remote-desc">Tüm cihazlarda ve tarayıcılarda yapılan değişiklikler <strong>otomatik ve canlı</strong> olarak birbirine eşitlenir.</p>
      ${tunnelContent}
    </div>

    <div class="remote-section">
      <div class="remote-section-head">
        <span class="remote-section-title">Aynı Wi-Fi Yerel Ağ Adresi</span>
        <span class="badge badge-khaki">Hızlı Yerel Ağ</span>
      </div>
      <div class="mobile-url-box">
        <span class="mobile-url-code">${esc(lanUrl)}</span>
        <button type="button" class="btn btn-soft btn-xs" id="btn-lan-copy">Kopyala</button>
      </div>
      <p class="remote-hint"><strong>Neden "localhost" başka cihazda açılmaz?</strong> <code>localhost</code> yalnızca bu bilgisayarı gösterir. Farklı cihaz veya mobil veri (4G/5G) için yukarıdaki <strong>Her Yerden Güvenli Link (HTTPS)</strong> bağlantısını kullanın.</p>
    </div>
  `;
}

function wireRemoteModalHandlers(info) {
  const bodyEl = document.getElementById('smart-modal-body');
  if (!bodyEl) return;

  const urls = Array.isArray(info.mobileUrls) && info.mobileUrls.length > 0 ? info.mobileUrls : [info.localUrl];
  const lanUrl = urls[0];

  const rerender = async () => {
    const fresh = await apiFetch('/api/network-info');
    bodyEl.innerHTML = buildRemoteModalBody(fresh);
    wireRemoteModalHandlers(fresh);
  };

  const startBtn = bodyEl.querySelector('#btn-remote-start');
  if (startBtn) {
    startBtn.addEventListener('click', async () => {
      startBtn.disabled = true;
      startBtn.textContent = 'Güvenli HTTPS bağlantısı kuruluyor...';
      try {
        await apiFetch('/api/remote/start', { method: 'POST', body: '{}' });
        toast('Her yerden güvenli HTTPS linki oluşturuldu.', 'success');
        await rerender();
      } catch (err) {
        toast('Uzaktan erişim başlatılamadı: ' + err.message, 'error');
        startBtn.disabled = false;
        startBtn.textContent = 'Her Yerden Güvenli Linki Başlat (HTTPS)';
      }
    });
  }

  const stopBtn = bodyEl.querySelector('#btn-remote-stop');
  if (stopBtn) {
    stopBtn.addEventListener('click', async () => {
      stopBtn.disabled = true;
      stopBtn.textContent = 'Kapatılıyor...';
      try {
        await apiFetch('/api/remote/stop', { method: 'POST', body: '{}' });
        toast('Uzaktan HTTPS bağlantısı kapatıldı.', 'info');
        await rerender();
      } catch (err) {
        toast('Kapatılamadı: ' + err.message, 'error');
        stopBtn.disabled = false;
        stopBtn.textContent = 'Bağlantıyı Kapat';
      }
    });
  }

  const regenBtn = bodyEl.querySelector('#btn-remote-regen-pin');
  if (regenBtn) {
    regenBtn.addEventListener('click', async () => {
      regenBtn.disabled = true;
      try {
        const res = await apiFetch('/api/remote/regenerate-pin', { method: 'POST', body: '{}' });
        toast('Yeni güvenlik PIN\'i oluşturuldu: ' + res.accessPin + ' (eski uzak oturumlar kapatıldı).', 'success');
        await rerender();
      } catch (err) {
        toast('PIN yenilenemedi: ' + err.message, 'error');
        regenBtn.disabled = false;
      }
    });
  }

  const copyUrlBtn = bodyEl.querySelector('#btn-remote-copy-url');
  if (copyUrlBtn) {
    copyUrlBtn.addEventListener('click', () => copyTextSafe(info.tunnelUrl, 'Her yerden erişim HTTPS linki kopyalandı.'));
  }

  const copyPinBtn = bodyEl.querySelector('#btn-remote-copy-pin');
  if (copyPinBtn) {
    copyPinBtn.addEventListener('click', () => copyTextSafe(info.accessPin, 'Güvenlik PIN kodu kopyalandı.'));
  }

  const copyBothBtn = bodyEl.querySelector('#btn-remote-copy-both');
  if (copyBothBtn) {
    copyBothBtn.addEventListener('click', () => {
      const bundle = `Smart Panel: ${info.tunnelUrl}\nGüvenlik PIN: ${info.accessPin}`;
      copyTextSafe(bundle, 'Bağlantı linki ve PIN kodu birlikte kopyalandı.');
    });
  }

  const lanCopyBtn = bodyEl.querySelector('#btn-lan-copy');
  if (lanCopyBtn) {
    lanCopyBtn.addEventListener('click', () => copyTextSafe(lanUrl, 'Yerel Wi-Fi adresi kopyalandı.'));
  }
}

function applyViewDom(viewName) {
  state.activeView = viewName;
  document.querySelectorAll('.nav-item, .mnav-item').forEach(b => {
    b.classList.toggle('active', b.dataset.view === viewName);
  });
  document.querySelectorAll('.view').forEach(v => {
    v.classList.toggle('active', v.id === 'view-' + viewName);
  });
}

async function openMobileAccessModal() {
  try {
    const info = await apiFetch('/api/network-info');
    openSmartModal({
      eyebrow: 'Çoklu Cihaz & Uzaktan Erişim',
      title: 'Başka Cihaz veya Tarayıcıdan Bağlan',
      tone: 'brand',
      wide: true,
      bodyHtml: buildRemoteModalBody(info),
      actions: [
        { label: 'Kapat', cls: 'btn-soft', onClick: () => {} }
      ]
    });
    wireRemoteModalHandlers(info);
  } catch (err) {
    toast('Ağ bilgisi alınamadı: ' + err.message, 'error');
  }
}

function switchView(viewName) {
  applyViewDom(viewName);
  scheduleDraftSave(true);
  if (viewName === 'inventory' || viewName === 'logs' || viewName === 'dashboard') {
    loadState(false);
  }
}

// ---------------- Sidebar & Dashboard ----------------
function renderSidebarStatus() {
  document.getElementById('nav-product-count').textContent = state.products.length;
  const mCount = document.getElementById('mobile-product-count');
  if (mCount) {
    mCount.textContent = state.products.length;
    mCount.style.display = state.products.length > 0 ? 'inline-flex' : 'none';
  }
  const dot = document.getElementById('sidebar-sync-dot');
  const txt = document.getElementById('sidebar-sync-text');
  const sub = document.getElementById('sidebar-last-sync');

  if (state.syncConfig && state.syncConfig.AutoSyncEnabled) {
    dot.classList.remove('off');
    txt.textContent = `Oto-Stok Senkron (${state.syncConfig.IntervalMinutes} dk)`;
  } else {
    dot.classList.add('off');
    txt.textContent = 'Oto-Stok Senkron Kapalı';
  }
  sub.textContent = state.syncConfig && state.syncConfig.LastSyncAt
    ? 'Son eşitleme: ' + state.syncConfig.LastSyncAt
    : 'Henüz eşitleme yapılmadı';
}

function renderDashboard() {
  const totalProducts = state.products.length;
  const enabledChannels = state.marketplaces.filter(m => m.Enabled).length;
  const liveChannels = state.marketplaces.filter(m => !m.SimulationMode && m.Connected).length;
  const totalStock = state.products.reduce((acc, p) => acc + (p.Stock || 0), 0);
  const threshold = (state.syncConfig && state.syncConfig.LowStockThreshold) || 5;
  const lowStock = state.products.filter(p => (p.Stock || 0) <= threshold).length;

  document.getElementById('kpi-products').textContent = totalProducts;
  document.getElementById('kpi-channels').textContent = enabledChannels + ' / ' + state.marketplaces.length;
  document.getElementById('kpi-channels-hint').textContent = liveChannels > 0
    ? `${liveChannels} canlı bağlı, ${enabledChannels - liveChannels} test modu`
    : 'Tümü Test Modunda kullanıma hazır';
  document.getElementById('kpi-stock').textContent = totalStock.toLocaleString('tr-TR');
  document.getElementById('kpi-low-stock').textContent = lowStock;

  const mpEl = document.getElementById('dashboard-mp-list');
  mpEl.innerHTML = state.marketplaces.map(m => {
    const badge = !m.Enabled
      ? '<span class="badge badge-neutral">Pasif</span>'
      : (m.SimulationMode
        ? '<span class="badge badge-khaki">Test Modu Hazır</span>'
        : (m.Connected
          ? '<span class="badge badge-success">Canlı Bağlı</span>'
          : '<span class="badge badge-warning">Bağlantı Bekliyor</span>'));
    return `
      <div class="mp-status-row">
        <div class="mp-status-left">
          <span class="mp-status-name">${esc(m.Name)}</span>
          <span class="mp-status-sub">${esc(m.StatusMessage || '')}</span>
        </div>
        ${badge}
      </div>
    `;
  }).join('');

  const logEl = document.getElementById('dashboard-logs');
  const recent = state.logs.slice(0, 6);
  if (recent.length === 0) {
    logEl.innerHTML = '<div class="empty-state">Henüz işlem kaydı yok.</div>';
  } else {
    logEl.innerHTML = recent.map(l => `
      <div class="mini-log">
        <div class="mini-log-top">
          <strong>${esc(l.Marketplace)}</strong>
          <span>${esc(l.Timestamp)}</span>
        </div>
        <div class="mini-log-msg">${esc(l.Message)}</div>
      </div>
    `).join('');
  }
}

// ---------------- Add Product Wizard ----------------
function bindAddProductEvents() {
  const dropzone = document.getElementById('dropzone');
  const fileInput = document.getElementById('file-input');

  dropzone.addEventListener('click', () => fileInput.click());
  dropzone.addEventListener('dragover', e => {
    e.preventDefault();
    dropzone.classList.add('dragover');
  });
  dropzone.addEventListener('dragleave', () => dropzone.classList.remove('dragover'));
  dropzone.addEventListener('drop', e => {
    e.preventDefault();
    dropzone.classList.remove('dragover');
    handleImageFiles(e.dataTransfer.files);
  });
  fileInput.addEventListener('change', e => {
    handleImageFiles(e.target.files);
    fileInput.value = '';
  });

  ['product-prompt', 'opt-price', 'opt-stock', 'opt-brand', 'opt-barcode'].forEach(id => {
    document.getElementById(id).addEventListener('input', () => scheduleDraftSave(false));
  });

  document.querySelectorAll('.chip-btn[data-sample]').forEach(btn => {
    btn.addEventListener('click', () => {
      document.getElementById('product-prompt').value = btn.dataset.sample;
      scheduleDraftSave(true);
      toast('Örnek ürün promptu dolduruldu ve taslak kaydedildi.', 'info');
    });
  });

  document.getElementById('btn-toggle-all-mps').addEventListener('click', () => {
    if (state.selectedTargetMps.length === state.marketplaces.length) {
      state.selectedTargetMps = [];
    } else {
      state.selectedTargetMps = state.marketplaces.map(m => m.Id);
    }
    renderTargetMpChips();
    scheduleDraftSave(true);
  });

  document.getElementById('btn-analyze').addEventListener('click', () => runAnalysis(false));
  document.getElementById('btn-autopilot').addEventListener('click', () => runAnalysis(true));
  document.getElementById('btn-publish-draft').addEventListener('click', publishCurrentDraft);
  document.getElementById('btn-clear-draft').addEventListener('click', clearDraftExplicitly);

  const resetTopBtn = document.getElementById('btn-reset-top');
  if (resetTopBtn) resetTopBtn.addEventListener('click', clearDraftExplicitly);

  const scanBtn = document.getElementById('btn-scan-vision');
  if (scanBtn) scanBtn.addEventListener('click', scanUploadedImagesWithVision);
}

async function scanUploadedImagesWithVision() {
  if (state.uploadedImages.length === 0) {
    toast('Lütfen önce en az bir ürün görseli yükleyin.', 'error');
    return;
  }
  const btn = document.getElementById('btn-scan-vision');
  const orig = btn.textContent;
  btn.disabled = true;
  btn.textContent = 'Görseller taranıyor...';
  try {
    const res = await apiFetch('/api/vision/scan', {
      method: 'POST',
      body: JSON.stringify({
        Images: state.uploadedImages,
        OriginalFileNames: state.uploadedFileNames,
        Prompt: document.getElementById('product-prompt').value.trim()
      })
    });
    const v = res.vision;
    if (v && v.Analyzed) {
      const promptEl = document.getElementById('product-prompt');
      if (!promptEl.value.trim() && v.DetectedProductName) {
        const parts = [v.DetectedProductName];
        if (v.DominantColor && v.DominantColor !== 'Standart') parts.push(v.DominantColor);
        if (v.DetectedMaterial) parts.push(v.DetectedMaterial);
        if (Array.isArray(v.VisualFeatures) && v.VisualFeatures.length > 0) parts.push(v.VisualFeatures[0]);
        promptEl.value = parts.join(', ');
        scheduleDraftSave(true);
      }
      toast(`Görsel tarandı (${v.EngineUsed}): ${v.DetectedProductName} • Renk: ${v.DominantColor}`, 'success');
    }
  } catch (err) {
    toast('Görsel tarama hatası: ' + err.message, 'error');
  } finally {
    btn.disabled = false;
    btn.textContent = orig;
  }
}

async function clearDraftExplicitly() {
  const ok = await smartConfirm({
    eyebrow: 'Formu Sıfırla',
    title: 'Yapılanları sıfırlamak istiyor musunuz?',
    message: 'Yüklenen görseller, prompt metni, opsiyonel alanlar ve oluşturulan analiz taslağı tamamen temizlenecektir. Kayıtlı ürünleriniz etkilenmez.',
    confirmText: 'Sıfırla ve Temizle',
    cancelText: 'Vazgeç',
    tone: 'neutral'
  });
  if (!ok) return;
  try {
    const res = await apiFetch('/api/draft/clear', { method: 'POST', body: '{}' });
    state.uploadedFileNames = [];
    const fi = document.getElementById('file-input');
    if (fi) fi.value = '';
    const vBox = document.getElementById('vision-summary-box');
    if (vBox) vBox.classList.add('hidden');
    restoreDraftFromServer(res.draft);
    renderTargetMpChips();
    document.getElementById('analyze-status').textContent = '';
    markSaved('Form sıfırlandı');
    toast('Ürün ekleme formu tamamen sıfırlandı.', 'info');
  } catch (err) {
    toast('Hata: ' + err.message, 'error');
  }
}

async function handleImageFiles(files) {
  if (!files || files.length === 0) return;
  for (const file of Array.from(files)) {
    const isImageMime = file.type && file.type.startsWith('image/');
    const isImageExt = /\.(jpe?g|png|webp|avif|bmp|gif|jfif|heic)$/i.test(file.name || '');
    if (!isImageMime && !isImageExt) {
      toast('Yalnızca görsel dosyaları (JPG, PNG, WEBP, AVIF) yüklenebilir.', 'error');
      continue;
    }
    if (file.size > 15 * 1024 * 1024) {
      toast('Görsel boyutu en fazla 15 MB olabilir: ' + file.name, 'error');
      continue;
    }
    let uploadDataUrl = '';
    let uploadName = (file.name || 'urun.jpg').replace(/\.[^.]+$/, '') + '.jpg';
    try {
      // Convert ANY image (WEBP, transparent PNG, AVIF, high-res phone photo) into exact 1200x1800 white-bg sRGB baseline JPEG
      uploadDataUrl = await normalizeFileToMarketplaceJpgDataUrl(file);
    } catch {
      if (!['image/jpeg', 'image/png', 'image/webp'].includes(file.type) || file.size > 5 * 1024 * 1024) {
        toast('Bu görsel formatı tarayıcıda JPEG\'e dönüştürülemedi: ' + file.name, 'error');
        continue;
      }
      uploadDataUrl = await readFileAsDataURL(file);
      uploadName = file.name;
    }
    try {
      const resp = await apiFetch('/api/upload', {
        method: 'POST',
        body: JSON.stringify({ fileName: uploadName, data: uploadDataUrl })
      });
      state.uploadedImages.push(resp.url);
      state.uploadedFileNames.push(file.name);
    } catch (err) {
      toast('Görsel yüklenemedi: ' + err.message, 'error');
    }
  }
  renderUploadedThumbs();
  scheduleDraftSave(true);
}

function normalizeFileToMarketplaceJpgDataUrl(file) {
  return new Promise((resolve, reject) => {
    const objUrl = URL.createObjectURL(file);
    const img = new Image();
    img.onload = () => {
      try {
        const sw = img.naturalWidth || img.width;
        const sh = img.naturalHeight || img.height;
        if (!sw || !sh) { URL.revokeObjectURL(objUrl); reject(new Error('invalid dims')); return; }
        const canvasW = 1200;
        const canvasH = 1800;
        const pad = 36;
        const scale = Math.min((canvasW - pad * 2) / sw, (canvasH - pad * 2) / sh);
        const dw = Math.max(1, Math.round(sw * scale));
        const dh = Math.max(1, Math.round(sh * scale));
        const dx = Math.floor((canvasW - dw) / 2);
        const dy = Math.floor((canvasH - dh) / 2);

        const canvas = document.createElement('canvas');
        canvas.width = canvasW;
        canvas.height = canvasH;
        const ctx = canvas.getContext('2d');
        ctx.fillStyle = '#FFFFFF';
        ctx.fillRect(0, 0, canvasW, canvasH);
        ctx.imageSmoothingEnabled = true;
        ctx.imageSmoothingQuality = 'high';
        ctx.drawImage(img, dx, dy, dw, dh);
        const dataUrl = canvas.toDataURL('image/jpeg', 0.92);
        URL.revokeObjectURL(objUrl);
        resolve(dataUrl);
      } catch (e) {
        URL.revokeObjectURL(objUrl);
        reject(e);
      }
    };
    img.onerror = () => {
      URL.revokeObjectURL(objUrl);
      reject(new Error('decode failed'));
    };
    img.src = objUrl;
  });
}

function readFileAsDataURL(file) {
  return new Promise((resolve, reject) => {
    const r = new FileReader();
    r.onload = () => resolve(r.result);
    r.onerror = reject;
    r.readAsDataURL(file);
  });
}

function renderUploadedThumbs() {
  const el = document.getElementById('uploaded-thumbs');
  el.innerHTML = state.uploadedImages.map((url, idx) => `
    <div class="thumb-item">
      <img src="${escAttr(safeImgSrc(url))}" alt="Ürün görseli" />
      <button type="button" class="thumb-remove" data-action="remove-image" data-idx="${idx}" title="Kaldır">×</button>
    </div>
  `).join('');
  const scanBtn = document.getElementById('btn-scan-vision');
  if (scanBtn) scanBtn.classList.toggle('hidden', state.uploadedImages.length === 0);
}

function removeUploadedImage(idx) {
  if (isNaN(idx)) return;
  state.uploadedImages.splice(idx, 1);
  if (state.uploadedFileNames.length > idx) state.uploadedFileNames.splice(idx, 1);
  renderUploadedThumbs();
  scheduleDraftSave(true);
}

function renderTargetMpChips() {
  const el = document.getElementById('target-mp-chips');
  el.innerHTML = state.marketplaces.map(m => {
    const sel = state.selectedTargetMps.includes(m.Id);
    return `
      <button type="button" class="mp-chip ${sel ? 'selected' : ''}" data-action="toggle-target-mp" data-id="${escAttr(m.Id)}">
        <span class="mp-chip-dot"></span>
        <span>${esc(m.Name)}</span>
      </button>
    `;
  }).join('');
}

function toggleTargetMp(id) {
  const idx = state.selectedTargetMps.indexOf(id);
  if (idx >= 0) state.selectedTargetMps.splice(idx, 1);
  else state.selectedTargetMps.push(id);
  renderTargetMpChips();
  scheduleDraftSave(true);
}

async function runAnalysis(autoPublish) {
  const prompt = document.getElementById('product-prompt').value.trim();
  if (!prompt && state.uploadedImages.length === 0) {
    toast('Lütfen en az bir ürün görseli yükleyin veya ürün hakkında kısa bir açıklama yazın.', 'error');
    return;
  }
  if (state.selectedTargetMps.length === 0) {
    toast('Lütfen en az bir hedef pazaryeri seçin.', 'error');
    return;
  }

  const statusEl = document.getElementById('analyze-status');
  const btnAnalyze = document.getElementById('btn-analyze');
  const btnAuto = document.getElementById('btn-autopilot');
  btnAnalyze.disabled = true;
  btnAuto.disabled = true;
  statusEl.textContent = 'Görseller taranıyor ve her pazaryeri için ayrı izole rakip & fiyat araştırması yapılıyor...';

  const priceVal = parseFloat(document.getElementById('opt-price').value);
  const stockVal = parseInt(document.getElementById('opt-stock').value, 10);

  try {
    const payload = {
      Prompt: prompt,
      Images: state.uploadedImages,
      OriginalFileNames: state.uploadedFileNames,
      TargetMarketplaces: state.selectedTargetMps,
      Price: isNaN(priceVal) ? null : priceVal,
      Stock: isNaN(stockVal) ? null : stockVal,
      Brand: document.getElementById('opt-brand').value.trim(),
      Barcode: document.getElementById('opt-barcode').value.trim()
    };

    const res = await apiFetch('/api/analyze', {
      method: 'POST',
      body: JSON.stringify(payload)
    });

    state.currentDraft = res.product;
    state.engineUsed = res.engineUsed || 'Smart SEO Motoru';
    state.activeListingTab = state.selectedTargetMps[0] || 'trendyol';

    renderGeneratedPreview(state.engineUsed);
    markSaved();
    statusEl.textContent = 'Analiz tamamlandı — tüm zorunlu alanlar dolduruldu ve kalıcı olarak kaydedildi.';

    if (autoPublish) {
      statusEl.textContent = 'Seçili pazaryerlerine otomatik yayınlanıyor...';
      await publishCurrentDraft();
    } else {
      toast('Rakip analizi ve 7 pazaryeri içeriği hazır!', 'success');
      document.getElementById('generated-area').scrollIntoView({ behavior: 'smooth', block: 'start' });
    }
  } catch (err) {
    statusEl.textContent = '';
    toast('Analiz hatası: ' + err.message, 'error');
  } finally {
    btnAnalyze.disabled = false;
    btnAuto.disabled = false;
  }
}

function renderGeneratedPreview(engineUsed) {
  const area = document.getElementById('generated-area');
  area.classList.remove('hidden');

  const p = state.currentDraft;
  const c = p.CompetitorData || {};
  const v = p.VisionData || null;

  const visionEl = document.getElementById('vision-summary-box');
  if (visionEl) {
    if (v && v.Analyzed) {
      const swatch = v.DominantColorHex ? `<span class="color-swatch" style="background:${escAttr(v.DominantColorHex)}"></span>` : '';
      const vFeats = (v.VisualFeatures || []).map(f => `<span class="kw-tag">${esc(f)}</span>`).join('');
      visionEl.innerHTML = `
        <div class="vision-head">
          <div class="vision-title">${swatch} Görsel Tarama: ${esc(v.DetectedProductName || p.BaseName)}</div>
          <span class="badge badge-info">${esc(v.EngineUsed || 'Smart Vision')}</span>
        </div>
        <div class="vision-meta">${esc(v.Summary || '')}</div>
        ${vFeats ? `<div class="keyword-list">${vFeats}</div>` : ''}
      `;
      visionEl.classList.remove('hidden');
    } else {
      visionEl.classList.add('hidden');
    }
  }

  document.getElementById('engine-badge').textContent = engineUsed || 'Smart SEO Motoru';
  document.getElementById('comp-summary').textContent = c.Summary || '';
  document.getElementById('comp-category').textContent = c.DetectedCategory || p.Category;
  document.getElementById('comp-price-band').textContent =
    `Önerilen Bant: ${fmtMoney(c.MinPrice)} — ${fmtMoney(c.MaxPrice)} (Ort: ${fmtMoney(c.AvgPrice)})`;

  document.getElementById('comp-keywords').innerHTML = (c.TrendingKeywords || [])
    .map(k => `<span class="kw-tag">${esc(k)}</span>`).join('');

  const autoList = c.AutoFilledHighlights && c.AutoFilledHighlights.length > 0
    ? c.AutoFilledHighlights
    : ['Promptunuzdaki tüm temel özellikler eksiksiz algılandı.'];
  document.getElementById('comp-autofilled').innerHTML = autoList.map(x => `<li>${esc(x)}</li>`).join('');

  document.getElementById('comp-samples').innerHTML = (c.Samples || [])
    .map(s => `<li><strong>${esc(s.Source)}:</strong> ${esc(s.Title)} <span class="muted-label">(${esc(s.PriceHint)})</span></li>`).join('');

  renderListingTabs();
  renderActiveListingEditor();

  const pubBtn = document.getElementById('btn-publish-draft');
  if (pubBtn && state.currentDraft) {
    const isEdit = state.products.some(pr => pr.Id === state.currentDraft.Id);
    pubBtn.textContent = isEdit ? 'Değişiklikleri Kaydet & Pazaryerlerinde Güncelle' : 'Seçili Pazaryerlerinde Yayınla';
  }
}

function renderListingTabs() {
  const p = state.currentDraft;
  const tabsEl = document.getElementById('listing-tabs');
  tabsEl.innerHTML = state.marketplaces.map(m => {
    const l = p.Listings[m.Id];
    if (!l) return '';
    const active = state.activeListingTab === m.Id;
    const mark = l.Selected ? '✓ ' : '';
    return `<button type="button" class="mp-tab ${active ? 'active' : ''}" data-action="select-listing-tab" data-id="${escAttr(m.Id)}">${mark}${esc(m.Name)}</button>`;
  }).join('');
}

function selectListingTab(mpId) {
  saveCurrentListingEditsFromDom();
  state.activeListingTab = mpId;
  renderListingTabs();
  renderActiveListingEditor();
  scheduleDraftSave(false);
}

function renderActiveListingEditor() {
  const p = state.currentDraft;
  const l = p.Listings[state.activeListingTab];
  const el = document.getElementById('listing-editor');
  if (!l) {
    el.innerHTML = '';
    return;
  }

  const attrsHtml = (l.Attributes || []).map((a, idx) => {
    const src = SOURCE_BADGES[a.Source] || SOURCE_BADGES.default;
    const hasOpts = Array.isArray(a.Options) && a.Options.length > 0 && a.AllowCustom === false;
    const fieldCtrl = hasOpts
      ? `<select class="select attr-input" data-attr-idx="${idx}">${a.Options.map(opt => `<option value="${escAttr(opt)}" ${opt === a.Value ? 'selected' : ''}>${esc(opt)}</option>`).join('')}</select>`
      : `<input type="text" class="input attr-input" data-attr-idx="${idx}" value="${escAttr(a.Value)}" />`;
    return `
      <div class="attr-card">
        <div class="attr-head">
          <span class="attr-label">${esc(a.Label)}${a.Required ? '<span class="req-star">*</span>' : ''}</span>
          <span class="badge ${src.cls}">${src.label}</span>
        </div>
        ${fieldCtrl}
      </div>
    `;
  }).join('');

  const tyCatBoxHtml = state.activeListingTab === 'trendyol' ? `
    <div class="ty-cat-box">
      <div class="ty-cat-head">
        <div>
          <div class="ty-cat-label">Trendyol İç Yaprak Kategorisi</div>
          <div class="ty-cat-current" id="ty-cat-current">${esc(l.CategoryName || p.Category)} <span class="badge badge-khaki">ID: ${esc(l.CategoryId)}</span></div>
        </div>
        <button type="button" class="btn btn-khaki-soft btn-sm" data-action="ty-auto-match">Trendyol Kategorisini Otomatik Eşleştir</button>
      </div>
      <div class="ty-cat-search-row">
        <input type="text" id="ty-cat-search" class="input" placeholder="Trendyol iç kategorisi ara (örn: Tencere, Çelik Tencere, Sweatshirt)..." />
      </div>
      <div id="ty-cat-results" class="ty-cat-results hidden"></div>
    </div>
  ` : '';

  const chRes = l.Research || null;
  const chResearchHtml = chRes ? `
    <div class="channel-research-box">
      <div class="channel-research-top">
        <div class="channel-research-title">${esc(l.MarketplaceName)} Kanalına Özel İzole Araştırma</div>
        <div style="display:flex; gap:6px; flex-wrap:wrap;">
          <span class="badge ${chRes.LiveDataFound ? 'badge-success' : 'badge-khaki'}">${chRes.LiveDataFound ? 'Canlı Kanal Verisi' : 'Kanal Zekâsı'}</span>
          <span class="badge badge-neutral">Çakışmasız İzole Şema</span>
        </div>
      </div>
      <div class="vision-meta">${esc(chRes.Summary || '')}</div>
      <div class="channel-research-grid">
        <div>
          <div class="comp-box-title" style="margin-bottom:5px;">Kanal Fiyat Bandı & Anahtar Kelimeler</div>
          <div class="price-band" style="margin-top:0;">${fmtMoney(chRes.MinPrice)} – ${fmtMoney(chRes.MaxPrice)} • Önerilen: ${fmtMoney(chRes.SuggestedPrice)}</div>
          <div class="keyword-list">${(chRes.ChannelKeywords || []).map(k => `<span class="kw-tag">${esc(k)}</span>`).join('')}</div>
        </div>
        <div>
          <div class="comp-box-title" style="margin-bottom:5px;">${esc(l.MarketplaceName)} Rakip Örnekleri</div>
          <ul class="clean-list">${(chRes.Samples || []).slice(0, 3).map(s => `<li>${esc(s.Title)} <span class="muted-label">(${esc(s.PriceHint)})</span></li>`).join('')}</ul>
        </div>
      </div>
    </div>
  ` : '';

  el.innerHTML = `
    <div style="display:flex; justify-content:space-between; align-items:center; margin-bottom:14px;">
      <label class="switch-label">
        <input type="checkbox" id="ed-selected" ${l.Selected ? 'checked' : ''} />
        <span><strong>${esc(l.MarketplaceName)}</strong> kanalında yayınlansın</span>
      </label>
      <span class="badge badge-neutral">Kategori ID: ${esc(l.CategoryId)} • Barkod: ${esc(p.Barcode)}</span>
    </div>
    ${chResearchHtml}
    ${tyCatBoxHtml}

    <div class="editor-grid">
      <div>
        <div class="input-group" style="margin-bottom:12px;">
          <label>
            SEO Ürün Başlığı
            <span class="char-meter" id="ed-title-meter">${l.Title.length} / ${l.MaxTitleLength} krk</span>
          </label>
          <input type="text" id="ed-title" class="input" maxlength="${l.MaxTitleLength}" value="${escAttr(l.Title)}" />
        </div>

        <div class="optional-row" style="grid-template-columns: 1fr 1fr 1fr; margin-bottom:12px;">
          <div class="input-group">
            <label>Satış Fiyatı (TL)</label>
            <input type="number" id="ed-price" class="input" step="0.01" value="${l.Price}" />
          </div>
          <div class="input-group">
            <label>Merkez Stok</label>
            <input type="number" id="ed-stock" class="input" value="${p.Stock}" />
          </div>
          <div class="input-group">
            <label>SKU / Stok Kodu</label>
            <input type="text" id="ed-sku" class="input" value="${escAttr(p.Sku)}" />
          </div>
        </div>

        <div class="input-group">
          <label>Profesyonel Ürün Açıklaması</label>
          <textarea id="ed-desc" class="textarea" rows="10">${esc(l.Description)}</textarea>
        </div>
      </div>

      <div>
        <label class="field-label">${esc(l.MarketplaceName)} Zorunlu Özellikleri</label>
        <div class="attr-grid">${attrsHtml}</div>
      </div>
    </div>
  `;

  const titleEl = document.getElementById('ed-title');
  titleEl.addEventListener('input', () => {
    document.getElementById('ed-title-meter').textContent = `${titleEl.value.length} / ${l.MaxTitleLength} krk`;
    scheduleDraftSave(false);
  });
  ['ed-price', 'ed-stock', 'ed-sku', 'ed-desc'].forEach(id => {
    document.getElementById(id).addEventListener('input', () => scheduleDraftSave(false));
  });
  document.getElementById('ed-selected').addEventListener('change', () => {
    saveCurrentListingEditsFromDom();
    renderListingTabs();
    scheduleDraftSave(true);
  });
  document.querySelectorAll('.attr-input').forEach(inp => {
    inp.addEventListener('input', () => scheduleDraftSave(false));
    inp.addEventListener('change', () => scheduleDraftSave(true));
  });

  const tySearchEl = document.getElementById('ty-cat-search');
  if (tySearchEl) {
    let tySearchTimer = null;
    tySearchEl.addEventListener('input', () => {
      clearTimeout(tySearchTimer);
      const q = tySearchEl.value.trim();
      const box = document.getElementById('ty-cat-results');
      if (q.length < 2) {
        if (box) box.classList.add('hidden');
        return;
      }
      tySearchTimer = setTimeout(async () => {
        try {
          const res = await apiFetch('/api/trendyol/categories/search', {
            method: 'POST',
            body: JSON.stringify({ query: q })
          });
          if (!box) return;
          const cats = res.categories || [];
          if (cats.length === 0) {
            box.innerHTML = '<div class="ty-cat-empty">Eşleşen Trendyol yaprak kategorisi bulunamadı.</div>';
          } else {
            box.innerHTML = cats.map(c => `
              <button type="button" class="ty-cat-option" data-action="ty-pick-cat" data-id="${c.Id}">
                <strong>${esc(c.Name)}</strong>
                <span>${esc(c.FullPath)} (ID: ${c.Id})</span>
              </button>
            `).join('');
          }
          box.classList.remove('hidden');
        } catch (err) {
          toast('Trendyol kategori araması başarısız: ' + err.message, 'error');
        }
      }, 300);
    });
  }
}

async function runTrendyolCategoryMatch(preferredCategoryId, btnEl) {
  if (!state.currentDraft) return;
  saveCurrentListingEditsFromDom();
  const l = state.currentDraft.Listings && state.currentDraft.Listings.trendyol;
  if (!l) return;

  const orig = btnEl ? btnEl.textContent : '';
  if (btnEl && preferredCategoryId === 0) {
    btnEl.disabled = true;
    btnEl.textContent = 'Eşleştiriliyor...';
  }
  try {
    const res = await apiFetch('/api/trendyol/categories/match', {
      method: 'POST',
      body: JSON.stringify({
        baseName: state.currentDraft.BaseName,
        title: l.Title,
        prompt: state.currentDraft.Prompt,
        category: state.currentDraft.Category,
        preferredCategoryId: preferredCategoryId || 0
      })
    });
    const m = res.match;
    if (m && m.Success) {
      l.CategoryId = String(m.CategoryId);
      l.CategoryName = m.FullPath;
      if (Array.isArray(m.ResolvedAttributes) && m.ResolvedAttributes.length > 0) {
        l.Attributes = m.ResolvedAttributes;
      }
      renderActiveListingEditor();
      scheduleDraftSave(true);
      toast(`Trendyol iç kategorisi eşleşti: ${m.CategoryName} (ID: ${m.CategoryId}) • ${m.Source}`, 'success');
    } else {
      toast('Kategori eşleştirilemedi: ' + ((m && m.Error) || 'Bilinmeyen hata'), 'error');
    }
  } catch (err) {
    toast('Trendyol kategori eşleştirme hatası: ' + err.message, 'error');
  } finally {
    if (btnEl && preferredCategoryId === 0) {
      btnEl.disabled = false;
      btnEl.textContent = orig;
    }
  }
}

function saveCurrentListingEditsFromDom() {
  if (!state.currentDraft) return;
  // Sync top-level product fields & uploaded images so edits never get dropped
  if (Array.isArray(state.uploadedImages) && state.uploadedImages.length > 0) {
    state.currentDraft.Images = [...state.uploadedImages];
  }
  const optBrand = (document.getElementById('opt-brand')?.value || '').trim();
  if (optBrand) state.currentDraft.Brand = optBrand;
  const optBarcode = (document.getElementById('opt-barcode')?.value || '').trim();
  if (optBarcode) state.currentDraft.Barcode = optBarcode;

  const l = state.currentDraft.Listings[state.activeListingTab];
  if (!l) return;
  const titleEl = document.getElementById('ed-title');
  if (!titleEl) return;

  l.Selected = document.getElementById('ed-selected').checked;
  l.Title = titleEl.value.trim();
  l.Description = document.getElementById('ed-desc').value;
  const pr = parseFloat(document.getElementById('ed-price').value);
  if (!isNaN(pr) && pr > 0) {
    l.Price = pr;
    state.currentDraft.Price = pr;
  }
  const st = parseInt(document.getElementById('ed-stock').value, 10);
  if (!isNaN(st) && st >= 0) state.currentDraft.Stock = st;
  const sku = document.getElementById('ed-sku').value.trim();
  if (sku) state.currentDraft.Sku = sku;

  document.querySelectorAll('.attr-input').forEach(inp => {
    const idx = parseInt(inp.dataset.attrIdx, 10);
    if (l.Attributes[idx]) l.Attributes[idx].Value = inp.value;
  });
}

async function publishCurrentDraft() {
  if (!state.currentDraft) return;
  saveCurrentListingEditsFromDom();

  const isEdit = state.products.some(pr => pr.Id === state.currentDraft.Id);
  const btn = document.getElementById('btn-publish-draft');
  btn.disabled = true;
  btn.textContent = isEdit ? 'Pazaryerlerinde Güncelleniyor...' : 'Yayınlanıyor...';

  try {
    const res = await apiFetch('/api/products/publish', {
      method: 'POST',
      body: JSON.stringify(state.currentDraft)
    });

    const failedItems = (res.results || []).filter(r => !r.success);
    if (res.results && res.results.length > 0) {
      const verb = res.updated ? 'Güncelleme' : 'Yayınlama';
      smartPublishSummaryModal({
        title: failedItems.length === 0 ? `${verb} Tamamlandı` : `${verb} Özeti`,
        subtitle: res.liveSuccessCount > 0
          ? `${res.liveSuccessCount} pazaryerine canlı gönderildi${res.simulatedCount > 0 ? `, ${res.simulatedCount} kanal Test Modunda işlendi` : ''}.`
          : (failedItems.length > 0 ? 'Bazı kanallarda dikkat edilmesi gereken bir durum oluştu.' : `${res.simulatedCount} kanal Test Modunda işlendi.`),
        results: res.results
      });
    } else {
      toast('Ürün kaydedildi.', 'success');
    }
    state.currentDraft = null;
    state.uploadedImages = [];
    document.getElementById('product-prompt').value = '';
    document.getElementById('opt-price').value = '';
    document.getElementById('opt-stock').value = '';
    document.getElementById('opt-brand').value = '';
    document.getElementById('opt-barcode').value = '';
    document.getElementById('analyze-status').textContent = '';
    document.getElementById('generated-area').classList.add('hidden');
    renderUploadedThumbs();
    markSaved();

    await loadState(false);
    switchView('inventory');
  } catch (err) {
    toast('Yayınlama hatası: ' + err.message, 'error');
  } finally {
    btn.disabled = false;
    btn.textContent = 'Seçili Pazaryerlerinde Yayınla';
  }
}

// ---------------- Inventory & Stock Sync ----------------
function bindInventoryEvents() {
  document.getElementById('inventory-search').addEventListener('input', renderInventory);
  document.getElementById('btn-sync-all-top').addEventListener('click', syncAllStocks);
  document.getElementById('btn-quick-sync').addEventListener('click', syncAllStocks);
  document.getElementById('btn-pull-orders')?.addEventListener('click', pullLiveOrders);
  document.getElementById('btn-pull-orders-2')?.addEventListener('click', pullLiveOrders);
  document.getElementById('btn-save-sync-cfg').addEventListener('click', () => saveSyncSettings(false));

  ['sync-auto-toggle', 'sync-auto-orders', 'sync-restore-cancel', 'sync-interval', 'sync-threshold'].forEach(id => {
    document.getElementById(id)?.addEventListener('change', () => {
      clearTimeout(syncSaveTimer);
      syncSaveTimer = setTimeout(() => saveSyncSettings(true), 300);
    });
  });
}

function renderInventory() {
  if (state.syncConfig) {
    document.getElementById('sync-auto-toggle').checked = !!state.syncConfig.AutoSyncEnabled;
    const ao = document.getElementById('sync-auto-orders');
    if (ao) ao.checked = state.syncConfig.AutoPullOrders !== false;
    const rc = document.getElementById('sync-restore-cancel');
    if (rc) rc.checked = state.syncConfig.RestoreStockOnCancel !== false;
    document.getElementById('sync-interval').value = String(state.syncConfig.IntervalMinutes || 5);
    document.getElementById('sync-threshold').value = state.syncConfig.LowStockThreshold || 5;
  }
  renderOrdersHistory();

  const query = (document.getElementById('inventory-search').value || '').toLowerCase();
  const threshold = (state.syncConfig && state.syncConfig.LowStockThreshold) || 5;
  const list = state.products.filter(p =>
    (p.BaseName || '').toLowerCase().includes(query) ||
    (p.Sku || '').toLowerCase().includes(query) ||
    (p.Barcode || '').toLowerCase().includes(query)
  );

  const container = document.getElementById('inventory-list');
  if (list.length === 0) {
    container.innerHTML = `
      <div class="card empty-state">
        <h3>Henüz ürün eklenmedi</h3>
        <p style="margin-bottom:14px;">"Ürün Ekle" ekranından görsel ve kısa bir açıklama girerek saniyeler içinde tüm pazaryerlerine ürün yükleyebilirsiniz.</p>
        <button class="btn btn-primary" data-nav="add-product">İlk Ürünü Ekle</button>
      </div>
    `;
    return;
  }

  container.innerHTML = list.map(p => {
    const thumb = (p.Images && p.Images.length > 0)
      ? `<img src="${escAttr(safeImgSrc(p.Images[0]))}" alt="" />`
      : `<span>Görsel</span>`;
    const isLow = p.Stock <= threshold;
    const stockBadge = p.Stock === 0
      ? '<span class="badge badge-danger">Tükendi</span>'
      : (isLow ? '<span class="badge badge-warning">Kritik Stok</span>' : '<span class="badge badge-success">Stok Eşitli</span>');

    const channels = state.marketplaces.map(m => {
      const l = p.Listings && p.Listings[m.Id];
      if (!l || !l.Selected) return '';
      const dotCls = l.Status === 'published' ? 'badge-success'
        : (l.Status === 'simulated' ? 'badge-khaki' : (l.Status === 'error' ? 'badge-danger' : 'badge-neutral'));
      const lbl = l.Status === 'published' ? 'Canlı' : (l.Status === 'simulated' ? 'Test' : (l.Status === 'error' ? 'Hata' : 'Taslak'));
      return `
        <div class="channel-pill" title="${escAttr(l.StatusMessage || '')}">
          <strong>${esc(m.Name)}</strong>
          <span>Stok: ${l.SyncedStock}</span>
          <span class="badge ${dotCls}">${lbl}</span>
        </div>
      `;
    }).join('');

    const liveAlerts = state.marketplaces.map(m => {
      const l = p.Listings && p.Listings[m.Id];
      if (!l || !l.Selected) return '';
      if (l.Status === 'error') {
        return `<div class="live-msg live-msg-err"><strong>${esc(m.Name)} Hata:</strong> ${esc(l.StatusMessage || '')}</div>`;
      }
      if (l.Status === 'published') {
        return `<div class="live-msg live-msg-ok"><strong>${esc(m.Name)} Canlı:</strong> ${esc(l.StatusMessage || '')} ${l.RemoteId ? `<span class="muted-label">(Batch: ${esc(l.RemoteId)})</span>` : ''}</div>`;
      }
      return '';
    }).join('');

    const pid = escAttr(p.Id);
    const tyListing = p.Listings && p.Listings.trendyol;
    const tyBatchBtn = (tyListing && tyListing.Status === 'published' && tyListing.RemoteId && !tyListing.RemoteId.startsWith('TRE-'))
      ? `<button type="button" class="btn btn-soft btn-sm" data-action="check-ty-batch" data-id="${pid}">Trendyol Durum</button>`
      : '';

    return `
      <div class="inv-card">
        <div class="inv-top">
          <div class="inv-info">
            <div class="inv-thumb">${thumb}</div>
            <div>
              <div class="inv-title">${esc(p.BaseName)} ${stockBadge}</div>
              <div class="inv-meta">SKU: ${esc(p.Sku)} • Barkod: ${esc(p.Barcode)} • Kategori: ${esc(p.Category)} • Fiyat: <strong>${fmtMoney(p.Price)}</strong></div>
            </div>
          </div>

          <div class="inv-controls">
            <div class="stock-stepper">
              <button type="button" data-action="stock-step" data-id="${pid}" data-delta="-1">−</button>
              <input type="number" id="stk-${pid}" value="${p.Stock}" min="0" />
              <button type="button" data-action="stock-step" data-id="${pid}" data-delta="1">+</button>
            </div>
            <button type="button" class="btn btn-primary btn-sm" data-action="quick-update-product" data-id="${pid}">Güncelle</button>
            <button type="button" class="btn btn-soft btn-sm" data-action="save-stock" data-id="${pid}">Stok Eşitle</button>
            <button type="button" class="btn btn-soft btn-sm" data-action="republish-live" data-id="${pid}">Canlı Yayınla</button>
            ${tyBatchBtn}
            <button type="button" class="btn btn-soft btn-sm" data-action="edit-product" data-id="${pid}">Detaylı Düzenle</button>
            <button type="button" class="btn btn-khaki-soft btn-sm" data-action="simulate-order" data-id="${pid}" title="Sipariş geldiğini simüle edip tüm diğer pazaryerlerinden stok düşer">Sipariş (-1)</button>
            <button type="button" class="btn btn-danger-soft btn-sm" data-action="delete-product" data-id="${pid}">Sil</button>
          </div>
        </div>
        <div class="inv-channels">${channels}</div>
        ${liveAlerts ? `<div class="live-alerts-wrap">${liveAlerts}</div>` : ''}
      </div>
    `;
  }).join('');
}

function editExistingProduct(id) {
  const prod = state.products.find(p => p.Id === id);
  if (!prod) return;
  state.currentDraft = JSON.parse(JSON.stringify(prod));
  state.uploadedImages = Array.isArray(prod.Images) ? [...prod.Images] : [];
  document.getElementById('product-prompt').value = prod.Prompt || prod.BaseName || '';
  document.getElementById('opt-price').value = prod.Price || '';
  document.getElementById('opt-stock').value = prod.Stock || '';
  document.getElementById('opt-brand').value = prod.Brand || '';
  document.getElementById('opt-barcode').value = prod.Barcode || '';
  state.activeListingTab = 'trendyol';

  renderUploadedThumbs();
  renderGeneratedPreview('Kayıtlı Ürün Düzenleme');
  switchView('add-product');
  scheduleDraftSave(true);
  toast('Ürün düzenleme için açıldı. Değişiklikleriniz otomatik kaydedilir.', 'info');
}

async function republishProductLive(id, btnEl) {
  const liveCount = state.marketplaces.filter(m => m.Enabled && !m.SimulationMode).length;
  if (liveCount === 0) {
    toast('Canlı API Modunda bağlı pazaryeri bulunmuyor. Önce API Bağlantıları ekranından anahtarlarınızı girip Canlı Modu açın.', 'error');
    switchView('apis');
    return;
  }

  const origText = btnEl ? btnEl.textContent : 'Canlı Yayınla';
  if (btnEl) { btnEl.disabled = true; btnEl.textContent = 'Gönderiliyor...'; }
  toast('Görseller yükleniyor, Trendyol kategori/özellikleri eşleştirilip canlı API\'ye gönderiliyor...', 'info');

  try {
    const res = await apiFetch('/api/products/republish', {
      method: 'POST',
      body: JSON.stringify({ productId: id, liveOnly: true })
    });
    markSaved();
    const failed = (res.results || []).filter(r => !r.success);
    await loadState(false);
    if (res.results && res.results.length > 0) {
      smartPublishSummaryModal({
        title: failed.length === 0 ? 'Canlı Yayın Başarılı' : 'Canlı Yayın Sonucu',
        subtitle: failed.length === 0
          ? `Ürününüz ${res.liveSuccessCount} pazaryerine canlı olarak iletildi.`
          : 'Aşağıdaki pazaryeri yanıtlarını inceleyebilirsiniz.',
        results: res.results
      });
    } else {
      toast('Gönderilecek aktif canlı kanal bulunamadı.', 'info');
    }
  } catch (err) {
    toast('Canlı yayın hatası: ' + err.message, 'error');
  } finally {
    if (btnEl) { btnEl.disabled = false; btnEl.textContent = origText; }
  }
}

async function checkTrendyolBatch(id, btnEl) {
  if (btnEl) { btnEl.disabled = true; btnEl.textContent = 'Sorgulanıyor...'; }
  try {
    const res = await apiFetch('/api/trendyol/check-batch', {
      method: 'POST',
      body: JSON.stringify({ productId: id })
    });
    toast(res.result.Message, res.ok ? 'success' : 'error');
    await loadState(false);
  } catch (err) {
    toast('Batch sorgu hatası: ' + err.message, 'error');
  } finally {
    if (btnEl) { btnEl.disabled = false; btnEl.textContent = 'Trendyol Durum'; }
  }
}

function adjustStockInput(id, delta) {
  const inp = document.getElementById('stk-' + id);
  if (!inp) return;
  const val = Math.max(0, (parseInt(inp.value, 10) || 0) + delta);
  inp.value = val;
}

async function saveStock(id) {
  const inp = document.getElementById('stk-' + id);
  const newStock = Math.max(0, parseInt(inp.value, 10) || 0);
  try {
    const res = await apiFetch('/api/stock/update', {
      method: 'POST',
      body: JSON.stringify({ productId: id, newStock, source: 'Merkez Stok Paneli' })
    });
    markSaved();
    toast(`Stok ${newStock} olarak güncellendi ve ${res.syncedChannels} pazaryerine eşitlendi.`, 'success');
    await loadState(false);
  } catch (err) {
    toast('Stok güncelleme hatası: ' + err.message, 'error');
  }
}

async function simulateOrder(id) {
  try {
    const res = await apiFetch('/api/stock/simulate-order', {
      method: 'POST',
      body: JSON.stringify({ productId: id, marketplaceId: 'trendyol', quantity: 1 })
    });
    markSaved();
    toast(`Trendyol siparişi işlendi! Yeni stok (${res.product.Stock}) ${res.syncedChannels} kanala eşitlendi.`, 'info');
    await loadState(false);
  } catch (err) {
    toast('Hata: ' + err.message, 'error');
  }
}

async function deleteProduct(id) {
  const prod = state.products.find(p => p.Id === id);
  const ok = await smartConfirm({
    eyebrow: 'Envanter',
    title: 'Ürünü kaldırmak istiyor musunuz?',
    message: prod
      ? `"${prod.BaseName}" ürünü Smart envanterinden kaldırılacaktır. Bu işlem geri alınamaz.`
      : 'Bu ürün Smart envanterinden kaldırılacaktır.',
    confirmText: 'Ürünü Kaldır',
    cancelText: 'Vazgeç',
    tone: 'danger'
  });
  if (!ok) return;
  try {
    await apiFetch('/api/products/' + encodeURIComponent(id), { method: 'DELETE' });
    markSaved();
    toast('Ürün envanterden kaldırıldı.', 'info');
    await loadState(false);
  } catch (err) {
    toast('Silme hatası: ' + err.message, 'error');
  }
}

async function syncAllStocks() {
  try {
    const res = await apiFetch('/api/stock/sync-all', { method: 'POST', body: '{}' });
    markSaved();
    toast(`Toplu senkronizasyon tamamlandı (${res.syncedOperations} kanal güncellendi).`, 'success');
    await loadState(false);
  } catch (err) {
    toast('Senkronizasyon hatası: ' + err.message, 'error');
  }
}

async function saveSyncSettings(silent = false) {
  try {
    const res = await apiFetch('/api/settings/sync', {
      method: 'POST',
      body: JSON.stringify({
        silent,
        AutoSyncEnabled: document.getElementById('sync-auto-toggle').checked,
        AutoPullOrders: document.getElementById('sync-auto-orders') ? document.getElementById('sync-auto-orders').checked : true,
        RestoreStockOnCancel: document.getElementById('sync-restore-cancel') ? document.getElementById('sync-restore-cancel').checked : true,
        IntervalMinutes: parseInt(document.getElementById('sync-interval').value, 10) || 5,
        LowStockThreshold: parseInt(document.getElementById('sync-threshold').value, 10) || 5
      })
    });
    state.syncConfig = res.syncConfig;
    renderSidebarStatus();
    markSaved();
    if (!silent) toast('Stok & sipariş senkronizasyon ayarları kaydedildi.', 'success');
  } catch (err) {
    if (!silent) toast('Ayar kaydedilemedi: ' + err.message, 'error');
  }
}

async function pullLiveOrders() {
  const btn1 = document.getElementById('btn-pull-orders');
  const btn2 = document.getElementById('btn-pull-orders-2');
  [btn1, btn2].forEach(b => { if (b) { b.disabled = true; b.dataset.orig = b.textContent; b.textContent = 'Taranıyor...'; } });
  try {
    const res = await apiFetch('/api/orders/pull', { method: 'POST', body: '{}' });
    const s = res.summary || {};
    markSaved();
    await loadState(false);
    if (s.ChannelsChecked === 0) {
      toast('Canlı API modunda bağlı pazaryeri yok. API Bağlantıları ekranından anahtarlarınızı ekleyip Canlı Modu açtığınızda siparişler otomatik çekilir.', 'info');
    } else {
      toast(`${s.ChannelsChecked} canlı kanal tarandı: ${s.NewOrdersProcessed || 0} yeni sipariş işlendi, ${s.StockSyncOperations || 0} kanal stoğu güncellendi.`, 'success');
    }
  } catch (err) {
    toast('Sipariş tarama hatası: ' + err.message, 'error');
  } finally {
    [btn1, btn2].forEach(b => { if (b) { b.disabled = false; b.textContent = b.dataset.orig || 'Siparişleri Çek'; } });
  }
}

function renderOrdersHistory() {
  const sub = document.getElementById('orders-last-pull-sub');
  if (sub && state.syncConfig && state.syncConfig.LastOrderPullAt) {
    sub.textContent = `Son sipariş kontrolü: ${state.syncConfig.LastOrderPullAt} • Herhangi bir pazaryerinde sipariş gelince diğer tüm kanallardan stok otomatik düşülür.`;
  }
  const el = document.getElementById('orders-history-list');
  if (!el) return;
  const orders = Array.isArray(state.orders) ? state.orders.slice(0, 25) : [];
  if (orders.length === 0) {
    el.innerHTML = '<div class="empty-state" style="padding:18px;">Henüz işlenen sipariş yok. Canlı API bağlı pazaryerlerinden gelen siparişler veya anlık webhook bildirimleri burada listelenir.</div>';
    return;
  }
  el.innerHTML = orders.map(o => {
    const srcCls = o.Source === 'Canlı API' ? 'badge-success' : (o.Source === 'Anlık Webhook' ? 'badge-info' : 'badge-khaki');
    const stCls = o.Restored ? 'badge-warning' : 'badge-neutral';
    return `
      <div class="order-sync-row">
        <div class="order-sync-left">
          <div class="order-sync-title">
            <span class="badge badge-khaki">${esc(o.MarketplaceName || o.MarketplaceId)}</span>
            <strong>#${esc(o.OrderNumber)}</strong>
            <span>${esc(o.ProductName)}</span>
            <span class="badge ${srcCls}">${esc(o.Source || 'API')}</span>
            <span class="badge ${stCls}">${esc(o.Status || 'İşlendi')}</span>
          </div>
          <div class="order-sync-meta">
            Adet: <strong>${o.Quantity}</strong> • Merkez Stok: <strong>${o.StockBefore} → ${o.StockAfter}</strong> • Eşitlenen Kanal: <strong>${o.SyncedChannels}</strong> • Barkod: ${esc(o.Barcode || '-')} • ${esc(o.ProcessedAt || '')}
          </div>
        </div>
      </div>
    `;
  }).join('');
}

function openQuickUpdateProductModal(id) {
  const p = state.products.find(x => x.Id === id);
  if (!p) return;

  const bodyHtml = `
    <p class="account-card-desc" style="margin-bottom:12px;">Burada yaptığınız değişiklikler hem Smart envanterinde kaydedilir hem de seçili tüm pazaryerlerine (fiyat, stok, başlık, KDV/Desi) <strong>anında gönderilir</strong>.</p>
    <div class="account-form">
      <label class="field-label">
        <span>Ürün Adı</span>
        <input type="text" id="qu-name" class="input" value="${escAttr(p.BaseName || '')}" />
      </label>
      <label class="login-remember" style="margin-top:-4px;">
        <input type="checkbox" id="qu-apply-title" checked />
        <span>Güncel ürün adını tüm pazaryeri başlıklarına da uygula</span>
      </label>
      <div class="account-form-row">
        <label class="field-label">
          <span>Satış Fiyatı (TL)</span>
          <input type="number" step="0.01" min="0.01" id="qu-price" class="input" value="${escAttr(p.Price || 0)}" />
        </label>
        <label class="field-label">
          <span>Liste Fiyatı (TL)</span>
          <input type="number" step="0.01" min="0" id="qu-list-price" class="input" value="${escAttr(p.ListPrice || p.Price || 0)}" />
        </label>
      </div>
      <div class="account-form-row">
        <label class="field-label">
          <span>Merkez Stok (Adet)</span>
          <input type="number" min="0" id="qu-stock" class="input" value="${escAttr(p.Stock || 0)}" />
        </label>
        <label class="field-label">
          <span>Marka</span>
          <input type="text" id="qu-brand" class="input" value="${escAttr(p.Brand || '')}" />
        </label>
      </div>
      <div class="account-form-row">
        <label class="field-label">
          <span>SKU / Stok Kodu</span>
          <input type="text" id="qu-sku" class="input" value="${escAttr(p.Sku || '')}" />
        </label>
        <label class="field-label">
          <span>Barkod</span>
          <input type="text" id="qu-barcode" class="input" value="${escAttr(p.Barcode || '')}" />
        </label>
      </div>
      <div class="account-form-row">
        <label class="field-label">
          <span>KDV Oranı (%)</span>
          <select id="qu-vat" class="input">
            <option value="20" ${p.VatRate === 20 ? 'selected' : ''}>%20</option>
            <option value="10" ${p.VatRate === 10 ? 'selected' : ''}>%10</option>
            <option value="1" ${p.VatRate === 1 ? 'selected' : ''}>%1</option>
            <option value="0" ${p.VatRate === 0 ? 'selected' : ''}>%0</option>
          </select>
        </label>
        <label class="field-label">
          <span>Desi</span>
          <input type="number" step="0.1" min="0.1" id="qu-desi" class="input" value="${escAttr(p.Desi || 1)}" />
        </label>
      </div>
      <label class="field-label">
        <span>Ortak Açıklama Güncelle (Boş bırakırsanız mevcut kanal açıklamaları korunur)</span>
        <textarea id="qu-desc" class="input" rows="2" placeholder="Tüm pazaryerlerinin açıklamasını değiştirmek isterseniz yazın..."></textarea>
      </label>
    </div>
  `;

  openSmartModal({
    eyebrow: 'Entegrasyon Ürün Güncelleme',
    title: 'Ürünü Güncelle & Pazaryerlerine Gönder',
    tone: 'brand',
    wide: true,
    bodyHtml,
    actions: [
      { label: 'Vazgeç', cls: 'btn-soft', onClick: () => {} },
      {
        label: 'Kaydet & Tüm Pazaryerlerini Güncelle',
        cls: 'btn-primary',
        onClick: async () => {
          const payload = {
            productId: id,
            baseName: (document.getElementById('qu-name')?.value || '').trim(),
            applyTitleToAll: !!(document.getElementById('qu-apply-title')?.checked),
            price: parseFloat(document.getElementById('qu-price')?.value || p.Price),
            listPrice: parseFloat(document.getElementById('qu-list-price')?.value || p.ListPrice || p.Price),
            stock: parseInt(document.getElementById('qu-stock')?.value || p.Stock, 10),
            brand: (document.getElementById('qu-brand')?.value || '').trim(),
            sku: (document.getElementById('qu-sku')?.value || '').trim(),
            barcode: (document.getElementById('qu-barcode')?.value || '').trim(),
            vatRate: parseInt(document.getElementById('qu-vat')?.value || '20', 10),
            desi: parseFloat(document.getElementById('qu-desi')?.value || '1'),
            description: (document.getElementById('qu-desc')?.value || '').trim()
          };
          toast('Ürün güncelleniyor ve tüm bağlı pazaryerlerine gönderiliyor...', 'info');
          try {
            const res = await apiFetch('/api/products/update', {
              method: 'POST',
              body: JSON.stringify(payload)
            });
            markSaved();
            await loadState(false);
            if (res.results && res.results.length > 0) {
              smartPublishSummaryModal({
                title: res.failCount === 0 ? 'Pazaryeri Güncellemesi Tamamlandı' : 'Güncelleme Özeti',
                subtitle: `${res.liveSuccessCount || 0} canlı kanal, ${res.simulatedCount || 0} test kanalı güncellendi.`,
                results: res.results
              });
            } else {
              toast('Ürün güncellendi.', 'success');
            }
          } catch (err) {
            toast('Güncelleme hatası: ' + err.message, 'error');
          }
        }
      }
    ]
  });
}

function updateSecurityBadges() {
  const sec = state.security || {};
  const encEl = document.getElementById('sec-badge-enc');
  const netEl = document.getElementById('sec-badge-net');
  if (encEl) encEl.textContent = sec.cloudMode ? 'AES-256-GCM Bulut Şifreleme Aktif' : 'DPAPI Şifreleme Aktif';
  if (netEl) netEl.textContent = sec.cloudMode ? 'Vercel HTTPS + Kullanıcı Hesabı Korumalı' : 'Yerel Ağ + Kullanıcı Hesabı Korumalı';
  renderWebhookUrl();
}

function renderWebhookUrl() {
  const mpSel = document.getElementById('webhook-mp-select');
  const urlInp = document.getElementById('webhook-url-display');
  if (!mpSel || !urlInp) return;
  const mp = mpSel.value || 'trendyol';
  const uname = (state.user && state.user.username) ? state.user.username : 'nihatstark';
  const tok = (state.syncConfig && state.syncConfig.WebhookToken) ? state.syncConfig.WebhookToken : '';
  const origin = window.location.origin;
  urlInp.value = `${origin}/api/webhooks/order/${encodeURIComponent(mp)}?user=${encodeURIComponent(uname)}&token=${encodeURIComponent(tok)}`;
}

// ---------------- API & AI Settings ----------------
function bindSettingsEvents() {
  document.getElementById('btn-save-ai').addEventListener('click', () => saveAiSettings(false));
  ['ai-provider', 'ai-tone', 'ai-gemini-key', 'ai-openai-key', 'ai-web-comp'].forEach(id => {
    const ev = (id === 'ai-gemini-key' || id === 'ai-openai-key') ? 'input' : 'change';
    document.getElementById(id).addEventListener(ev, () => {
      clearTimeout(aiSaveTimer);
      aiSaveTimer = setTimeout(() => saveAiSettings(true), 500);
    });
  });

  document.getElementById('log-filter').addEventListener('change', renderLogs);
  document.getElementById('btn-clear-logs').addEventListener('click', clearLogs);

  document.getElementById('webhook-mp-select')?.addEventListener('change', renderWebhookUrl);
  document.getElementById('btn-copy-webhook')?.addEventListener('click', () => {
    const val = document.getElementById('webhook-url-display')?.value || '';
    if (val) copyTextSafe(val, 'Anlık sipariş Webhook adresi kopyalandı.');
  });
  document.getElementById('btn-rotate-webhook')?.addEventListener('click', async () => {
    const ok = await smartConfirm({
      eyebrow: 'Webhook Güvenliği',
      title: 'Webhook anahtarı yenilensin mi?',
      message: 'Yeni anahtar üretildiğinde eski Webhook URL geçersiz olur; pazaryeri panelinizde yeni adresi güncellemeniz gerekir.',
      confirmText: 'Yenile',
      cancelText: 'Vazgeç',
      tone: 'danger'
    });
    if (!ok) return;
    try {
      const res = await apiFetch('/api/settings/webhook/rotate', { method: 'POST', body: '{}' });
      if (state.syncConfig) state.syncConfig.WebhookToken = res.webhookToken;
      renderWebhookUrl();
      toast('Webhook güvenlik anahtarı yenilendi.', 'success');
    } catch (err) {
      toast('Yenilenemedi: ' + err.message, 'error');
    }
  });
}

function renderApiPanel() {
  if (state.aiConfig) {
    document.getElementById('ai-provider').value = state.aiConfig.Provider || 'auto';
    document.getElementById('ai-tone').value = state.aiConfig.Tone || 'professional';
    document.getElementById('ai-gemini-key').value = state.aiConfig.GeminiApiKey || '';
    document.getElementById('ai-openai-key').value = state.aiConfig.OpenAiApiKey || '';
    document.getElementById('ai-web-comp').checked = !!state.aiConfig.EnableWebCompetitorAnalysis;
  }

  const grid = document.getElementById('api-cards-grid');
  grid.innerHTML = state.marketplaces.map(m => {
    const creds = m.Credentials || {};
    const mid = escAttr(m.Id);
    const fieldsHtml = Object.keys(creds).map(k => {
      const isSecret = k.toLowerCase().includes('secret') || k.toLowerCase().includes('password') || k.toLowerCase().includes('token') || k.toLowerCase().includes('key');
      const val = creds[k] || '';
      const ph = isSecret && val.startsWith('••••••••') ? 'Şifreli kayıtlı (değiştirmek için yazın)' : 'Giriniz...';
      return `
        <div class="input-group">
          <label>${esc(CRED_LABELS[k] || k)}</label>
          <input type="${isSecret ? 'password' : 'text'}" class="input mp-cred-input" autocomplete="off" data-mp="${mid}" data-cred="${escAttr(k)}" value="${escAttr(val)}" placeholder="${escAttr(ph)}" />
        </div>
      `;
    }).join('');

    const statusBadge = m.SimulationMode
      ? '<span class="badge badge-khaki">Test Modu</span>'
      : (m.Connected ? '<span class="badge badge-success">Canlı Bağlı</span>' : '<span class="badge badge-warning">Canlı — Test Bekliyor</span>');

    return `
      <div class="api-card">
        <div class="api-card-head">
          <div>
            <div class="api-card-title">${esc(m.Name)}</div>
            <div class="api-card-auth">${esc(m.AuthType)}</div>
          </div>
          ${statusBadge}
        </div>

        <div class="api-fields">${fieldsHtml}</div>

        <div class="api-status-line">${esc(m.StatusMessage || '')}</div>

        <div class="api-card-foot">
          <div style="display:flex; gap:14px; align-items:center;">
            <label class="switch-label">
              <input type="checkbox" class="mp-toggle-input" data-mp="${mid}" id="mp-en-${mid}" ${m.Enabled ? 'checked' : ''} />
              <span>Aktif</span>
            </label>
            <label class="switch-label">
              <input type="checkbox" class="mp-toggle-input" data-mp="${mid}" id="mp-live-${mid}" ${!m.SimulationMode ? 'checked' : ''} />
              <span>Canlı API Modu</span>
            </label>
          </div>
          <div style="display:flex; gap:8px;">
            <button type="button" class="btn btn-soft btn-sm" data-action="save-mp" data-id="${mid}">Kaydet</button>
            <button type="button" class="btn btn-primary btn-sm" data-action="test-mp" data-id="${mid}">Bağlantıyı Test Et</button>
          </div>
        </div>
      </div>
    `;
  }).join('');

  grid.querySelectorAll('.mp-cred-input').forEach(inp => {
    inp.addEventListener('input', () => scheduleMarketplaceSave(inp.dataset.mp));
  });
  grid.querySelectorAll('.mp-toggle-input').forEach(chk => {
    chk.addEventListener('change', () => scheduleMarketplaceSave(chk.dataset.mp));
  });
}

function scheduleMarketplaceSave(mpId) {
  clearTimeout(mpSaveTimers[mpId]);
  mpSaveTimers[mpId] = setTimeout(() => saveMarketplace(mpId, true), 500);
}

async function saveMarketplace(mpId, silent = false) {
  const mp = state.marketplaces.find(m => m.Id === mpId);
  if (!mp) return;
  const creds = {};
  document.querySelectorAll(`input[data-mp="${mpId}"][data-cred]`).forEach(inp => {
    creds[inp.dataset.cred] = inp.value.trim();
  });
  const enabled = document.getElementById('mp-en-' + mpId).checked;
  const isLive = document.getElementById('mp-live-' + mpId).checked;

  try {
    const res = await apiFetch('/api/marketplaces/save', {
      method: 'POST',
      body: JSON.stringify({
        silent,
        Id: mpId,
        Enabled: enabled,
        SimulationMode: !isLive,
        Credentials: creds
      })
    });
    const idx = state.marketplaces.findIndex(x => x.Id === mpId);
    if (idx >= 0 && res.marketplace) state.marketplaces[idx] = res.marketplace;
    const finalIsLive = res.marketplace ? !res.marketplace.SimulationMode : isLive;
    const liveChk = document.getElementById('mp-live-' + mpId);
    if (liveChk) liveChk.checked = finalIsLive;
    markSaved();
    if (!silent) {
      toast(`${mp.Name} ayarları şifrelenerek kaydedildi (${finalIsLive ? 'Canlı API Modu' : 'Test Modu'}).`, 'success');
      renderApiPanel();
      renderDashboard();
    }
  } catch (err) {
    if (!silent) toast('Kaydedilemedi: ' + err.message, 'error');
  }
}

async function testMarketplace(mpId) {
  await saveMarketplace(mpId, true);
  try {
    const res = await apiFetch('/api/marketplaces/test', {
      method: 'POST',
      body: JSON.stringify({ id: mpId })
    });
    markSaved();
    toast(res.result.Message, res.ok ? 'success' : 'error');
    await loadState(false);
    renderApiPanel();
  } catch (err) {
    toast('Test sonucu: ' + err.message, 'error');
    await loadState(false);
    renderApiPanel();
  }
}

async function saveAiSettings(silent = false) {
  try {
    const res = await apiFetch('/api/settings/ai', {
      method: 'POST',
      body: JSON.stringify({
        silent,
        Provider: document.getElementById('ai-provider').value,
        Tone: document.getElementById('ai-tone').value,
        GeminiApiKey: document.getElementById('ai-gemini-key').value.trim(),
        GeminiModel: (state.aiConfig && state.aiConfig.GeminiModel) || 'gemini-2.0-flash',
        OpenAiApiKey: document.getElementById('ai-openai-key').value.trim(),
        OpenAiModel: (state.aiConfig && state.aiConfig.OpenAiModel) || 'gpt-4o-mini',
        EnableWebCompetitorAnalysis: document.getElementById('ai-web-comp').checked
      })
    });
    state.aiConfig = res.aiConfig;
    markSaved();
    if (!silent) toast('Yapay zeka ve rakip analiz ayarları şifrelenerek kaydedildi.', 'success');
  } catch (err) {
    if (!silent) toast('Ayar kaydedilemedi: ' + err.message, 'error');
  }
}

// ---------------- Backup Export / Import ----------------
function bindBackupEvents() {
  document.getElementById('btn-export-backup').addEventListener('click', async () => {
    try {
      const res = await fetch('/api/backup/export', {
        headers: { 'X-Smart-Token': SESSION_TOKEN }
      });
      if (!res.ok) throw new Error('Yetkisiz veya başarısız yedekleme.');
      const blob = await res.blob();
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = 'smart-yedek-' + new Date().toISOString().slice(0, 10) + '.json';
      a.click();
      URL.revokeObjectURL(url);
      toast('Yedek indirildi (güvenlik gereği gizli API şifreleri yedek dosyasına dahil edilmez).', 'success');
    } catch (err) {
      toast('Yedek alınamadı: ' + err.message, 'error');
    }
  });

  const inp = document.getElementById('backup-file-input');
  document.getElementById('btn-import-backup').addEventListener('click', () => inp.click());
  inp.addEventListener('change', async e => {
    const file = e.target.files[0];
    inp.value = '';
    if (!file) return;
    if (file.size > 9 * 1024 * 1024) {
      toast('Yedek dosyası 9 MB sınırını aşıyor.', 'error');
      return;
    }
    try {
      const text = await file.text();
      await apiFetch('/api/backup/import', { method: 'POST', body: text });
      toast('Yedek doğrulandı ve başarıyla geri yüklendi!', 'success');
      state.initialized = false;
      await loadState(true);
    } catch (err) {
      toast('Yedek yüklenemedi: ' + err.message, 'error');
    }
  });
}

// ---------------- Logs ----------------
function renderLogs() {
  const filter = document.getElementById('log-filter').value;
  const list = filter === 'all' ? state.logs : state.logs.filter(l => l.Type === filter);
  const el = document.getElementById('logs-container');
  if (list.length === 0) {
    el.innerHTML = '<div class="empty-state">Bu filtrede kayıt bulunmuyor.</div>';
    return;
  }
  el.innerHTML = list.map(l => {
    const bCls = l.Level === 'success' ? 'badge-success'
      : (l.Level === 'error' ? 'badge-danger' : (l.Level === 'warning' ? 'badge-warning' : 'badge-khaki'));
    return `
      <div class="log-row">
        <div class="log-time">${esc(l.Timestamp)}</div>
        <div><span class="badge ${bCls}">${esc(l.Marketplace)}</span></div>
        <div>
          <div>${esc(l.Message)}</div>
          ${l.Details ? `<div class="log-details">${esc(l.Details)}</div>` : ''}
        </div>
      </div>
    `;
  }).join('');
}

async function clearLogs() {
  const ok = await smartConfirm({
    eyebrow: 'İşlem Kayıtları',
    title: 'Tüm kayıtları temizlemek istiyor musunuz?',
    message: 'Geçmiş işlem ve senkronizasyon günlükleri temizlenecektir. Ürünleriniz ve API ayarlarınız korunur.',
    confirmText: 'Kayıtları Temizle',
    cancelText: 'Vazgeç',
    tone: 'neutral'
  });
  if (!ok) return;
  try {
    await apiFetch('/api/logs/clear', { method: 'POST', body: '{}' });
    markSaved();
    toast('Kayıtlar temizlendi.', 'info');
    await loadState(false);
  } catch (err) {
    toast('Hata: ' + err.message, 'error');
  }
}

// ---------------- Smart Signature Popups & Toasts ----------------
const TOAST_META = {
  success: {
    title: 'Tamamlandı',
    icon: '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.4" stroke-linecap="round" stroke-linejoin="round"><polyline points="20 6 9 17 4 12"/></svg>'
  },
  error: {
    title: 'Dikkat',
    icon: '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><line x1="12" y1="8" x2="12" y2="12.5"/><line x1="12" y1="16" x2="12.01" y2="16"/></svg>'
  },
  info: {
    title: 'Smart',
    icon: '<svg width="15" height="15" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.1" stroke-linecap="round" stroke-linejoin="round"><circle cx="12" cy="12" r="9"/><line x1="12" y1="16" x2="12" y2="12"/><line x1="12" y1="8" x2="12.01" y2="8"/></svg>'
  }
};

function toast(msg, type = 'info', customTitle = '') {
  const c = document.getElementById('toast-container');
  if (!c) return;
  const meta = TOAST_META[type] || TOAST_META.info;
  const duration = type === 'error' ? 6200 : 4400;

  const t = document.createElement('div');
  t.className = 'toast ' + type;
  t.style.setProperty('--toast-duration', duration + 'ms');
  t.innerHTML = `
    <div class="toast-icon" aria-hidden="true">${meta.icon}</div>
    <div class="toast-content">
      <div class="toast-title">${esc(customTitle || meta.title)}</div>
      <div class="toast-msg">${esc(msg)}</div>
    </div>
    <button type="button" class="toast-close" aria-label="Kapat">×</button>
    <div class="toast-progress"></div>
  `;

  let remaining = duration;
  let startedAt = Date.now();
  let timerId = null;

  const dismiss = () => {
    if (t.classList.contains('leaving')) return;
    clearTimeout(timerId);
    t.classList.add('leaving');
    setTimeout(() => t.remove(), 220);
  };

  t.querySelector('.toast-close').addEventListener('click', dismiss);
  t.addEventListener('mouseenter', () => {
    clearTimeout(timerId);
    remaining = Math.max(400, remaining - (Date.now() - startedAt));
  });
  t.addEventListener('mouseleave', () => {
    startedAt = Date.now();
    timerId = setTimeout(dismiss, remaining);
  });

  // Cap visible toasts to 4 so the corner always stays calm & tidy
  while (c.children.length >= 4) {
    c.firstElementChild.remove();
  }
  c.appendChild(t);
  timerId = setTimeout(dismiss, duration);
}

let activeModalCleanup = null;

function openSmartModal({ eyebrow = 'Smart', title = '', bodyHtml = '', actions = [], tone = 'brand', wide = false, onCancel = null }) {
  if (activeModalCleanup) activeModalCleanup();

  const overlay = document.getElementById('smart-modal-overlay');
  const card = document.getElementById('smart-modal-card');
  const badge = document.getElementById('smart-modal-badge');
  const eyebrowEl = document.getElementById('smart-modal-eyebrow');
  const titleEl = document.getElementById('smart-modal-title');
  const bodyEl = document.getElementById('smart-modal-body');
  const actionsEl = document.getElementById('smart-modal-actions');
  const closeBtn = document.getElementById('smart-modal-close');

  card.classList.toggle('wide', !!wide);
  badge.className = 'smart-modal-badge' + (tone === 'danger' ? ' tone-danger' : (tone === 'success' ? ' tone-success' : ''));
  if (tone === 'danger') {
    badge.innerHTML = '<svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="3 6 5 6 21 6"/><path d="M19 6l-1 14a2 2 0 0 1-2 2H8a2 2 0 0 1-2-2L5 6"/><path d="M10 11v6"/><path d="M14 11v6"/><path d="M9 6V4a1 1 0 0 1 1-1h4a1 1 0 0 1 1 1v2"/></svg>';
  } else if (tone === 'success') {
    badge.innerHTML = '<svg width="20" height="20" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.2" stroke-linecap="round" stroke-linejoin="round"><polyline points="20 6 9 17 4 12"/></svg>';
  } else {
    badge.innerHTML = '<img src="/logo.png" alt="" class="smart-modal-logo" />';
  }

  eyebrowEl.textContent = eyebrow;
  titleEl.textContent = title;
  bodyEl.innerHTML = bodyHtml;
  actionsEl.innerHTML = '';

  const closeModal = (triggerCancel = false) => {
    overlay.classList.add('closing');
    document.removeEventListener('keydown', onKeyDown);
    overlay.removeEventListener('click', onBackdropClick);
    closeBtn.removeEventListener('click', onCloseClick);
    activeModalCleanup = null;
    setTimeout(() => {
      overlay.classList.add('hidden');
      overlay.classList.remove('closing');
    }, 170);
    if (triggerCancel && typeof onCancel === 'function') onCancel();
  };

  const onKeyDown = e => { if (e.key === 'Escape') closeModal(true); };
  const onBackdropClick = e => { if (e.target === overlay) closeModal(true); };
  const onCloseClick = () => closeModal(true);

  actions.forEach(act => {
    const btn = document.createElement('button');
    btn.type = 'button';
    btn.className = 'btn ' + (act.cls || 'btn-soft');
    btn.textContent = act.label;
    btn.addEventListener('click', () => {
      closeModal(false);
      if (typeof act.onClick === 'function') act.onClick();
    });
    actionsEl.appendChild(btn);
  });

  document.addEventListener('keydown', onKeyDown);
  overlay.addEventListener('click', onBackdropClick);
  closeBtn.addEventListener('click', onCloseClick);
  activeModalCleanup = () => {
    document.removeEventListener('keydown', onKeyDown);
    overlay.removeEventListener('click', onBackdropClick);
    closeBtn.removeEventListener('click', onCloseClick);
  };

  overlay.classList.remove('hidden', 'closing');
  const lastBtn = actionsEl.lastElementChild;
  if (lastBtn) lastBtn.focus();
}

function smartConfirm({ eyebrow = 'Onay', title = 'Emin misiniz?', message = '', confirmText = 'Onayla', cancelText = 'Vazgeç', tone = 'brand' }) {
  return new Promise(resolve => {
    openSmartModal({
      eyebrow,
      title,
      tone,
      bodyHtml: `<p>${esc(message)}</p>`,
      onCancel: () => resolve(false),
      actions: [
        { label: cancelText, cls: 'btn-soft', onClick: () => resolve(false) },
        { label: confirmText, cls: tone === 'danger' ? 'btn-danger-soft' : 'btn-primary', onClick: () => resolve(true) }
      ]
    });
  });
}

function smartPublishSummaryModal({ title, subtitle, results }) {
  const hasErr = (results || []).some(r => !r.success);
  const rowsHtml = (results || []).map(r => {
    const badgeCls = !r.success ? 'badge-danger' : (r.simulated ? 'badge-khaki' : 'badge-success');
    const badgeLbl = !r.success ? 'Hata' : (r.simulated ? 'Test Modu' : 'Canlı');
    return `
      <div class="publish-summary-row">
        <div class="publish-summary-left">
          <span class="publish-summary-name">${esc(r.marketplace)}</span>
          <span class="publish-summary-msg">${esc(r.message || '')}</span>
        </div>
        <span class="badge ${badgeCls}">${badgeLbl}</span>
      </div>
    `;
  }).join('');

  openSmartModal({
    eyebrow: 'Yayın Özeti',
    title,
    tone: hasErr ? 'brand' : 'success',
    wide: true,
    bodyHtml: `<p>${esc(subtitle)}</p><div class="publish-summary-list">${rowsHtml}</div>`,
    actions: [
      { label: 'Tamam', cls: 'btn-primary', onClick: () => {} }
    ]
  });
}

function fmtMoney(val) {
  const n = Number(val) || 0;
  return n.toLocaleString('tr-TR', { minimumFractionDigits: 0, maximumFractionDigits: 2 }) + ' TL';
}

function safeImgSrc(url) {
  if (!url) return '';
  const s = String(url);
  if (s.startsWith('/uploads/') || s.startsWith('https://')) return s;
  return '';
}

function esc(str) {
  return String(str == null ? '' : str)
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;');
}

function escAttr(str) {
  return esc(str).replace(/"/g, '&quot;').replace(/'/g, '&#39;');
}

// ==========================================================
// USER ACCOUNT, PROFILE & CUSTOMIZATION PANEL
// ==========================================================
const AVATAR_COLOR_CLASSES = ['av-khaki', 'av-slate', 'av-forest', 'av-amber', 'av-plum', 'av-terracotta'];

function setAvatarColorClass(el, colorName) {
  if (!el) return;
  AVATAR_COLOR_CLASSES.forEach(c => el.classList.remove(c));
  const cls = 'av-' + (colorName || 'khaki');
  el.classList.add(AVATAR_COLOR_CLASSES.includes(cls) ? cls : 'av-khaki');
}

function applyUserProfileToUi() {
  const p = state.userProfile || {};
  const u = state.user || {};

  const theme = p.Theme || 'khaki';
  const density = p.Density || 'comfortable';
  document.body.dataset.theme = theme;
  document.body.dataset.density = density;

  const panelTitle = p.PanelTitle || 'Smart';
  const storeName = p.StoreName || 'Pazaryeri Otomasyonu';
  const dispName = p.DisplayName || u.displayName || u.username || 'Kullanıcı';
  const handle = '@' + (u.username || 'kullanici');
  const initials = (p.AvatarInitials || dispName.slice(0, 2)).toUpperCase();
  const avColor = p.AvatarColor || 'khaki';

  document.title = `${panelTitle} — ${storeName}`;

  const sbName = document.getElementById('sidebar-brand-name');
  const sbSub = document.getElementById('sidebar-brand-sub');
  const mbName = document.getElementById('mobile-brand-name');
  if (sbName) sbName.textContent = panelTitle;
  if (sbSub) sbSub.textContent = storeName;
  if (mbName) mbName.textContent = panelTitle;

  const sbAv = document.getElementById('sidebar-user-avatar');
  const sbUserName = document.getElementById('sidebar-user-name');
  const sbHandle = document.getElementById('sidebar-user-handle');
  const mbAvBtn = document.getElementById('btn-mobile-account');
  const mbInit = document.getElementById('mobile-user-initials');

  if (sbAv) { sbAv.textContent = initials; setAvatarColorClass(sbAv, avColor); }
  if (sbUserName) sbUserName.textContent = dispName;
  if (sbHandle) sbHandle.textContent = handle;
  if (mbAvBtn) setAvatarColorClass(mbAvBtn, avColor);
  if (mbInit) mbInit.textContent = initials;
}

function renderAccountView() {
  const p = state.userProfile || {};
  const u = state.user || {};

  const dispName = p.DisplayName || u.displayName || u.username || '';
  const initials = (p.AvatarInitials || dispName.slice(0, 2)).toUpperCase();
  const avColor = p.AvatarColor || 'khaki';
  const theme = p.Theme || 'khaki';

  const heroAv = document.getElementById('acc-avatar-preview');
  const heroName = document.getElementById('acc-hero-name');
  const heroSub = document.getElementById('acc-hero-sub');
  const roleBadge = document.getElementById('acc-role-badge');
  if (heroAv) { heroAv.textContent = initials; setAvatarColorClass(heroAv, avColor); }
  if (heroName) heroName.textContent = dispName || 'Kullanıcı';
  if (heroSub) heroSub.textContent = `@${u.username || ''} • Kalıcı Bulut Hesabı`;
  if (roleBadge) roleBadge.textContent = u.isAdmin ? 'Yönetici' : 'Kullanıcı';

  document.querySelectorAll('#acc-swatch-row .swatch-btn').forEach(btn => {
    btn.classList.toggle('active', btn.dataset.avatarColor === avColor);
  });

  const setVal = (id, v) => { const el = document.getElementById(id); if (el) el.value = v || ''; };
  setVal('acc-display-name', p.DisplayName);
  setVal('acc-initials', p.AvatarInitials);
  setVal('acc-panel-title', p.PanelTitle || 'Smart');
  setVal('acc-store-name', p.StoreName || 'Pazaryeri Otomasyonu');
  setVal('acc-email', p.Email);
  setVal('acc-bio', p.Bio);
  setVal('acc-density', p.Density || 'comfortable');
  setVal('acc-default-view', p.DefaultView || 'dashboard');

  document.querySelectorAll('#acc-theme-grid .theme-card').forEach(btn => {
    btn.classList.toggle('active', btn.dataset.themePick === theme);
  });

  const uNameEl = document.getElementById('acc-info-username');
  const lastLoginEl = document.getElementById('acc-info-last-login');
  if (uNameEl) uNameEl.textContent = '@' + (u.username || '');
  if (lastLoginEl) lastLoginEl.textContent = u.lastLoginAt || 'Aktif oturum';

  // Admin user list card
  const adminCard = document.getElementById('card-admin-users');
  const usersList = document.getElementById('admin-users-list');
  if (adminCard && usersList) {
    if (u.isAdmin) {
      adminCard.classList.remove('hidden');
      const users = Array.isArray(u.users) ? u.users : [];
      usersList.innerHTML = users.map(item => {
        const isSelf = item.username === u.username;
        const init = (item.displayName || item.username || 'U').slice(0, 2).toUpperCase();
        return `
          <div class="account-user-row">
            <div class="account-user-row-left">
              <div class="user-avatar ${item.role === 'admin' ? 'av-khaki' : 'av-slate'}">${esc(init)}</div>
              <div>
                <div class="account-user-row-name">${esc(item.displayName || item.username)} <span class="badge ${item.role === 'admin' ? 'badge-khaki' : 'badge-neutral'}">${item.role === 'admin' ? 'Yönetici' : 'Kullanıcı'}</span></div>
                <div class="account-user-row-sub">@${esc(item.username)}${item.lastLoginAt ? ' • Son giriş: ' + esc(item.lastLoginAt) : ''}</div>
              </div>
            </div>
            ${isSelf ? '<span class="badge badge-success">Aktif</span>' : `<button type="button" class="btn btn-danger-soft btn-xs" data-action="delete-user" data-username="${escAttr(item.username)}">Sil</button>`}
          </div>
        `;
      }).join('');
    } else {
      adminCard.classList.add('hidden');
    }
  }
}

function collectAccountProfilePayload() {
  const activeSwatch = document.querySelector('#acc-swatch-row .swatch-btn.active');
  const activeTheme = document.querySelector('#acc-theme-grid .theme-card.active');
  return {
    DisplayName: (document.getElementById('acc-display-name')?.value || '').trim(),
    AvatarInitials: (document.getElementById('acc-initials')?.value || '').trim(),
    PanelTitle: (document.getElementById('acc-panel-title')?.value || '').trim() || 'Smart',
    StoreName: (document.getElementById('acc-store-name')?.value || '').trim() || 'Pazaryeri Otomasyonu',
    Email: (document.getElementById('acc-email')?.value || '').trim(),
    Bio: (document.getElementById('acc-bio')?.value || '').trim(),
    AvatarColor: activeSwatch ? activeSwatch.dataset.avatarColor : (state.userProfile?.AvatarColor || 'khaki'),
    Theme: activeTheme ? activeTheme.dataset.themePick : (state.userProfile?.Theme || 'khaki'),
    Density: document.getElementById('acc-density')?.value || 'comfortable',
    DefaultView: document.getElementById('acc-default-view')?.value || 'dashboard'
  };
}

async function saveUserProfile(successMsg) {
  const payload = collectAccountProfilePayload();
  try {
    const res = await apiFetch('/api/account/profile', {
      method: 'POST',
      body: JSON.stringify(payload)
    });
    state.userProfile = res.userProfile || payload;
    applyUserProfileToUi();
    renderAccountView();
    markSaved();
    toast(successMsg || 'Hesap ve özelleştirme ayarlarınız kaydedildi.', 'success');
  } catch (err) {
    toast('Kaydedilemedi: ' + err.message, 'error');
  }
}

async function handleLogout() {
  const confirmed = await smartConfirm({
    eyebrow: 'Oturum',
    title: 'Çıkış yapılsın mı?',
    message: 'Hesabınızdan çıkış yaptığınızda verileriniz ve API bağlantılarınız güvende kalır; dilediğiniz cihazdan tekrar giriş yapabilirsiniz.',
    confirmText: 'Çıkış Yap',
    cancelText: 'Vazgeç'
  });
  if (!confirmed) return;
  try {
    await fetch('/api/auth/logout', { method: 'POST', credentials: 'same-origin' });
  } catch { }
  window.location.replace('/?loggedout=' + Date.now());
}

async function handleLogoutAll() {
  const confirmed = await smartConfirm({
    eyebrow: 'Hesap Güvenliği',
    title: 'Diğer cihazlardaki oturumlar kapatılsın mı?',
    message: 'Bu cihazdaki oturumunuz açık kalır, diğer tüm tarayıcı ve telefonlardaki oturumlar anında sonlandırılır.',
    confirmText: 'Diğer Oturumları Kapat',
    cancelText: 'Vazgeç',
    tone: 'danger'
  });
  if (!confirmed) return;
  try {
    await apiFetch('/api/auth/logout-all', { method: 'POST', body: '{}' });
    toast('Diğer tüm cihazlardaki oturumlar başarıyla kapatıldı.', 'success');
  } catch (err) {
    toast('İşlem başarısız: ' + err.message, 'error');
  }
}

async function handleDeleteUser(username) {
  if (!username) return;
  const confirmed = await smartConfirm({
    eyebrow: 'Kullanıcı Yönetimi',
    title: `'${username}' kullanıcısı silinsin mi?`,
    message: 'Bu kullanıcı artık sisteme giriş yapamayacaktır.',
    confirmText: 'Kullanıcıyı Sil',
    cancelText: 'Vazgeç',
    tone: 'danger'
  });
  if (!confirmed) return;
  try {
    const res = await apiFetch('/api/account/users/delete', {
      method: 'POST',
      body: JSON.stringify({ username })
    });
    if (state.user) state.user.users = res.users || [];
    renderAccountView();
    toast(`'${username}' hesabı kaldırıldı.`, 'info');
  } catch (err) {
    toast('Silinemedi: ' + err.message, 'error');
  }
}

function bindAccountEvents() {
  document.getElementById('btn-sidebar-logout')?.addEventListener('click', handleLogout);
  document.getElementById('btn-account-logout')?.addEventListener('click', handleLogout);
  document.getElementById('btn-logout-all-devices')?.addEventListener('click', handleLogoutAll);

  // Live avatar color preview
  document.querySelectorAll('#acc-swatch-row .swatch-btn').forEach(btn => {
    btn.addEventListener('click', () => {
      document.querySelectorAll('#acc-swatch-row .swatch-btn').forEach(b => b.classList.remove('active'));
      btn.classList.add('active');
      setAvatarColorClass(document.getElementById('acc-avatar-preview'), btn.dataset.avatarColor);
    });
  });

  // Live initials preview
  document.getElementById('acc-initials')?.addEventListener('input', e => {
    const val = (e.target.value || '').toUpperCase().slice(0, 3);
    const prev = document.getElementById('acc-avatar-preview');
    if (prev && val) prev.textContent = val;
  });

  // Live theme preview on click
  document.querySelectorAll('#acc-theme-grid .theme-card').forEach(btn => {
    btn.addEventListener('click', () => {
      document.querySelectorAll('#acc-theme-grid .theme-card').forEach(b => b.classList.remove('active'));
      btn.classList.add('active');
      document.body.dataset.theme = btn.dataset.themePick || 'khaki';
    });
  });

  // Live density preview
  document.getElementById('acc-density')?.addEventListener('change', e => {
    document.body.dataset.density = e.target.value || 'comfortable';
  });

  document.getElementById('account-profile-form')?.addEventListener('submit', e => {
    e.preventDefault();
    saveUserProfile('Profil ve panel kimliğiniz tüm cihazlarınız için kaydedildi.');
  });

  document.getElementById('btn-save-appearance')?.addEventListener('click', () => {
    saveUserProfile('Tema ve görünüm özelleştirmeleriniz tüm cihazlarınız için kaydedildi.');
  });

  document.getElementById('account-password-form')?.addEventListener('submit', async e => {
    e.preventDefault();
    const curEl = document.getElementById('acc-cur-pwd');
    const newEl = document.getElementById('acc-new-pwd');
    const new2El = document.getElementById('acc-new-pwd2');
    const curPwd = curEl?.value || '';
    const newPwd = newEl?.value || '';
    const newPwd2 = new2El?.value || '';

    if (newPwd.length < 6) {
      toast('Yeni şifre en az 6 karakter olmalıdır.', 'error');
      return;
    }
    if (newPwd !== newPwd2) {
      toast('Yeni şifre ve tekrarı eşleşmiyor.', 'error');
      return;
    }
    try {
      await apiFetch('/api/account/password', {
        method: 'POST',
        body: JSON.stringify({ currentPassword: curPwd, newPassword: newPwd })
      });
      if (curEl) curEl.value = '';
      if (newEl) newEl.value = '';
      if (new2El) new2El.value = '';
      toast('Şifreniz güncellendi; diğer cihazlardaki eski oturumlar kapatıldı.', 'success');
    } catch (err) {
      toast('Şifre değiştirilemedi: ' + err.message, 'error');
    }
  });

  document.getElementById('admin-create-user-form')?.addEventListener('submit', async e => {
    e.preventDefault();
    const uEl = document.getElementById('new-user-username');
    const dEl = document.getElementById('new-user-display');
    const pEl = document.getElementById('new-user-password');
    const rEl = document.getElementById('new-user-role');
    try {
      const res = await apiFetch('/api/account/users/create', {
        method: 'POST',
        body: JSON.stringify({
          username: uEl?.value || '',
          displayName: dEl?.value || '',
          password: pEl?.value || '',
          role: rEl?.value || 'user'
        })
      });
      if (state.user) state.user.users = res.users || [];
      if (uEl) uEl.value = '';
      if (dEl) dEl.value = '';
      if (pEl) pEl.value = '';
      renderAccountView();
      toast('Yeni kullanıcı hesabı oluşturuldu.', 'success');
    } catch (err) {
      toast('Kullanıcı oluşturulamadı: ' + err.message, 'error');
    }
  });
}
