// Shared helpers for every page.

const SAFE_RETURN_URLS = ['/', '/index.html', '/dashboard.html'];

class ApiError extends Error {
    constructor(message, status, retryAfter) {
        super(message);
        this.name = 'ApiError';
        this.status = status;
        this.retryAfter = retryAfter;
    }
}

// Fetches a URL with the session cookie and returns the JSON body (or null).
// Non-2xx responses throw ApiError carrying the server's {message}; a 401 also calls
// onUnauthorized when given.
async function apiFetch(url, options = {}, onUnauthorized) {
    const headers = new Headers(options.headers || {});
    headers.set('X-Requested-With', 'XMLHttpRequest');

    const response = await fetch(url, { ...options, headers, credentials: 'same-origin' });
    const isJson = (response.headers.get('Content-Type') || '').includes('application/json');
    const data = isJson ? await response.json().catch(() => null) : null;

    if (!response.ok) {
        if (response.status === 401 && onUnauthorized) onUnauthorized();
        const message = data && data.message ? data.message : `Request failed (${response.status}).`;
        const retryAfter = Number(response.headers.get('Retry-After')) || 0;
        throw new ApiError(message, response.status, retryAfter);
    }

    return data;
}

// Returns the signed-in user ({username, role}), or null when not signed in.
async function loadCurrentUser() {
    try {
        return await apiFetch('/api/auth/me');
    } catch (error) {
        if (error instanceof ApiError && error.status === 401) return null;
        throw error;
    }
}

// Returns value only when it is a known same-site page; anything else becomes '/'.
function safeReturnUrl(value) {
    return SAFE_RETURN_URLS.includes(value) ? value : '/';
}

function loginUrl(returnTo) {
    return `/login.html?returnUrl=${encodeURIComponent(safeReturnUrl(returnTo))}`;
}

async function logout() {
    try {
        await apiFetch('/api/auth/logout', { method: 'POST' });
    } finally {
        location.href = '/login.html';
    }
}

// Builds the shared header (brand, nav, user chip, Log Out) with DOM APIs only.
function renderAppHeader(container, user, activePage) {
    const bar = document.createElement('div');
    bar.className = 'app-header';

    const brand = document.createElement('a');
    brand.className = 'brand';
    brand.href = '/';
    brand.textContent = 'Internal File Server';
    bar.appendChild(brand);

    const nav = document.createElement('nav');
    nav.className = 'nav';
    nav.setAttribute('aria-label', 'Main');
    const links = [{ page: 'files', href: '/', text: 'File List' }];
    if (user.role === 'Admin') links.push({ page: 'dashboard', href: '/dashboard.html', text: 'Dashboard' });
    links.forEach(link => {
        const anchor = document.createElement('a');
        anchor.href = link.href;
        anchor.textContent = link.text;
        if (link.page === activePage) anchor.setAttribute('aria-current', 'page');
        nav.appendChild(anchor);
    });
    bar.appendChild(nav);

    const chip = document.createElement('div');
    chip.className = 'user-chip';
    const name = document.createElement('span');
    name.className = 'user-name';
    name.textContent = user.username;
    const badge = document.createElement('span');
    badge.className = 'role-badge';
    badge.textContent = user.role;
    const logoutButton = document.createElement('button');
    logoutButton.type = 'button';
    logoutButton.className = 'secondary';
    logoutButton.textContent = 'Log Out';
    logoutButton.addEventListener('click', logout);
    chip.append(name, badge, logoutButton);
    bar.appendChild(chip);

    container.replaceChildren(bar);
}

// Flattens a folder tree into [{ path, name, depth }] in display order.
function flattenTree(nodes, depth = 0, result = []) {
    nodes.forEach(node => {
        result.push({ path: node.path, name: node.name, depth });
        flattenTree(node.children || [], depth + 1, result);
    });
    return result;
}

function formatSize(bytes) {
    const units = ['B', 'KB', 'MB', 'GB'];
    let value = bytes;
    let unit = 0;
    while (value >= 1024 && unit < units.length - 1) {
        value /= 1024;
        unit++;
    }
    return `${value.toFixed(unit === 0 ? 0 : 1)} ${units[unit]}`;
}

function escapeHtml(value) {
    return String(value).replace(/[&<>"']/g, char => ({
        '&': '&amp;',
        '<': '&lt;',
        '>': '&gt;',
        '"': '&quot;',
        "'": '&#39;'
    }[char]));
}

function escapeAttr(value) {
    return escapeHtml(value).replace(/`/g, '&#96;');
}
