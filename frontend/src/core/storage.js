// localStorage behind one try/catch: a private-mode window has no storage and
// throws on the first call, and every setting in the client has to survive that
// the same way - the callers used to carry five copies of the same guard.

export function load(key, fallback) {
  try {
    const v = localStorage.getItem(key);
    return v === null ? fallback : v;
  } catch (e) {
    return fallback;
  }
}

export function save(key, value) {
  try { localStorage.setItem(key, value); } catch (e) { /* no storage, no memory */ }
}
