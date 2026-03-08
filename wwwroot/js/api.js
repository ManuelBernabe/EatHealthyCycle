const API = {
    baseUrl: '',
    token: null,
    user: null,

    init() {
        this.token = localStorage.getItem('token');
        this.user = JSON.parse(localStorage.getItem('user') || 'null');
    },

    setAuth(accessToken, refreshToken, user) {
        this.token = accessToken;
        this.user = user;
        localStorage.setItem('token', accessToken);
        localStorage.setItem('refreshToken', refreshToken);
        localStorage.setItem('user', JSON.stringify(user));
    },

    logout() {
        this.token = null;
        this.user = null;
        localStorage.clear();
    },

    isLoggedIn() {
        return !!this.token;
    },

    async request(method, path, body, isFormData) {
        const headers = {};
        if (this.token) headers['Authorization'] = `Bearer ${this.token}`;
        if (!isFormData) headers['Content-Type'] = 'application/json';

        const opts = { method, headers };
        if (body) opts.body = isFormData ? body : JSON.stringify(body);

        let res = await fetch(this.baseUrl + path, opts);

        // Token expired - try refresh
        if (res.status === 401 && this.token) {
            const refreshed = await this.tryRefresh();
            if (refreshed) {
                headers['Authorization'] = `Bearer ${this.token}`;
                opts.headers = headers;
                res = await fetch(this.baseUrl + path, opts);
            } else {
                this.logout();
                App.showPage('login');
                throw new Error('Sesión expirada');
            }
        }

        if (!res.ok) {
            const err = await res.json().catch(() => ({ error: 'Error del servidor' }));
            throw new Error(err.error || err.title || 'Error');
        }

        const text = await res.text();
        return text ? JSON.parse(text) : null;
    },

    async tryRefresh() {
        const refreshToken = localStorage.getItem('refreshToken');
        if (!refreshToken) return false;
        try {
            const res = await fetch(this.baseUrl + '/auth/refresh', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ refreshToken })
            });
            if (!res.ok) return false;
            const data = await res.json();
            this.setAuth(data.accessToken, data.refreshToken, data.user);
            return true;
        } catch { return false; }
    },

    // Auth
    login: (username, password) => API.request('POST', '/auth/login', { username, password }),
    register: (username, email, password) => API.request('POST', '/auth/register', { username, email, password }),
    me: () => API.request('GET', '/auth/me'),

    // Dietas
    importarDieta: (usuarioId, formData) => API.request('POST', `/api/usuarios/${usuarioId}/dietas/importar`, formData, true),
    listarDietas: (usuarioId) => API.request('GET', `/api/usuarios/${usuarioId}/dietas`),
    obtenerDieta: (id) => API.request('GET', `/api/dietas/${id}`),
    eliminarDieta: (id) => API.request('DELETE', `/api/dietas/${id}`),

    // Planes
    crearPlan: (usuarioId, dietaId, fechaInicio) => API.request('POST', `/api/usuarios/${usuarioId}/planes`, { dietaId, fechaInicio }),
    crearPlanManual: (usuarioId, nombre, fechaInicio) => API.request('POST', `/api/usuarios/${usuarioId}/planes/manual`, { nombre, fechaInicio }),
    addComidaPlan: (planDiaId, tipo, descripcion) => API.request('POST', `/api/plandia/${planDiaId}/comidas`, { tipo, descripcion }),
    deleteComidaPlan: (comidaId) => API.request('DELETE', `/api/plancomidas/${comidaId}`),
    listarPlanes: (usuarioId) => API.request('GET', `/api/usuarios/${usuarioId}/planes`),
    obtenerPlan: (id) => API.request('GET', `/api/planes/${id}`),
    eliminarPlan: (id) => API.request('DELETE', `/api/planes/${id}`),
    toggleComida: (id) => API.request('PUT', `/api/plancomidas/${id}/completar`),
    cumplimiento: (planId) => API.request('GET', `/api/planes/${planId}/cumplimiento`),

    // Peso
    registrarPeso: (usuarioId, fecha, peso, nota) => API.request('POST', `/api/usuarios/${usuarioId}/peso`, { fecha, peso, nota }),
    listarPeso: (usuarioId) => API.request('GET', `/api/usuarios/${usuarioId}/peso`),
    eliminarPeso: (id) => API.request('DELETE', `/api/peso/${id}`),

    // Lista compra
    generarListaCompra: (planId) => API.request('POST', `/api/planes/${planId}/lista-compra`),
    obtenerListaCompra: (planId) => API.request('GET', `/api/planes/${planId}/lista-compra`),
    toggleComprado: (itemId) => API.request('PUT', `/api/lista-compra/${itemId}`),
    addItemCompra: (planId, nombre, cantidad, categoria) => API.request('POST', `/api/planes/${planId}/lista-compra/item`, { nombre, cantidad, categoria }),
    deleteItemCompra: (itemId) => API.request('DELETE', `/api/lista-compra/${itemId}`),

    // Admin - Users
    listarUsuarios: () => API.request('GET', '/users'),
    crearUsuario: (data) => API.request('POST', '/users', data),
    actualizarUsuario: (id, data) => API.request('PUT', `/users/${id}`, data),
    eliminarUsuario: (id) => API.request('DELETE', `/users/${id}`),
    resetPassword: (id, newPassword) => API.request('PUT', `/users/${id}/password`, { newPassword }),
    impersonar: (id) => API.request('POST', `/auth/impersonate/${id}`),

    // Profile
    actualizarPerfil: (data) => API.request('PUT', '/me/profile', data),
    cambiarPassword: (data) => API.request('PUT', '/me/password', data),
    get2FAStatus: () => API.request('GET', '/me/2fa/status'),
    setup2FA: () => API.request('POST', '/me/2fa/setup'),
    confirm2FA: (code) => API.request('POST', '/me/2fa/confirm', { code }),
    disable2FA: (password) => API.request('POST', '/me/2fa/disable', { password })
};
