const App = {
    currentPage: null,
    currentPlan: null,
    currentDayIndex: 0,

    init() {
        API.init();
        EcOffline.initListeners();
        window.addEventListener('ec-synced', (e) => {
            EcOffline.showSyncToast('Sincronizado ✓');
            if (this.currentPage === 'plan') this.loadPlan();
        });
        if (API.isLoggedIn()) {
            if (!API.user) {
                // Token present but user data missing — force clean re-login
                API.logout();
                this.showPage('login');
            } else {
                this.showPage('dashboard');
                this.loadDashboard();
                this.updateAdminNav();
                this.checkForUpdates();
            }
        } else {
            this.showPage('login');
        }
        this.bindNav();
    },

    updateAdminNav() {
        const role = API.user?.role;
        const adminBtn = document.querySelector('.nav-admin');
        if (adminBtn) {
            adminBtn.style.display = (role === 'Admin' || role === 'Superuser' || role === 'SuperUserMaster') ? 'flex' : 'none';
        }
    },

    showPage(name) {
        document.querySelectorAll('.page').forEach(p => p.classList.remove('active'));
        const page = document.getElementById(`page-${name}`);
        if (page) page.classList.add('active');
        document.querySelectorAll('.nav-item').forEach(n => n.classList.remove('active'));
        const nav = document.querySelector(`[data-page="${name}"]`);
        if (nav) nav.classList.add('active');
        const bottomNav = document.getElementById('bottom-nav');
        if (bottomNav) bottomNav.style.display = name === 'login' || name === 'register' ? 'none' : 'flex';
        this.currentPage = name;
    },

    bindNav() {
        document.querySelectorAll('.nav-item').forEach(btn => {
            btn.addEventListener('click', () => {
                const page = btn.dataset.page;
                this.showPage(page);
                if (page === 'dashboard') this.loadDashboard();
                if (page === 'plan') this.loadPlan();
                if (page === 'peso') this.loadPeso();
                if (page === 'compra') this.loadCompra();
                if (page === 'perfil') this.loadPerfil();
                if (page === 'admin') this.loadAdmin();
            });
        });
    },

    toast(msg, type = 'success') {
        const el = document.getElementById('toast');
        el.textContent = msg;
        el.className = `toast ${type} show`;
        setTimeout(() => el.classList.remove('show'), 3000);
    },

    // --- AUTH ---
    async login() {
        const username = document.getElementById('login-username').value;
        const password = document.getElementById('login-password').value;
        if (!username || !password) return this.toast('Completa todos los campos', 'error');
        try {
            const btn = document.getElementById('btn-login');
            btn.disabled = true;
            btn.textContent = 'Entrando...';
            const res = await API.login(username, password);
            if (res.requires2FA) {
                this.toast('2FA requerido (no implementado en frontend aún)', 'error');
                return;
            }
            API.setAuth(res.accessToken, res.refreshToken, res.user);
            this.updateAdminNav();
            this.showPage('dashboard');
            this.loadDashboard();
            this.toast('Bienvenido ' + res.user.username);
        } catch (e) {
            this.toast(e.message, 'error');
        } finally {
            const btn = document.getElementById('btn-login');
            btn.disabled = false;
            btn.textContent = 'Entrar';
        }
    },

    async register() {
        const username = document.getElementById('reg-username').value;
        const email = document.getElementById('reg-email').value;
        const password = document.getElementById('reg-password').value;
        if (!username || !email || !password) return this.toast('Completa todos los campos', 'error');
        try {
            await API.register(username, email, password);
            this.toast('Registro exitoso. Revisa tu email para activar la cuenta.');
            this.showLoginForm();
        } catch (e) {
            this.toast(e.message, 'error');
        }
    },

    showRegisterForm() {
        document.getElementById('login-form').style.display = 'none';
        document.getElementById('register-form').style.display = 'block';
    },

    showLoginForm() {
        document.getElementById('login-form').style.display = 'block';
        document.getElementById('register-form').style.display = 'none';
    },

    logout() {
        API.logout();
        this.showPage('login');
    },

    // --- DASHBOARD ---
    async loadDashboard() {
        const uid = API.user?.id;
        if (!uid) return;
        try {
            const [dietas, planes, pesos] = await Promise.all([
                API.listarDietas(uid),
                API.listarPlanes(uid),
                API.listarPeso(uid)
            ]);

            document.getElementById('stat-dietas').textContent = dietas.length;
            document.getElementById('stat-planes').textContent = planes.length;
            document.getElementById('stat-registros').textContent = pesos.length;

            const lastPeso = pesos.length > 0 ? pesos[pesos.length - 1].peso + ' kg' : '--';
            document.getElementById('stat-peso').textContent = lastPeso;

            document.getElementById('dashboard-user').textContent = API.user.username;

            // Load compliance if there's an active plan
            if (planes.length > 0) {
                const lastPlan = planes[0];
                try {
                    const c = await API.cumplimiento(lastPlan.id);
                    document.getElementById('stat-cumplimiento').textContent = c.porcentajeCumplimiento + '%';
                    document.getElementById('progress-fill').style.width = c.porcentajeCumplimiento + '%';
                } catch { }
                this.currentPlan = lastPlan;
            }

            this.renderDietasList(dietas);
        } catch (e) {
            this.toast(e.message, 'error');
        }
    },

    renderDietasList(dietas) {
        const container = document.getElementById('dietas-list');
        if (dietas.length === 0) {
            container.innerHTML = '<p style="color:var(--text-secondary);text-align:center;padding:16px;">No hay dietas importadas</p>';
            return;
        }
        container.innerHTML = dietas.map(d => `
            <div class="card" style="margin:8px 0;">
                <div style="display:flex;justify-content:space-between;align-items:center;">
                    <div>
                        <strong>${d.nombre}</strong>
                        <div style="font-size:12px;color:var(--text-secondary);">${new Date(d.fechaImportacion).toLocaleDateString('es')}</div>
                    </div>
                    <div style="display:flex;gap:4px;flex-wrap:wrap;">
                        <button class="btn btn-sm btn-outline" onclick="App.verDietaDetalle(${d.id}, this)">Ver</button>
                        <button class="btn btn-sm" style="background:#9C27B0;color:white;" onclick="App.editarDieta(${d.id})">Editar</button>
                        <button class="btn btn-primary btn-sm" onclick="App.crearPlanDesdeDieta(${d.id})">Plan</button>
                        <button class="btn btn-danger btn-sm" onclick="App.borrarDieta(${d.id})">X</button>
                    </div>
                </div>
                <div id="dieta-detalle-${d.id}" style="display:none;margin-top:8px;"></div>
            </div>
        `).join('');
    },

    async verDietaDetalle(id, btn) {
        const el = document.getElementById(`dieta-detalle-${id}`);
        if (el.style.display === 'block') { el.style.display = 'none'; btn.textContent = 'Ver'; return; }
        el.innerHTML = '<p style="font-size:12px;color:var(--text-secondary);">Cargando...</p>';
        el.style.display = 'block';
        btn.textContent = 'Ocultar';
        try {
            const dieta = await API.obtenerDieta(id);
            const dayNames = { 0: 'Domingo', 1: 'Lunes', 2: 'Martes', 3: 'Miércoles', 4: 'Jueves', 5: 'Viernes', 6: 'Sábado',
                Sunday: 'Domingo', Monday: 'Lunes', Tuesday: 'Martes', Wednesday: 'Miércoles', Thursday: 'Jueves', Friday: 'Viernes', Saturday: 'Sábado' };
            el.innerHTML = dieta.dias.map(dia => {
                let dayTotal = 0;
                const mealHtml = dia.comidas.map(c => {
                    let mealTotal = 0;
                    const foodsHtml = c.alimentos.map(a => {
                        if (a.kcal != null) mealTotal += a.kcal;
                        return `<div style="font-size:12px;margin-left:8px;padding:2px 0;">
                            ${App.escHtml(a.nombre)}${a.cantidad ? ' <span style="color:var(--text-secondary);">(' + App.escHtml(a.cantidad) + ')</span>' : ''}${a.kcal != null ? ' <span style="color:var(--accent);font-weight:600;">' + a.kcal + ' kcal</span>' : ''}
                        </div>`;
                    }).join('');
                    dayTotal += mealTotal;
                    const tipoLabel = c.tipo === 'MediaManana' ? 'Media Mañana' : c.tipo === 'PreDesayuno' ? 'Pre Desayuno' : c.tipo;
                    return `<div style="margin-left:8px;margin-bottom:6px;">
                        <div style="display:flex;justify-content:space-between;align-items:center;">
                            <div style="font-size:12px;font-weight:600;color:var(--text-secondary);">${tipoLabel}</div>
                            ${mealTotal > 0 ? '<span style="font-size:11px;font-weight:600;color:var(--accent);">' + mealTotal + ' kcal</span>' : ''}
                        </div>
                        ${foodsHtml}
                    </div>`;
                }).join('');
                return `<div style="margin:8px 0;padding:8px;background:#fafafa;border-radius:8px;">
                    <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:4px;">
                        <div style="font-weight:600;font-size:13px;color:var(--primary-dark);">${dayNames[dia.diaSemana] || 'Día ' + dia.diaSemana}</div>
                        ${dayTotal > 0 ? '<span style="font-size:13px;font-weight:700;color:var(--accent);">' + dayTotal + ' kcal</span>' : ''}
                    </div>
                    ${mealHtml}
                </div>`;
            }).join('');
        } catch (e) { el.innerHTML = `<p style="color:var(--danger);font-size:12px;">${e.message}</p>`; }
    },

    // --- IMPORT DIET ---
    async importarDieta() {
        const file = document.getElementById('diet-file').files[0];
        const nombre = document.getElementById('diet-name').value;
        if (!file || !nombre) return this.toast('Selecciona un PDF y un nombre', 'error');

        const formData = new FormData();
        formData.append('archivo', file);
        formData.append('nombre', nombre);

        try {
            await API.importarDieta(API.user.id, formData);
            this.toast('Dieta importada correctamente');
            this.closeModal('modal-import');
            this.loadDashboard();
        } catch (e) {
            this.toast(e.message, 'error');
        }
    },

    async importarDietaImagen() {
        const file = document.getElementById('diet-img-file').files[0];
        const nombre = document.getElementById('diet-img-name').value;
        if (!file || !nombre) return this.toast('Selecciona una imagen y un nombre', 'error');

        const uid = API.user?.id;
        if (!uid) return this.toast('Sesión no válida, vuelve a iniciar sesión', 'error');

        const formData = new FormData();
        formData.append('archivo', file);
        formData.append('nombre', nombre);

        try {
            await API.importarDietaImagen(uid, formData);
            this.toast('Dieta importada desde imagen correctamente');
            this.closeModal('modal-import-imagen');
            this.loadDashboard();
        } catch (e) {
            this.toast(e.message, 'error');
        }
    },

    async borrarDieta(id) {
        if (!confirm('¿Eliminar esta dieta?')) return;
        try {
            await API.eliminarDieta(id);
            this.toast('Dieta eliminada');
            this.loadDashboard();
        } catch (e) { this.toast(e.message, 'error'); }
    },

    async crearPlanDesdeDieta(dietaId) {
        const lunes = getCurrentMonday();
        try {
            await API.crearPlan(API.user.id, dietaId, lunes);
            this.toast('Plan semanal creado');
            this.loadDashboard();
            this.showPage('plan');
            this.loadPlan();
        } catch (e) { this.toast(e.message, 'error'); }
    },

    async crearPlanManual() {
        const nombre = document.getElementById('manual-plan-name').value;
        const fecha = document.getElementById('manual-plan-date').value;
        if (!nombre || !fecha) return this.toast('Completa todos los campos', 'error');
        try {
            await API.crearPlanManual(API.user.id, nombre, fecha);
            this.toast('Plan manual creado');
            this.closeModal('modal-plan-manual');
            this.loadDashboard();
            this.showPage('plan');
            this.loadPlan();
        } catch (e) { this.toast(e.message, 'error'); }
    },

    // --- PLAN SEMANAL ---
    selectedPlanIndex: 0,

    async loadPlan() {
        const uid = API.user?.id;
        if (!uid) return;
        try {
            const planes = await API.listarPlanes(uid);
            if (planes.length === 0) {
                this.currentPlan = null;
                document.getElementById('plan-content').innerHTML = `
                    <div class="empty-state">
                        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="3" y="4" width="18" height="18" rx="2" ry="2"/><line x1="16" y1="2" x2="16" y2="6"/><line x1="8" y1="2" x2="8" y2="6"/><line x1="3" y1="10" x2="21" y2="10"/></svg>
                        <p>No hay planes semanales</p>
                        <p style="font-size:13px;">Importa una dieta y crea tu primer plan</p>
                    </div>`;
                return;
            }

            if (this.selectedPlanIndex >= planes.length) this.selectedPlanIndex = 0;
            const selectedPlan = planes[this.selectedPlanIndex];
            const plan = await API.obtenerPlan(selectedPlan.id);
            this.currentPlan = plan;
            this.allPlanes = planes;
            this.renderPlan(plan);
        } catch (e) { this.toast(e.message, 'error'); }
    },

    async borrarPlan(id) {
        if (!confirm('¿Eliminar este plan semanal?')) return;
        try {
            await API.eliminarPlan(id);
            this.selectedPlanIndex = 0;
            this.currentDayIndex = 0;
            this.toast('Plan eliminado');
            this.loadPlan();
            this.loadDashboard();
        } catch (e) { this.toast(e.message, 'error'); }
    },

    selectPlan(index) {
        this.selectedPlanIndex = index;
        this.currentDayIndex = 0;
        this.loadPlan();
    },

    async repetirPlanSiguiente() {
        const plan = this.currentPlan;
        if (!plan || !plan.dietaId) return this.toast('Este plan no tiene dieta asociada', 'error');
        // fechaInicio + 7 days = next Monday
        const inicio = new Date(plan.fechaInicio);
        const nextMonday = new Date(inicio.getFullYear(), inicio.getMonth(), inicio.getDate() + 7);
        const y = nextMonday.getFullYear();
        const m = String(nextMonday.getMonth() + 1).padStart(2, '0');
        const d = String(nextMonday.getDate()).padStart(2, '0');
        const fecha = `${y}-${m}-${d}`;
        try {
            await API.crearPlan(API.user.id, plan.dietaId, fecha);
            this.toast('Plan creado para semana del ' + fecha);
            this.selectedPlanIndex = 0;
            this.loadPlan();
        } catch (e) { this.toast(e.message, 'error'); }
    },

    renderPlan(plan) {
        const days = ['Dom', 'Lun', 'Mar', 'Mié', 'Jue', 'Vie', 'Sáb'];
        const mealTypes = {
            'PreDesayuno': 'predesayuno',
            'Desayuno': 'desayuno',
            'MediaManana': 'mediamanana',
            'Almuerzo': 'almuerzo',
            'Comida': 'comida',
            'Merienda': 'merienda',
            'Cena': 'cena'
        };
        const mealNames = {
            'PreDesayuno': 'Pre Desayuno',
            'Desayuno': 'Desayuno',
            'MediaManana': 'Media Mañana',
            'Almuerzo': 'Almuerzo',
            'Comida': 'Comida',
            'Merienda': 'Merienda',
            'Cena': 'Cena'
        };

        // Day tabs
        const tabsHtml = plan.dias.map((d, i) => {
            const date = new Date(d.fecha);
            return `<button class="day-tab ${i === this.currentDayIndex ? 'active' : ''}" onclick="App.selectDay(${i})">${days[date.getDay()]} ${date.getDate()}</button>`;
        }).join('');

        const dia = plan.dias[this.currentDayIndex];
        const mealsHtml = Object.keys(mealTypes).map(tipo => {
            const comidas = dia.comidas.filter(c => c.tipo === tipo);
            if (comidas.length === 0) return '';
            return `
                <div class="meal-card">
                    <div class="meal-header meal-${mealTypes[tipo]}">${mealNames[tipo]}</div>
                    <div class="meal-body">
                        ${comidas.map(c => `
                            <div class="meal-item">
                                <div class="meal-check ${c.completada ? 'checked' : ''}" onclick="App.toggleMeal(${c.id}, this)">
                                    ${c.completada ? '✓' : ''}
                                </div>
                                <span class="meal-text ${c.completada ? 'completed' : ''}">${c.descripcion}</span>
                                <button class="btn-delete-item" onclick="App.deleteMeal(${c.id})">&times;</button>
                            </div>
                        `).join('')}
                    </div>
                </div>
            `;
        }).join('');

        // Plan selector
        const planes = this.allPlanes || [];
        let planSelectorHtml = '';
        const repeatBtn = plan.dietaId ? `<button class="btn btn-sm" style="background:#9C27B0;color:white;" onclick="App.repetirPlanSiguiente()">Repetir →</button>` : '';
        if (planes.length > 1) {
            planSelectorHtml = `<div style="padding:8px 16px;display:flex;align-items:center;gap:6px;flex-wrap:wrap;">
                <select onchange="App.selectPlan(+this.value)" style="flex:1;padding:6px 8px;border-radius:8px;border:1px solid #ddd;min-width:150px;">
                    ${planes.map((p, i) => `<option value="${i}" ${i === this.selectedPlanIndex ? 'selected' : ''}>
                        ${new Date(p.fechaInicio).toLocaleDateString('es')} - ${new Date(p.fechaFin).toLocaleDateString('es')}
                    </option>`).join('')}
                </select>
                ${repeatBtn}
                <button class="btn btn-danger btn-sm" onclick="App.borrarPlan(${plan.id})">Eliminar</button>
            </div>`;
        } else {
            planSelectorHtml = `<div style="padding:8px 16px;display:flex;justify-content:flex-end;gap:6px;">
                ${repeatBtn}
                <button class="btn btn-danger btn-sm" onclick="App.borrarPlan(${plan.id})">Eliminar</button>
            </div>`;
        }

        document.getElementById('plan-content').innerHTML = `
            ${planSelectorHtml}
            <div class="day-tabs">${tabsHtml}</div>
            <div style="padding:8px 16px;display:flex;gap:8px;flex-wrap:wrap;">
                <button class="btn btn-sm" style="background:var(--primary);color:white;" onclick="App.completarDia()">✓ Completar día</button>
                <button class="btn btn-outline btn-sm" onclick="App.openAddMealModal(${dia.id})">+ Añadir comida</button>
            </div>
            ${mealsHtml}
            <div style="padding:4px 16px 16px;">
                <a href="/api/planes/${plan.id}/pdf" target="_blank" class="btn btn-accent">Descargar PDF</a>
            </div>
        `;
    },

    selectDay(index) {
        this.currentDayIndex = index;
        if (this.currentPlan) {
            this.renderPlan(this.currentPlan);
            // Scroll active tab into view on mobile
            requestAnimationFrame(() => {
                const activeTab = document.querySelector('.day-tab.active');
                if (activeTab) activeTab.scrollIntoView({ behavior: 'smooth', inline: 'center', block: 'nearest' });
            });
        }
    },

    async completarDia() {
        const plan = this.currentPlan;
        if (!plan) return;
        const dia = plan.dias[this.currentDayIndex];
        if (!dia || !dia.comidas || dia.comidas.length === 0) return this.toast('No hay comidas en este día', 'error');
        const pendientes = dia.comidas.filter(c => !c.completada);
        if (pendientes.length === 0) return this.toast('Ya están todas completadas');
        let completadas = 0;
        for (const c of pendientes) {
            try {
                const res = await API.toggleComida(c.id);
                c.completada = res.completada;
                completadas++;
            } catch (e) {
                this.toast('Error al completar: ' + e.message, 'error');
                break;
            }
        }
        if (completadas > 0) {
            this.toast(`${completadas} comidas completadas`);
            this.renderPlan(plan);
        }
    },

    async toggleMeal(id, el) {
        const newState = !el.classList.contains('checked');
        try {
            const res = await API.toggleComida(id);
            el.classList.toggle('checked');
            el.innerHTML = res.completada ? '✓' : '';
            el.nextElementSibling.classList.toggle('completed');
            // Update in-memory plan so switching days doesn't reset state
            if (this.currentPlan) {
                for (const dia of this.currentPlan.dias) {
                    const comida = dia.comidas.find(c => c.id === id);
                    if (comida) { comida.completada = res.completada; break; }
                }
            }
        } catch (e) {
            if (!navigator.onLine || e.message === 'offline') {
                // Optimistic update + queue for sync
                EcOffline.enqueue('PUT', `/api/plancomidas/${id}/completar`, null);
                el.classList.toggle('checked');
                el.innerHTML = newState ? '✓' : '';
                el.nextElementSibling.classList.toggle('completed');
                if (this.currentPlan) {
                    for (const dia of this.currentPlan.dias) {
                        const comida = dia.comidas.find(c => c.id === id);
                        if (comida) { comida.completada = newState; break; }
                    }
                }
            } else {
                this.toast(e.message, 'error');
            }
        }
    },

    openAddMealModal(planDiaId) {
        document.getElementById('add-meal-plandia-id').value = planDiaId;
        document.getElementById('add-meal-desc').value = '';
        this.openModal('modal-add-meal');
    },

    async addMealToPlan() {
        const planDiaId = document.getElementById('add-meal-plandia-id').value;
        const tipo = document.getElementById('add-meal-tipo').value;
        const descripcion = document.getElementById('add-meal-desc').value;
        if (!descripcion) return this.toast('Escribe una descripción', 'error');
        try {
            await API.addComidaPlan(planDiaId, tipo, descripcion);
            this.toast('Comida añadida');
            this.closeModal('modal-add-meal');
            this.loadPlan();
        } catch (e) { this.toast(e.message, 'error'); }
    },

    async deleteMeal(id) {
        if (!confirm('¿Eliminar esta comida?')) return;
        try {
            await API.deleteComidaPlan(id);
            this.loadPlan();
        } catch (e) { this.toast(e.message, 'error'); }
    },

    // --- PESO ---
    async loadPeso() {
        const uid = API.user?.id;
        if (!uid) return;
        try {
            const pesos = await API.listarPeso(uid);
            const container = document.getElementById('peso-content');

            if (pesos.length === 0) {
                container.innerHTML = `
                    <div class="empty-state">
                        <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M12 20V10"/><path d="M18 20V4"/><path d="M6 20v-4"/></svg>
                        <p>Sin registros de peso</p>
                    </div>`;
                return;
            }

            let html = '<div class="weight-list">';
            for (let i = pesos.length - 1; i >= 0; i--) {
                const p = pesos[i];
                let diffHtml = '';
                if (i > 0) {
                    const diff = (p.peso - pesos[i - 1].peso).toFixed(1);
                    const cls = diff < 0 ? 'down' : diff > 0 ? 'up' : '';
                    diffHtml = `<span class="weight-diff ${cls}">${diff > 0 ? '+' : ''}${diff} kg</span>`;
                }
                html += `
                    <div class="weight-entry">
                        <div>
                            <div class="weight-value">${p.peso} kg</div>
                            <div class="weight-date">${new Date(p.fecha).toLocaleDateString('es')}</div>
                        </div>
                        <div style="text-align:right;">
                            ${diffHtml}
                            <button class="btn btn-danger btn-sm" style="margin-top:4px;" onclick="App.borrarPeso(${p.id})">X</button>
                        </div>
                    </div>`;
            }
            html += '</div>';
            container.innerHTML = html;
        } catch (e) { this.toast(e.message, 'error'); }
    },

    async registrarPeso() {
        const peso = parseFloat(document.getElementById('new-peso').value);
        const nota = document.getElementById('new-peso-nota').value;
        if (!peso) return this.toast('Introduce un peso válido', 'error');

        try {
            await API.registrarPeso(API.user.id, new Date().toISOString(), peso, nota || null);
            this.toast('Peso registrado');
            this.closeModal('modal-peso');
            this.loadPeso();
            this.loadDashboard();
        } catch (e) { this.toast(e.message, 'error'); }
    },

    async borrarPeso(id) {
        if (!confirm('¿Eliminar este registro?')) return;
        try {
            await API.eliminarPeso(id);
            this.toast('Registro eliminado');
            this.loadPeso();
        } catch (e) { this.toast(e.message, 'error'); }
    },

    // --- LISTA COMPRA ---
    currentPlanIdForCompra: null,

    async loadCompra() {
        const uid = API.user?.id;
        if (!uid) return;
        try {
            const planes = await API.listarPlanes(uid);
            const container = document.getElementById('compra-content');

            if (planes.length === 0) {
                container.innerHTML = '<div class="empty-state"><p>Crea un plan semanal primero</p></div>';
                return;
            }

            const planId = planes[0].id;
            this.currentPlanIdForCompra = planId;
            let items = await API.obtenerListaCompra(planId);

            if (items.length === 0) {
                items = await API.generarListaCompra(planId);
            }

            this.renderCompra(items);
        } catch (e) { this.toast(e.message, 'error'); }
    },

    renderCompra(items) {
        const container = document.getElementById('compra-content');

        // Add item form
        let html = `
            <div class="card" style="margin:12px 16px;">
                <div style="display:flex;gap:8px;align-items:flex-end;">
                    <div style="flex:1;">
                        <input type="text" id="new-compra-nombre" placeholder="Añadir artículo..." style="width:100%;padding:10px;border:2px solid var(--border);border-radius:8px;font-size:14px;">
                    </div>
                    <div style="width:80px;">
                        <input type="text" id="new-compra-cantidad" placeholder="Cant." style="width:100%;padding:10px;border:2px solid var(--border);border-radius:8px;font-size:14px;">
                    </div>
                    <button class="btn btn-primary btn-sm" onclick="App.addItemCompra()" style="white-space:nowrap;">+</button>
                </div>
            </div>
            <div style="padding:8px 16px;">
                <button class="btn btn-outline btn-sm" onclick="App.regenerarCompra()">Regenerar lista</button>
            </div>`;

        if (items.length === 0) {
            html += '<div class="empty-state"><p>No hay items en la lista</p></div>';
            container.innerHTML = html;
            return;
        }

        let currentCat = '';
        for (const item of items) {
            if (item.categoria && item.categoria !== currentCat) {
                currentCat = item.categoria;
                html += `<div class="shop-category">${currentCat}</div>`;
            }
            html += `
                <div class="shop-item ${item.comprado ? 'bought' : ''}">
                    <div class="meal-check ${item.comprado ? 'checked' : ''}" onclick="App.toggleComprado(${item.id}, this.parentElement)">${item.comprado ? '✓' : ''}</div>
                    <span class="name" onclick="App.toggleComprado(${item.id}, this.parentElement)">${item.nombre}</span>
                    <span class="qty">${item.cantidad || ''}</span>
                    <button class="btn-delete-item" onclick="event.stopPropagation();App.deleteItemCompra(${item.id})">&times;</button>
                </div>`;
        }

        // Weekly totals summary
        const itemsWithQty = items.filter(i => i.cantidad && i.cantidad.trim());
        if (itemsWithQty.length > 0) {
            html += `<div class="shop-category" style="margin-top:20px;font-size:16px;border-top:2px solid var(--primary);">Total semanal</div>`;
            html += `<div class="card" style="margin:8px 16px;padding:12px;">`;
            for (const item of itemsWithQty) {
                html += `<div style="display:flex;justify-content:space-between;padding:4px 0;border-bottom:1px solid var(--border);">
                    <span>${item.nombre}</span>
                    <strong>${item.cantidad}</strong>
                </div>`;
            }
            html += `</div>`;
        }

        container.innerHTML = html;
    },

    async addItemCompra() {
        const nombre = document.getElementById('new-compra-nombre').value.trim();
        const cantidad = document.getElementById('new-compra-cantidad').value.trim();
        if (!nombre) return this.toast('Escribe un artículo', 'error');
        if (!this.currentPlanIdForCompra) return;
        try {
            await API.addItemCompra(this.currentPlanIdForCompra, nombre, cantidad || null, null);
            document.getElementById('new-compra-nombre').value = '';
            document.getElementById('new-compra-cantidad').value = '';
            this.loadCompra();
        } catch (e) { this.toast(e.message, 'error'); }
    },

    async deleteItemCompra(id) {
        try {
            await API.deleteItemCompra(id);
            this.loadCompra();
        } catch (e) { this.toast(e.message, 'error'); }
    },

    async regenerarCompra() {
        if (!this.currentPlanIdForCompra) return;
        try {
            const items = await API.generarListaCompra(this.currentPlanIdForCompra);
            this.renderCompra(items);
            this.toast('Lista regenerada');
        } catch (e) { this.toast(e.message, 'error'); }
    },

    async toggleComprado(id, el) {
        const newState = !el.classList.contains('bought');
        try {
            const res = await API.toggleComprado(id);
            el.classList.toggle('bought');
            el.querySelector('.meal-check').classList.toggle('checked');
            el.querySelector('.meal-check').innerHTML = res.comprado ? '✓' : '';
        } catch (e) {
            if (!navigator.onLine || e.message === 'offline') {
                // Optimistic update + queue for sync
                EcOffline.enqueue('PUT', `/api/lista-compra/${id}`, null);
                el.classList.toggle('bought');
                el.querySelector('.meal-check').classList.toggle('checked');
                el.querySelector('.meal-check').innerHTML = newState ? '✓' : '';
            } else {
                this.toast(e.message, 'error');
            }
        }
    },

    // --- PERFIL ---
    async loadPerfil() {
        try {
            const info = await API.me();
            document.getElementById('perfil-username').value = info.username;
            document.getElementById('perfil-email').value = info.email || '';
            document.getElementById('perfil-role').textContent = info.role;
            this.load2FAStatus();
        } catch (e) { this.toast(e.message, 'error'); }
    },

    async guardarPerfil() {
        const email = document.getElementById('perfil-email').value;
        try {
            await API.actualizarPerfil({ email });
            this.toast('Perfil actualizado');
        } catch (e) { this.toast(e.message, 'error'); }
    },

    async cambiarPassword() {
        const currentPassword = document.getElementById('perfil-old-pass').value;
        const newPassword = document.getElementById('perfil-new-pass').value;
        if (!currentPassword || !newPassword) return this.toast('Rellena ambos campos', 'error');
        try {
            await API.cambiarPassword({ currentPassword, newPassword });
            this.toast('Contraseña cambiada');
            document.getElementById('perfil-old-pass').value = '';
            document.getElementById('perfil-new-pass').value = '';
        } catch (e) { this.toast(e.message, 'error'); }
    },

    async load2FAStatus() {
        try {
            const status = await API.get2FAStatus();
            const container = document.getElementById('2fa-content');
            if (status.enabled) {
                container.innerHTML = `
                    <p style="color:var(--success);font-weight:600;margin-bottom:12px;">2FA Activado</p>
                    <div class="form-group">
                        <label>Contraseña para desactivar</label>
                        <input type="password" id="2fa-disable-pass" placeholder="Tu contraseña">
                    </div>
                    <button class="btn btn-danger" onclick="App.disable2FA()">Desactivar 2FA</button>`;
            } else {
                container.innerHTML = `
                    <p style="color:var(--text-secondary);margin-bottom:12px;">2FA no está activado</p>
                    <button class="btn btn-primary" onclick="App.setup2FA()">Activar 2FA</button>
                    <div id="2fa-setup-area"></div>`;
            }
        } catch (e) { this.toast(e.message, 'error'); }
    },

    async setup2FA() {
        try {
            const result = await API.setup2FA();
            document.getElementById('2fa-setup-area').innerHTML = `
                <div class="card" style="margin-top:12px;">
                    <p style="font-size:13px;margin-bottom:8px;">Escanea este código QR o introduce la clave manualmente:</p>
                    <div style="text-align:center;margin:12px 0;">
                        <img src="https://api.qrserver.com/v1/create-qr-code/?data=${encodeURIComponent(result.otpAuthUri)}&size=200x200" alt="QR Code" style="border-radius:8px;">
                    </div>
                    <p style="font-size:12px;word-break:break-all;background:var(--bg);padding:8px;border-radius:4px;">${result.secret}</p>
                    <div class="form-group" style="margin-top:12px;">
                        <label>Código de verificación</label>
                        <input type="text" id="2fa-confirm-code" placeholder="123456" maxlength="6">
                    </div>
                    <button class="btn btn-primary" onclick="App.confirm2FA()">Confirmar</button>
                </div>`;
        } catch (e) { this.toast(e.message, 'error'); }
    },

    async confirm2FA() {
        const code = document.getElementById('2fa-confirm-code').value;
        if (!code) return this.toast('Introduce el código', 'error');
        try {
            await API.confirm2FA(code);
            this.toast('2FA activado correctamente');
            this.load2FAStatus();
        } catch (e) { this.toast(e.message, 'error'); }
    },

    async disable2FA() {
        const password = document.getElementById('2fa-disable-pass').value;
        if (!password) return this.toast('Introduce tu contraseña', 'error');
        try {
            await API.disable2FA(password);
            this.toast('2FA desactivado');
            this.load2FAStatus();
        } catch (e) { this.toast(e.message, 'error'); }
    },

    // --- ADMIN ---
    async loadAdmin() {
        const role = API.user?.role;
        if (role !== 'Admin' && role !== 'Superuser' && role !== 'SuperUserMaster') {
            document.getElementById('admin-content').innerHTML = '<div class="empty-state"><p>No tienes permisos</p></div>';
            return;
        }
        try {
            const users = await API.listarUsuarios();
            const isSUM = role === 'SuperUserMaster';
            let html = '<div class="admin-list">';
            for (const u of users) {
                const roleBadge = this.getRoleBadge(u.role);
                const statusBadge = u.isActive
                    ? '<span class="badge badge-success">Activo</span>'
                    : '<span class="badge badge-danger">Inactivo</span>';
                html += `
                    <div class="card" style="margin:8px 16px;">
                        <div style="display:flex;justify-content:space-between;align-items:center;">
                            <div>
                                <strong>${u.username}</strong>
                                <div style="font-size:12px;color:var(--text-secondary);">${u.email || 'Sin email'}</div>
                                <div style="margin-top:4px;">${roleBadge} ${statusBadge}</div>
                            </div>
                            <div style="display:flex;gap:6px;flex-wrap:wrap;justify-content:flex-end;">
                                ${isSUM ? `
                                    <button class="btn btn-sm btn-outline" onclick="App.editarUsuario(${u.id},'${u.email||''}','${u.role}',${u.isActive})">Editar</button>
                                    <button class="btn btn-sm btn-accent" onclick="App.impersonarUsuario(${u.id})">Impersonar</button>
                                    ${u.id !== API.user.id ? `<button class="btn btn-sm btn-danger" onclick="App.eliminarUsuario(${u.id},'${u.username}')">X</button>` : ''}
                                ` : ''}
                            </div>
                        </div>
                    </div>`;
            }
            html += '</div>';

            // Stats summary
            const totalUsers = users.length;
            const activeUsers = users.filter(u => u.isActive).length;
            const statsHtml = `
                <div class="stats-grid">
                    <div class="stat-card"><div class="stat-value">${totalUsers}</div><div class="stat-label">Total usuarios</div></div>
                    <div class="stat-card"><div class="stat-value">${activeUsers}</div><div class="stat-label">Activos</div></div>
                </div>`;

            // DB Admin section (SuperUserMaster only)
            if (isSUM) {
                html += `
                    <div class="card" style="margin:16px;">
                        <h3 style="margin-bottom:12px;">Base de Datos</h3>
                        <button class="btn btn-primary btn-sm" onclick="App.loadDbAdmin()">Explorar tablas</button>
                        <button class="btn btn-outline btn-sm" onclick="App.openSqlConsole()">Consola SQL</button>
                    </div>`;
            }

            document.getElementById('admin-content').innerHTML = statsHtml + html;
        } catch (e) { this.toast(e.message, 'error'); }
    },

    getRoleBadge(role) {
        const colors = { Standard: '#9E9E9E', Admin: '#2196F3', Superuser: '#FF9800', SuperUserMaster: '#f44336' };
        return `<span class="badge" style="background:${colors[role] || '#9E9E9E'};color:white;">${role}</span>`;
    },

    editarUsuario(id, email, role, isActive) {
        document.getElementById('edit-user-id').value = id;
        document.getElementById('edit-user-email').value = email;
        document.getElementById('edit-user-role').value = role;
        document.getElementById('edit-user-active').value = String(isActive);
        document.getElementById('edit-user-newpass').value = '';
        this.openModal('modal-editar-usuario');
    },

    async guardarUsuario() {
        const id = document.getElementById('edit-user-id').value;
        const email = document.getElementById('edit-user-email').value;
        const role = document.getElementById('edit-user-role').value;
        const isActive = document.getElementById('edit-user-active').value === 'true';
        try {
            await API.actualizarUsuario(id, { email, role, isActive });
            this.toast('Usuario actualizado');
            this.closeModal('modal-editar-usuario');
            this.loadAdmin();
        } catch (e) { this.toast(e.message, 'error'); }
    },

    async resetPasswordUsuario() {
        const id = document.getElementById('edit-user-id').value;
        const newPassword = document.getElementById('edit-user-newpass').value;
        if (!newPassword) return this.toast('Introduce la nueva contraseña', 'error');
        try {
            await API.resetPassword(id, newPassword);
            this.toast('Contraseña reseteada');
            document.getElementById('edit-user-newpass').value = '';
        } catch (e) { this.toast(e.message, 'error'); }
    },

    async crearUsuario() {
        const username = document.getElementById('new-user-username').value;
        const email = document.getElementById('new-user-email').value;
        const password = document.getElementById('new-user-password').value;
        const role = document.getElementById('new-user-role').value;
        if (!username || !email || !password) return this.toast('Completa todos los campos', 'error');
        try {
            await API.crearUsuario({ username, email, password, role, isActive: true });
            this.toast('Usuario creado');
            this.closeModal('modal-crear-usuario');
            this.loadAdmin();
        } catch (e) { this.toast(e.message, 'error'); }
    },

    async eliminarUsuario(id, username) {
        if (!confirm(`¿Eliminar al usuario ${username}?`)) return;
        try {
            await API.eliminarUsuario(id);
            this.toast('Usuario eliminado');
            this.loadAdmin();
        } catch (e) { this.toast(e.message, 'error'); }
    },

    async impersonarUsuario(id) {
        if (!confirm('¿Impersonar este usuario?')) return;
        try {
            const res = await API.impersonar(id);
            API.setAuth(res.accessToken, res.refreshToken, res.user);
            this.updateAdminNav();
            this.showPage('dashboard');
            this.loadDashboard();
            this.toast('Impersonando a ' + res.user.username);
        } catch (e) { this.toast(e.message, 'error'); }
    },

    // --- DB ADMIN ---
    async loadDbAdmin() {
        try {
            const tables = await API.dbTables();
            let html = `<div class="card" style="margin:16px;">
                <h3 style="margin-bottom:12px;">Tablas de la base de datos</h3>
                <div style="display:flex;flex-wrap:wrap;gap:8px;">`;
            for (const t of tables) {
                html += `<button class="btn btn-outline btn-sm" onclick="App.loadDbTable('${t}')">${t}</button>`;
            }
            html += `</div>
                <div style="margin-top:12px;">
                    <button class="btn btn-sm" style="background:#666;color:white;" onclick="App.openSqlConsole()">Consola SQL</button>
                    <button class="btn btn-sm btn-outline" onclick="App.loadAdmin()">Volver</button>
                </div>
            </div>
            <div id="db-table-content"></div>`;
            document.getElementById('admin-content').innerHTML = html;
        } catch (e) { this.toast(e.message, 'error'); }
    },

    async loadDbTable(table, page = 1) {
        try {
            const data = await API.dbTableData(table, page);
            const totalPages = Math.ceil(data.total / data.pageSize);
            let html = `<div class="card" style="margin:16px;overflow-x:auto;">
                <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:12px;">
                    <h3>${table} <span style="font-weight:normal;font-size:13px;color:var(--text-secondary);">(${data.total} filas)</span></h3>
                    <div style="display:flex;gap:6px;">
                        ${page > 1 ? `<button class="btn btn-sm btn-outline" onclick="App.loadDbTable('${table}',${page - 1})">Anterior</button>` : ''}
                        <span style="padding:6px;font-size:13px;">${page}/${totalPages}</span>
                        ${page < totalPages ? `<button class="btn btn-sm btn-outline" onclick="App.loadDbTable('${table}',${page + 1})">Siguiente</button>` : ''}
                    </div>
                </div>
                <table style="width:100%;border-collapse:collapse;font-size:12px;">
                <thead><tr>`;
            for (const col of data.columns) {
                html += `<th style="padding:6px 8px;border-bottom:2px solid var(--border);text-align:left;white-space:nowrap;">${col.name}${col.pk ? ' (PK)' : ''}<br><span style="color:#999;font-weight:normal;">${col.type}</span></th>`;
            }
            html += `<th style="padding:6px;border-bottom:2px solid var(--border);"></th></tr></thead><tbody>`;
            for (const row of data.rows) {
                html += '<tr>';
                let pkVal = null;
                for (const col of data.columns) {
                    const val = row[col.name];
                    if (col.pk) pkVal = val;
                    const display = val === null ? '<em style="color:#999;">NULL</em>' : String(val).length > 50 ? String(val).substring(0, 50) + '...' : String(val);
                    html += `<td style="padding:4px 8px;border-bottom:1px solid var(--border);max-width:200px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;">${display}</td>`;
                }
                if (pkVal !== null) {
                    html += `<td style="padding:4px;border-bottom:1px solid var(--border);"><button class="btn-delete-item" onclick="App.dbDeleteRow('${table}',${pkVal})">&times;</button></td>`;
                }
                html += '</tr>';
            }
            html += '</tbody></table></div>';
            document.getElementById('db-table-content').innerHTML = html;
        } catch (e) { this.toast(e.message, 'error'); }
    },

    async dbDeleteRow(table, id) {
        if (!confirm(`Eliminar fila ${id} de ${table}?`)) return;
        try {
            await API.dbDeleteRow(table, id);
            this.toast('Fila eliminada');
            this.loadDbTable(table);
        } catch (e) { this.toast(e.message, 'error'); }
    },

    openSqlConsole() {
        let html = `<div class="card" style="margin:16px;">
            <h3 style="margin-bottom:12px;">Consola SQL</h3>
            <textarea id="sql-input" style="width:100%;height:120px;padding:10px;border:2px solid var(--border);border-radius:8px;font-family:monospace;font-size:13px;resize:vertical;" placeholder="SELECT * FROM Usuarios LIMIT 10;"></textarea>
            <div style="margin-top:8px;display:flex;gap:8px;">
                <button class="btn btn-primary btn-sm" onclick="App.executeSql()">Ejecutar</button>
                <button class="btn btn-outline btn-sm" onclick="App.loadDbAdmin()">Volver a tablas</button>
            </div>
            <div id="sql-result" style="margin-top:12px;"></div>
        </div>`;
        const container = document.getElementById('db-table-content');
        if (container) {
            container.innerHTML = html;
        } else {
            document.getElementById('admin-content').innerHTML = html;
        }
    },

    async executeSql() {
        const sql = document.getElementById('sql-input').value.trim();
        if (!sql) return this.toast('Escribe una consulta SQL', 'error');
        try {
            const result = await API.dbExecute(sql);
            let html = '';
            if (result.type === 'query') {
                html = `<div style="font-size:13px;color:var(--text-secondary);margin-bottom:8px;">${result.rowCount} resultados</div>`;
                html += '<div style="overflow-x:auto;"><table style="width:100%;border-collapse:collapse;font-size:12px;">';
                html += '<thead><tr>';
                for (const col of result.columns) {
                    html += `<th style="padding:6px 8px;border-bottom:2px solid var(--border);text-align:left;white-space:nowrap;">${col}</th>`;
                }
                html += '</tr></thead><tbody>';
                for (const row of result.rows) {
                    html += '<tr>';
                    for (const col of result.columns) {
                        const val = row[col];
                        const display = val === null ? '<em style="color:#999;">NULL</em>' : String(val).length > 80 ? String(val).substring(0, 80) + '...' : String(val);
                        html += `<td style="padding:4px 8px;border-bottom:1px solid var(--border);">${display}</td>`;
                    }
                    html += '</tr>';
                }
                html += '</tbody></table></div>';
            } else {
                html = `<div style="padding:12px;background:#e8f5e9;border-radius:8px;color:#2e7d32;">${result.message}</div>`;
            }
            document.getElementById('sql-result').innerHTML = html;
        } catch (e) {
            document.getElementById('sql-result').innerHTML = `<div style="padding:12px;background:#ffebee;border-radius:8px;color:#c62828;">${e.message}</div>`;
        }
    },

    // --- VERSION CHECK ---
    async checkForUpdates() {
        try {
            const data = await API.getVersion();
            const storedVersion = localStorage.getItem('appVersion');
            if (storedVersion && storedVersion !== data.version) {
                this.showUpdatePopup(data.version);
            }
            localStorage.setItem('appVersion', data.version);
            const badge = document.getElementById('env-badge');
            if (badge && data.env) {
                badge.textContent = data.env === 'develop' ? '(D)' : '(P)';
                badge.style.cssText = 'font-size:1em;vertical-align:middle;font-weight:bold;';
            }
        } catch { /* ignore */ }
    },

    showUpdatePopup(version) {
        const popup = document.createElement('div');
        popup.id = 'update-popup';
        popup.innerHTML = `
            <div style="position:fixed;top:0;left:0;right:0;bottom:0;background:rgba(0,0,0,0.5);z-index:9999;display:flex;align-items:center;justify-content:center;padding:20px;">
                <div style="background:white;border-radius:16px;padding:24px;max-width:340px;text-align:center;box-shadow:0 8px 32px rgba(0,0,0,0.2);">
                    <div style="font-size:40px;margin-bottom:12px;">&#x1F680;</div>
                    <h3 style="margin-bottom:8px;">Nueva version disponible</h3>
                    <p style="color:var(--text-secondary);font-size:14px;margin-bottom:16px;">Se ha actualizado EatHealthyCycle a la version ${version}. Recarga para obtener los ultimos cambios.</p>
                    <button onclick="location.reload()" style="width:100%;padding:12px;background:var(--primary);color:white;border:none;border-radius:10px;font-size:15px;font-weight:600;cursor:pointer;">Actualizar ahora</button>
                    <button onclick="document.getElementById('update-popup').remove()" style="width:100%;padding:10px;background:none;border:none;color:var(--text-secondary);font-size:13px;cursor:pointer;margin-top:8px;">Mas tarde</button>
                </div>
            </div>`;
        document.body.appendChild(popup);
    },

    // --- MANUAL DIET ---
    mdState: { days: {}, currentDay: 1 },

    _dayNameToNum: { Sunday: 0, Monday: 1, Tuesday: 2, Wednesday: 3, Thursday: 4, Friday: 5, Saturday: 6 },

    mdDayKey(diaSemana) {
        // Handle both string ("Monday") and number (1) from API
        if (typeof diaSemana === 'string') return this._dayNameToNum[diaSemana] ?? 0;
        return diaSemana;
    },

    openManualDietModal(editId, nombre, desc, daysData) {
        document.getElementById('md-nombre').value = nombre || '';
        document.getElementById('md-desc').value = desc || '';
        const dayNames = ['L', 'M', 'X', 'J', 'V', 'S', 'D'];
        const dayValues = [1, 2, 3, 4, 5, 6, 0]; // Mon-Sun as DayOfWeek
        this.mdState = { days: {}, currentDay: 1, editId: editId || null };
        dayValues.forEach(d => { this.mdState.days[d] = { comidas: [] }; });
        // Load existing data if editing
        if (daysData) {
            daysData.forEach(dia => {
                this.mdState.days[this.mdDayKey(dia.diaSemana)] = {
                    comidas: dia.comidas.map(c => ({
                        tipo: c.tipo,
                        orden: c.orden,
                        nota: c.nota || null,
                        alimentos: c.alimentos.map(a => ({
                            nombre: a.nombre,
                            cantidad: a.cantidad || null,
                            categoria: a.categoria || null,
                            kcal: a.kcal != null ? a.kcal : null,
                            _kcalPor100g: null
                        }))
                    }))
                };
            });
        }
        const tabsEl = document.getElementById('md-day-tabs');
        tabsEl.innerHTML = dayValues.map((d, i) =>
            `<button class="btn btn-sm ${d === 1 ? 'btn-primary' : 'btn-outline'}" data-md-day="${d}" onclick="App.mdSelectDay(${d})" style="min-width:36px;padding:6px 8px;">${dayNames[i]}</button>`
        ).join('');
        // Update modal title
        document.querySelector('#modal-dieta-manual h3').textContent = editId ? 'Editar Dieta' : 'Crear Dieta Manual';
        this.mdRenderMeals();
        this.openModal('modal-dieta-manual');
    },

    async editarDieta(id) {
        try {
            const dieta = await API.obtenerDieta(id);
            this.openManualDietModal(id, dieta.nombre, dieta.descripcion, dieta.dias);
        } catch (e) {
            this.toast(e.message, 'error');
        }
    },

    mdSelectDay(day) {
        this.mdSaveCurrentMeals();
        this.mdState.currentDay = day;
        document.querySelectorAll('[data-md-day]').forEach(b => {
            b.classList.toggle('btn-primary', parseInt(b.dataset.mdDay) === day);
            b.classList.toggle('btn-outline', parseInt(b.dataset.mdDay) !== day);
        });
        this.mdRenderMeals();
    },

    mdSaveCurrentMeals() {
        const mealsEl = document.getElementById('md-meals');
        const meals = [];
        mealsEl.querySelectorAll('.md-meal-block').forEach(block => {
            const tipo = block.querySelector('.md-meal-tipo').value;
            const foods = [];
            block.querySelectorAll('.md-food-row').forEach(row => {
                const nombre = row.querySelector('.md-food-nombre').value.trim();
                if (!nombre) return;
                const cantidad = row.querySelector('.md-food-cantidad').value.trim() || null;
                const kcalVal = row.querySelector('.md-food-kcal').value;
                const kcal = kcalVal ? parseInt(kcalVal) : null;
                const kcal100Val = row.querySelector('.md-food-kcal100').value;
                const _kcalPor100g = kcal100Val ? parseFloat(kcal100Val) : null;
                foods.push({ nombre, cantidad, categoria: null, kcal, _kcalPor100g });
            });
            meals.push({ tipo, orden: meals.length, nota: null, alimentos: foods });
        });
        this.mdState.days[this.mdState.currentDay].comidas = meals;
    },

    mdRenderMeals() {
        const meals = this.mdState.days[this.mdState.currentDay].comidas;
        const container = document.getElementById('md-meals');
        if (meals.length === 0) {
            container.innerHTML = '<p style="color:var(--text-secondary);font-size:13px;text-align:center;padding:8px;">Añade comidas para este día</p>';
            this.mdUpdateTotal();
            return;
        }
        container.innerHTML = meals.map((m, mi) => `
            <div class="md-meal-block card" style="margin:8px 0;padding:10px;">
                <div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:8px;">
                    <select class="md-meal-tipo" style="flex:1;padding:6px;border-radius:6px;border:1px solid #ccc;font-size:13px;">
                        ${['PreDesayuno','Desayuno','MediaManana','Almuerzo','Comida','Merienda','Cena'].map(t =>
                            `<option value="${t}" ${t === m.tipo ? 'selected' : ''}>${t === 'PreDesayuno' ? 'Pre Desayuno' : t === 'MediaManana' ? 'Media Mañana' : t}</option>`
                        ).join('')}
                    </select>
                    <button class="btn btn-danger btn-sm" style="margin-left:8px;padding:4px 10px;" onclick="App.mdRemoveMeal(${mi})">✕</button>
                </div>
                <div class="md-food-list">
                    ${m.alimentos.map((f, fi) => App.mdFoodRowHtml(mi, fi, f)).join('')}
                </div>
                <div style="display:flex;justify-content:space-between;align-items:center;margin-top:8px;">
                    <button class="btn btn-sm btn-outline" style="font-size:12px;" onclick="App.mdAddFood(${mi})">+ Alimento</button>
                    <div class="md-meal-subtotal" style="font-size:13px;font-weight:700;color:var(--accent);"></div>
                </div>
            </div>
        `).join('');
        this.mdUpdateTotal();
    },

    mdFoodRowHtml(mi, fi, f) {
        const kcal100 = f._kcalPor100g != null ? f._kcalPor100g : '';
        return `<div class="md-food-row" style="margin:6px 0;padding:6px;background:#fafafa;border-radius:8px;border:1px solid #e8e8e8;">
            <div style="display:flex;gap:4px;align-items:center;">
                <input class="md-food-nombre" type="text" placeholder="Nombre del alimento" value="${this.escHtml(f.nombre || '')}" style="flex:1;padding:6px;border-radius:6px;border:1px solid #ccc;font-size:12px;">
                <button class="btn btn-sm md-voice-btn" style="padding:4px 8px;font-size:11px;background:#607D8B;color:white;" onclick="App.mdVoiceInput(${mi},${fi})" title="Dictado por voz">&#x1F3A4;</button>
                <button class="btn btn-sm" style="padding:4px 8px;font-size:11px;background:#2196F3;color:white;" onclick="App.mdSearchOFF(${mi},${fi})">&#x1F50D;</button>
                <button class="btn btn-danger btn-sm" style="padding:4px 8px;" onclick="App.mdRemoveFood(${mi},${fi})">✕</button>
            </div>
            <div style="display:flex;gap:4px;align-items:center;margin-top:4px;">
                <input class="md-food-cantidad" type="text" placeholder="Cantidad (ej: 300g)" value="${this.escHtml(f.cantidad || '')}" style="flex:1;padding:6px;border-radius:6px;border:1px solid #ccc;font-size:12px;" oninput="App.mdRecalcKcal(${mi},${fi})">
                <input class="md-food-kcal100" type="hidden" value="${kcal100}">
                <span style="font-size:11px;color:var(--text-secondary);white-space:nowrap;">${kcal100 ? kcal100 + '/100g' : ''}</span>
                <input class="md-food-kcal" type="number" placeholder="kcal" value="${f.kcal != null ? f.kcal : ''}" style="width:70px;padding:6px;border-radius:6px;border:1px solid #ccc;font-size:12px;" oninput="App.mdUpdateTotal()">
                <span style="font-size:11px;color:var(--text-secondary);">kcal</span>
            </div>
        </div>
        <div id="md-off-results-${mi}-${fi}" style="display:none;"></div>`;
    },

    escHtml(s) { const d = document.createElement('div'); d.textContent = s; return d.innerHTML; },

    mdVoiceInput(mi, fi) {
        const SpeechRecognition = window.SpeechRecognition || window.webkitSpeechRecognition;
        if (!SpeechRecognition) return this.toast('Dictado por voz no disponible en este navegador', 'error');

        const blocks = document.getElementById('md-meals').querySelectorAll('.md-meal-block');
        const row = blocks[mi].querySelectorAll('.md-food-row')[fi];
        const input = row.querySelector('.md-food-nombre');
        const btn = row.querySelector('.md-voice-btn');
        if (!btn) return;

        const recognition = new SpeechRecognition();
        recognition.lang = 'es-ES';
        recognition.continuous = false;
        recognition.interimResults = false;
        recognition.maxAlternatives = 1;

        const resetBtn = () => { btn.style.background = '#607D8B'; btn.innerHTML = '&#x1F3A4;'; };

        btn.style.background = '#f44336';
        btn.innerHTML = '<span style="animation:pulse 1s infinite">...</span>';

        recognition.onresult = (e) => {
            input.value = e.results[0][0].transcript;
            resetBtn();
            // Auto-search after voice input
            App.mdSearchOFF(mi, fi);
        };
        recognition.onerror = (e) => {
            const msgs = { 'not-allowed': 'Permite el acceso al micrófono', 'no-speech': 'No se detectó voz', 'network': 'Error de red' };
            this.toast(msgs[e.error] || 'Error de voz: ' + e.error, 'error');
            resetBtn();
        };
        recognition.onend = resetBtn;

        try { recognition.start(); } catch (e) { this.toast('No se pudo iniciar el micrófono', 'error'); resetBtn(); }
    },

    mdUpdateTotal() {
        let total = 0;
        document.querySelectorAll('#md-meals .md-meal-block').forEach(block => {
            let subtotal = 0;
            block.querySelectorAll('.md-food-kcal').forEach(input => {
                const v = parseInt(input.value);
                if (v > 0) subtotal += v;
            });
            const label = block.querySelector('.md-meal-subtotal');
            if (label) label.textContent = subtotal > 0 ? subtotal + ' kcal' : '';
            total += subtotal;
        });
        const el = document.getElementById('md-kcal-total');
        el.textContent = total > 0 ? `Total dia: ${total} kcal` : '';
    },

    mdAddMeal() {
        this.mdSaveCurrentMeals();
        this.mdState.days[this.mdState.currentDay].comidas.push({
            tipo: 'Desayuno', orden: 0, nota: null,
            alimentos: [{ nombre: '', cantidad: null, categoria: null, kcal: null, _kcalPor100g: null }]
        });
        this.mdRenderMeals();
    },

    mdRemoveMeal(mi) {
        this.mdSaveCurrentMeals();
        this.mdState.days[this.mdState.currentDay].comidas.splice(mi, 1);
        this.mdRenderMeals();
    },

    mdAddFood(mi) {
        this.mdSaveCurrentMeals();
        this.mdState.days[this.mdState.currentDay].comidas[mi].alimentos.push({ nombre: '', cantidad: null, categoria: null, kcal: null, _kcalPor100g: null });
        this.mdRenderMeals();
    },

    mdRemoveFood(mi, fi) {
        this.mdSaveCurrentMeals();
        this.mdState.days[this.mdState.currentDay].comidas[mi].alimentos.splice(fi, 1);
        this.mdRenderMeals();
    },

    async mdSearchOFF(mi, fi) {
        const mealsEl = document.getElementById('md-meals');
        const blocks = mealsEl.querySelectorAll('.md-meal-block');
        const row = blocks[mi].querySelectorAll('.md-food-row')[fi];
        const term = row.querySelector('.md-food-nombre').value.trim();
        if (!term || term.length < 2) return this.toast('Escribe al menos 2 caracteres', 'error');

        const resultsEl = document.getElementById(`md-off-results-${mi}-${fi}`);
        resultsEl.style.display = 'block';
        resultsEl.innerHTML = '<p style="font-size:12px;color:var(--text-secondary);padding:4px;">Buscando...</p>';

        try {
            const results = await API.buscarAlimentos(term);
            if (!results || results.length === 0) {
                resultsEl.innerHTML = '<p style="font-size:12px;color:var(--text-secondary);padding:4px;">Sin resultados</p>';
                return;
            }
            resultsEl.innerHTML = `<div style="max-height:150px;overflow-y:auto;border:1px solid #e0e0e0;border-radius:6px;margin:4px 0;">
                ${results.slice(0, 10).map((r, ri) => `
                    <div onclick="App.mdSelectOFF(${mi},${fi},${ri})" style="padding:6px 8px;font-size:12px;cursor:pointer;border-bottom:1px solid #f0f0f0;display:flex;justify-content:space-between;align-items:center;"
                         onmouseover="this.style.background='#f5f5f5'" onmouseout="this.style.background='white'">
                        <span>${App.escHtml(r.nombre)}${r.marca ? ' <span style="color:var(--text-secondary);">(' + App.escHtml(r.marca) + ')</span>' : ''}</span>
                        <span style="color:var(--accent);font-weight:600;white-space:nowrap;margin-left:8px;">${r.kcalPor100g != null ? r.kcalPor100g + ' kcal/100g' : '—'}</span>
                    </div>
                `).join('')}
            </div>`;
            this._offResults = results;
        } catch (e) {
            resultsEl.innerHTML = `<p style="font-size:12px;color:var(--danger);padding:4px;">${e.message}</p>`;
        }
    },

    mdSelectOFF(mi, fi, ri) {
        const r = this._offResults[ri];
        const blocks = document.getElementById('md-meals').querySelectorAll('.md-meal-block');
        const row = blocks[mi].querySelectorAll('.md-food-row')[fi];
        row.querySelector('.md-food-nombre').value = r.nombre + (r.marca ? ` (${r.marca})` : '');
        if (r.kcalPor100g != null) {
            row.querySelector('.md-food-kcal100').value = r.kcalPor100g;
            // Auto-calc if quantity already set, otherwise show per 100g
            const cantText = row.querySelector('.md-food-cantidad').value;
            const qty = this.mdParseQuantity(cantText);
            if (qty && qty.grams != null) {
                row.querySelector('.md-food-kcal').value = Math.round(r.kcalPor100g * qty.grams / 100);
            } else if (qty && qty.multiplier != null) {
                row.querySelector('.md-food-kcal').value = Math.round(r.kcalPor100g * qty.multiplier);
            } else {
                row.querySelector('.md-food-kcal').value = r.kcalPor100g;
            }
            // Update the label
            row.querySelector('.md-food-kcal100').parentElement.querySelector('span').textContent = r.kcalPor100g + '/100g';
        }
        document.getElementById(`md-off-results-${mi}-${fi}`).style.display = 'none';
        this.mdUpdateTotal();
    },

    mdRecalcKcal(mi, fi) {
        const blocks = document.getElementById('md-meals').querySelectorAll('.md-meal-block');
        const row = blocks[mi].querySelectorAll('.md-food-row')[fi];
        const kcal100 = parseFloat(row.querySelector('.md-food-kcal100').value);
        if (!kcal100) return;
        const qty = this.mdParseQuantity(row.querySelector('.md-food-cantidad').value);
        if (qty) {
            row.querySelector('.md-food-kcal').value = qty.grams != null
                ? Math.round(kcal100 * qty.grams / 100)
                : Math.round(kcal100 * qty.multiplier);
        }
        this.mdUpdateTotal();
    },

    // Returns { grams: N } for weight-based, { multiplier: N } for units, or null
    mdParseQuantity(text) {
        if (!text) return null;
        text = text.trim().toLowerCase();
        // Weight: "300g", "300 g", "40gr", "150 gramos", "40G"
        const g = text.match(/(\d+(?:[.,]\d+)?)\s*g(?:r(?:amos)?)?\s*$/);
        if (g) return { grams: parseFloat(g[1].replace(',', '.')) };
        // Volume: "200ml", "200 ml"
        const ml = text.match(/(\d+(?:[.,]\d+)?)\s*ml\s*$/);
        if (ml) return { grams: parseFloat(ml[1].replace(',', '.')) };
        // Weight kg: "1,5kg", "2 kg"
        const kg = text.match(/(\d+(?:[.,]\d+)?)\s*kg\s*$/);
        if (kg) return { grams: parseFloat(kg[1].replace(',', '.')) * 1000 };
        // Units: "2 latas", "3 unidades", "1 bote", etc. → multiplier
        const unit = text.match(/(\d+(?:[.,]\d+)?)\s*(?:latas?|unidad(?:es)?|botes?|piezas?|racion(?:es)?|porciones?|sobre(?:s)?|cucharada(?:s)?|taza(?:s)?|rebanada(?:s)?)\s*$/);
        if (unit) return { multiplier: parseFloat(unit[1].replace(',', '.')) };
        // Plain number — only if small (≤10), treat as multiplier; larger numbers = grams
        const plain = text.match(/^(\d+(?:[.,]\d+)?)\s*$/);
        if (plain) {
            const n = parseFloat(plain[1].replace(',', '.'));
            return n > 10 ? { grams: n } : { multiplier: n };
        }
        return null;
    },

    async guardarDietaManual() {
        const nombre = document.getElementById('md-nombre').value.trim();
        if (!nombre) return this.toast('Escribe un nombre para la dieta', 'error');

        this.mdSaveCurrentMeals();

        const dias = [];
        for (const [day, data] of Object.entries(this.mdState.days)) {
            if (data.comidas.length === 0) continue;
            const comidas = data.comidas.filter(c => c.alimentos.length > 0).map((c, i) => ({
                tipo: c.tipo,
                orden: i,
                nota: c.nota,
                alimentos: c.alimentos.map(a => ({ nombre: a.nombre, cantidad: a.cantidad, categoria: a.categoria, kcal: a.kcal }))
            }));
            if (comidas.length === 0) continue;
            dias.push({ diaSemana: parseInt(day), nota: null, comidas });
        }

        if (dias.length === 0) return this.toast('Añade al menos una comida con alimentos', 'error');

        const payload = {
            nombre,
            descripcion: document.getElementById('md-desc').value.trim() || null,
            dias
        };

        try {
            if (this.mdState.editId) {
                await API.actualizarDietaCompleta(this.mdState.editId, payload);
                this.toast('Dieta actualizada correctamente');
            } else {
                await API.crearDietaManual(API.user.id, payload);
                this.toast('Dieta creada correctamente');
            }
            this.closeModal('modal-dieta-manual');
            this.loadDashboard();
        } catch (e) {
            this.toast(e.message, 'error');
        }
    },

    // --- MODALS ---
    openModal(id) {
        document.getElementById(id).classList.add('active');
    },
    closeModal(id) {
        document.getElementById(id).classList.remove('active');
    }
};

function getCurrentMonday() {
    const d = new Date();
    const day = d.getDay();
    // Go back to Monday: Sunday(0)→-6, Mon(1)→0, Tue(2)→-1, Wed(3)→-2...
    const diff = day === 0 ? -6 : 1 - day;
    d.setDate(d.getDate() + diff);
    return d.toISOString().split('T')[0];
}

document.addEventListener('DOMContentLoaded', () => App.init());
