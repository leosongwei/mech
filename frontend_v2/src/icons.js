// Icon glyphs for the HUD.
//
// The single source of truth is frontend/icons.json: src/icons.js derives every
// glyph from it, and scripts/fetch-material-icons.sh subsets the vendored
// Material Symbols font (vendor/material-symbols/) down to exactly that list.
// Adding an icon = an entry in icons.json + re-running that script.
//
// The HUD draws in canvas, so there is no CSS step: the subset is bundled by
// webpack as an asset and registered with the FontFace API here.

import icons from '../icons.json';
import fontUrl from '../vendor/material-symbols/material-symbols-outlined.woff2';

const FAMILY = 'HUD Icons';

export const ICON = Object.fromEntries(
  icons.map((i) => [i.name, String.fromCodePoint(parseInt(i.codepoint, 16))]),
);

let ready = false;

export function iconsReady() {
  return ready;
}

// Register the subset.  Awaiting this is optional: drawIcon simply skips the
// glyph until the face lands (a few ms), so the HUD can fall back to its own
// primitive shapes in the meantime.
export async function loadIcons() {
  if (ready || typeof FontFace === 'undefined') return ready;
  try {
    const face = new FontFace(FAMILY, `url(${fontUrl})`);
    await face.load();
    document.fonts.add(face);
    ready = true;
  } catch (err) {
    console.warn('icon font failed to load', err);
  }
  return ready;
}

// One glyph centred on (x, y), sized by the font size.  Returns false when the
// font is not there yet, so the caller can draw its own placeholder.
export function drawIcon(ctx, name, x, y, size, color) {
  const glyph = ICON[name];
  if (!ready || !glyph) return false;
  ctx.save();
  ctx.font = `${size}px "${FAMILY}"`;
  ctx.fillStyle = color;
  ctx.textAlign = 'center';
  ctx.textBaseline = 'middle';
  ctx.fillText(glyph, x, y + size * 0.04);
  ctx.restore();
  return true;
}
