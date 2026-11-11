// Rendert die Grafiken für den Play-Store-Eintrag (Playwright mit Chromium):
//   node tools/render_store_graphics.js
// Ausgabe: docs/store/icon-512.png, docs/store/feature-graphic.png
const { chromium } = require('playwright');
const fs = require('fs');
const path = require('path');

const root = path.join(__dirname, '..');
const store = path.join(root, 'docs', 'store');
const logo = fs.readFileSync(path.join(root, 'docs', 'logo', 'zboard-logo.svg'), 'utf8');
const icon = fs.readFileSync(path.join(store, 'icon-512.svg'), 'utf8');

// Farben von world-of-bytes.de
const feature = `<!doctype html><html><head><style>
  html, body { margin: 0; width: 1024px; height: 500px; overflow: hidden; }
  body {
    background: radial-gradient(70% 120% at 100% 0%, rgba(63,169,217,.22), transparent 60%),
                radial-gradient(60% 100% at 0% 100%, rgba(107,169,66,.16), transparent 60%), #07090D;
    font-family: "Barlow", "Segoe UI", "Roboto", "DejaVu Sans", sans-serif; color: #E8ECF3;
    display: flex; align-items: center; gap: 56px; padding: 0 84px; box-sizing: border-box;
  }
  .logo svg { width: 260px; height: 260px; display: block; }
  /* Das „z“ von zBoard ist immer schwarz oder weiß – hier weiß, nur „Board“ trägt den Markenverlauf */
  h1 { margin: 0; font-size: 104px; line-height: 1; letter-spacing: -2px; font-weight: 700; color: #FFFFFF; }
  h1 .board { background: linear-gradient(95deg, #B1C740 0%, #6BA942 35%, #3FA9D9 70%, #387ABE 100%);
       -webkit-background-clip: text; background-clip: text; color: transparent; }
  p { margin: 18px 0 0; font-size: 34px; line-height: 1.3; color: #C9D0DB; }
  .by { margin-top: 28px; font-size: 22px; color: #98A2B3; letter-spacing: 1px; }
</style></head><body>
  <div class="logo">${logo.replace(/width="\d+" height="\d+"/, '')}</div>
  <div><h1>z<span class="board">Board</span></h1><p>Die freie Tastatur.<br>Schnell. Offline. Open Source.</p><div class="by">WORLD OF BYTES</div></div>
</body></html>`;

(async () => {
  const browser = await chromium.launch();
  const page = await browser.newPage({ viewport: { width: 512, height: 512 } });
  await page.setContent(`<html><body style="margin:0">${icon}</body></html>`);
  await page.screenshot({ path: path.join(store, 'icon-512.png'), clip: { x: 0, y: 0, width: 512, height: 512 } });

  await page.setViewportSize({ width: 1024, height: 500 });
  await page.setContent(feature);
  await page.screenshot({ path: path.join(store, 'feature-graphic.png') });
  await browser.close();
  console.log('docs/store/icon-512.png, docs/store/feature-graphic.png');
})();
