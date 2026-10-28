// Shared helpers for index.html and dashboard.html.

const ADMIN_PASSWORD_KEY = 'adminPassword';

class ApiError extends Error {
    constructor(message, status) {
        super(message);
        this.name = 'ApiError';
        this.status = status;
    }
}

function getAdminPassword() {
    return sessionStorage.getItem(ADMIN_PASSWORD_KEY) || '';
}

function setAdminPassword(password) {
    sessionStorage.setItem(ADMIN_PASSWORD_KEY, password);
}

function clearAdminPassword() {
    sessionStorage.removeItem(ADMIN_PASSWORD_KEY);
}

// Fetches a URL with the stored admin password and returns the JSON body (or null).
// Non-2xx responses throw ApiError carrying the server's {message}; a 401 also clears
// the stored password and calls onUnauthorized when given.
async function apiFetch(url, options = {}, onUnauthorized) {
    const headers = new Headers(options.headers || {});
    const password = getAdminPassword();
    if (password) headers.set('X-Admin-Password', password);

    const response = await fetch(url, { ...options, headers });
    const isJson = (response.headers.get('Content-Type') || '').includes('application/json');
    const data = isJson ? await response.json().catch(() => null) : null;

    if (!response.ok) {
        if (response.status === 401) {
            clearAdminPassword();
            if (onUnauthorized) onUnauthorized();
        }
        const message = data && data.message ? data.message : `Request failed (${response.status}).`;
        throw new ApiError(message, response.status);
    }

    return data;
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
