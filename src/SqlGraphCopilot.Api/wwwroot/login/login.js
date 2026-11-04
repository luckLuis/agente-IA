(() => {
    const form = document.getElementById('loginForm');
    const error = document.getElementById('error');
    const submit = document.getElementById('submit');

    // Si ya hay sesión, ir directo al dashboard (wwwroot).
    fetch('/api/auth/me', { credentials: 'same-origin' })
        .then(response => { if (response.ok) window.location.replace('/'); })
        .catch(() => { });

    form.addEventListener('submit', async (event) => {
        event.preventDefault();
        error.hidden = true;
        submit.disabled = true;

        try {
            const response = await fetch('/api/auth/login', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                credentials: 'same-origin',
                body: JSON.stringify({
                    username: form.username.value.trim(),
                    password: form.password.value
                })
            });

            if (response.ok) {
                window.location.replace('/');
                return;
            }

            showError(response.status === 429
                ? 'Demasiados intentos. Espera un minuto.'
                : 'Usuario o contraseña incorrectos.');
        } catch {
            showError('No se pudo contactar al servidor.');
        } finally {
            submit.disabled = false;
        }
    });

    function showError(message) {
        error.textContent = message;
        error.hidden = false;
        form.password.value = '';
        form.password.focus();
    }
})();
