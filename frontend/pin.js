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

  async function submitPin() {
    const pin = (input.value || '').replace(/\D+/g, '').slice(0, 6);
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
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ pin })
      });
      const data = await res.json().catch(() => ({}));
      if (!res.ok || data.ok === false) {
        showError(data.error || 'PIN doğrulanamadı. Lütfen tekrar deneyin.');
        input.select();
        input.focus();
        btn.disabled = false;
        btn.textContent = 'Panele Giriş Yap';
        return;
      }
      btn.textContent = 'Giriş Başarılı...';
      window.location.replace('/');
    } catch {
      showError('Sunucuya ulaşılamadı. Bilgisayarınızda Smart\'ın açık olduğundan emin olun.');
      btn.disabled = false;
      btn.textContent = 'Panele Giriş Yap';
    }
  }

  input.addEventListener('input', () => {
    const cleaned = (input.value || '').replace(/\D+/g, '').slice(0, 6);
    if (input.value !== cleaned) input.value = cleaned;
    clearError();
  });

  form.addEventListener('submit', e => {
    e.preventDefault();
    submitPin();
  });
});
