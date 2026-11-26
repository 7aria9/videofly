const { chromium } = require('playwright');
const fs = require('fs');
(async () => {
  const b = await chromium.launch();
  const p = await b.newPage({ deviceScaleFactor: 2 });
  const u = s => 'data:image/png;base64,' + fs.readFileSync(`icon-${s}.png`).toString('base64');
  const row = sizes => sizes.map(s => `<img src="${u(s)}" width="${s}" height="${s}">`).join('');
  await p.setViewportSize({ width: 760, height: 200 });
  await p.setContent(`<html><body style="margin:0">
    <div style="display:flex;gap:24px;align-items:flex-end;padding:28px;background:#0E0E11">${row([256,128,64,48,32,24,16])}</div>
    <div style="display:flex;gap:24px;align-items:flex-end;padding:28px;background:#F3F3F3">${row([64,48,32,24,16])}</div>
  </body></html>`);
  await p.screenshot({ path: 'preview.png', fullPage: true });
  await b.close();
})();
