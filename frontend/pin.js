// ==========================================================
// SMART — Remote HTTPS PIN Gate Controller (Zero inline JS)
// ==========================================================

document.addEventListener('DOMContentLoaded', () => {
  const form = document.getElementById('pin-form');
  const input = document.getElementById('pin-input');
  const errEl = document.getElementById('pin-error');
  const btn = document.getElementById('pin-submit');

  if (!form || !input) return;

  function showError(msg) {
    errEl.textContent = msg;
    errEl.classList.remove('hidden');
  }

  function clearError() {
    errEl.textContent = '';
    errEl.classList.add('hidden');
  }

  function extractSixDigits(val) {
    return String(val || '').replace(/\D+/g, '').slice(0, 6);
  }

  async function submitPin() {
    const pin = extractSixDigits(input.value);
    input.value = pin;
    clearError();

    if (pin.length < 6) {
      showError('Lütfen 6 haneli güvenlik PIN kodunu eksiksiz girin.');
      input.focus();
      return;
    }

    btn.disabled = true;
    btn.textContent = 'Doğrulanıyor...';
    try {
      const res = await fetch('/api/auth/pin', {
        method: 'POST',
        credentials: 'same-origin',
        cache: 'no-store',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ pin })
      });
      const data = await res.json().catch(() => ({}));
      if (!res.ok || data.ok === false) {
        showError(data.error || ('PIN doğrulanamadı (HTTP ' + res.status + '). Lütfen tekrar deneyin.'));
        input.select();
        input.focus();
        btn.disabled = false;
        btn.textContent = 'Panele Giriş Yap';
        return;
      }

      // Verify browser actually accepted the HttpOnly session cookie before redirecting
      btn.textContent = 'Oturum Açılıyor...';
      try {
        const chk = await fetch('/api/version?t=' + Date.now(), {
          credentials: 'same-origin',
          cache: 'no-store'
        });
        if (chk.status === 401) {
          showError('Bu tarayıcı oturum çerezini engelliyor (uygulama içi tarayıcı olabilir). Lütfen bu adresi doğrudan Chrome veya Safari ile açın.');
          btn.disabled = false;
          btn.textContent = 'Panele Giriş Yap';
          return;
        }
      } catch { }

      window.location.replace('/?ok=' + Date.now());
    } catch {
      showError('Sunucuya ulaşılamadı. Bilgisayarınızda Smart\'ın açık ve bağlantının aktif olduğundan emin olun.');
      btn.disabled = false;
      btn.textContent = 'Panele Giriş Yap';
    }
  }

  input.addEventListener('input', () => {
    const cleaned = extractSixDigits(input.value);
    if (input.value !== cleaned) input.value = cleaned;
    clearError();
  });

  input.addEventListener('paste', e => {
    const pasted = (e.clipboardData || window.clipboardData)?.getData('text') || '';
    if (!pasted) return;
    e.preventDefault();
    input.value = extractSixDigits(pasted);
    clearError();
  });

  form.addEventListener('submit', e => {
    e.preventDefault();
    submitPin();
  });
});
