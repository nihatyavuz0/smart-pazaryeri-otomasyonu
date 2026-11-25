// ==========================================================
// SMART — User Login Gate Controller (Zero inline JS, CSP strict)
// ==========================================================

document.addEventListener('DOMContentLoaded', () => {
  const form = document.getElementById('login-form');
  const userInput = document.getElementById('login-username');
  const passInput = document.getElementById('login-password');
  const rememberInput = document.getElementById('login-remember');
  const toggleBtn = document.getElementById('btn-toggle-pwd');
  const errEl = document.getElementById('login-error');
  const submitBtn = document.getElementById('login-submit');

  if (!form || !userInput || !passInput) return;

  function showError(msg) {
    errEl.textContent = msg;
    errEl.classList.remove('hidden');
  }

  function clearError() {
    errEl.textContent = '';
    errEl.classList.add('hidden');
  }

  if (toggleBtn) {
    toggleBtn.addEventListener('click', () => {
      const showing = passInput.type === 'text';
      passInput.type = showing ? 'password' : 'text';
      toggleBtn.textContent = showing ? 'Göster' : 'Gizle';
      passInput.focus();
    });
  }

  userInput.addEventListener('input', clearError);
  passInput.addEventListener('input', clearError);

  form.addEventListener('submit', async e => {
    e.preventDefault();
    clearError();

    const username = (userInput.value || '').trim();
    const password = passInput.value || '';
    const remember = !!(rememberInput && rememberInput.checked);

    if (!username || !password) {
      showError('Lütfen kullanıcı adı ve şifrenizi girin.');
      if (!username) userInput.focus();
      else passInput.focus();
      return;
    }

    submitBtn.disabled = true;
    submitBtn.textContent = 'Giriş Yapılıyor...';

    try {
      const res = await fetch('/api/auth/login', {
        method: 'POST',
        credentials: 'same-origin',
        cache: 'no-store',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ username, password, remember })
      });
      const data = await res.json().catch(() => ({}));
      if (!res.ok || data.ok === false) {
        showError(data.error || ('Giriş yapılamadı (HTTP ' + res.status + '). Lütfen bilgilerinizi kontrol edin.'));
        submitBtn.disabled = false;
        submitBtn.textContent = 'Panele Giriş Yap';
        passInput.select();
        passInput.focus();
        return;
      }

      submitBtn.textContent = 'Hesap Yükleniyor...';
      try {
        const chk = await fetch('/api/version?t=' + Date.now(), {
          credentials: 'same-origin',
          cache: 'no-store'
        });
        if (chk.status === 401) {
          showError('Bu tarayıcı oturum çerezini engelliyor. Lütfen sayfayı doğrudan Chrome, Edge veya Safari ile açın.');
          submitBtn.disabled = false;
          submitBtn.textContent = 'Panele Giriş Yap';
          return;
        }
      } catch { }

      window.location.replace('/?ok=' + Date.now());
    } catch {
      showError('Sunucuya bağlanılamadı. İnternet bağlantınızı kontrol edip tekrar deneyin.');
      submitBtn.disabled = false;
      submitBtn.textContent = 'Panele Giriş Yap';
    }
  });
});
