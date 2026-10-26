import { Game } from './game.js';
import { loadIcons } from './icons.js';

window.addEventListener('DOMContentLoaded', () => {
  try {
    const game = new Game();
    window.__game = game;
    loadIcons();           // the HUD draws its glyphs as soon as the face lands
    game.boot();
  } catch (err) {
    const el = document.getElementById('err');
    if (el) el.textContent = 'BOOT ERROR: ' + (err && err.stack || err);
    console.error(err);
  }
});
