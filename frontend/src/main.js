import { Game } from './game.js';

window.addEventListener('DOMContentLoaded', () => {
  try {
    const game = new Game();
    window.__game = game;
    game.boot();
  } catch (err) {
    const el = document.getElementById('err');
    if (el) el.textContent = 'BOOT ERROR: ' + (err && err.stack || err);
    console.error(err);
  }
});
