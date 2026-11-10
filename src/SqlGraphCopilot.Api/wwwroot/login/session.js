// OPCIONAL. Incluir en wwwroot/index.html ANTES de tus otros scripts:
//   <script src="/login/session.js"></script>
// - Si cualquier fetch a la API devuelve 401 (sesión vencida), vuelve al login.
// - Cualquier elemento con el atributo data-logout cierra sesión al hacer clic.
(() => {
    const LOGIN_URL = '/login/';
    const originalFetch = window.fetch.bind(window);

    window.fetch = async (input, init) => {
        const response = await originalFetch(input, init);
        const url = input instanceof Request ? input.url : String(input);

        if (response.status === 401 && !url.includes('/api/auth/login')) {
            window.location.replace(LOGIN_URL);
        }
        return response;
    };

    async function logout() {
        try {
            await originalFetch('/api/auth/logout', { method: 'POST', credentials: 'same-origin' });
        } finally {
            window.location.replace(LOGIN_URL);
        }
    }

    document.addEventListener('click', (event) => {
        const target = event.target instanceof Element ? event.target.closest('[data-logout]') : null;
        if (target) {
            event.preventDefault();
            logout();
        }
    });
})();
