const { chromium } = require('playwright');
const fs = require('fs');
(async () => {
  const b = await chromium.launch();
  const p = await b.newPage();
  for (const [file, sizes] of [['full.svg', [256, 128, 64, 48, 40]], ['small.svg', [32, 24, 20, 16]]]) {
    const svg = fs.readFileSync(file, 'utf8');
    for (const s of sizes) {
      await p.setViewportSize({ width: s, height: s });
      await p.setContent(`<html><body style="margin:0;background:transparent">${svg.replace(/width="\d+" height="\d+"/, `width="${s}" height="${s}"`)}</body></html>`);
      await p.screenshot({ path: `icon-${s}.png`, omitBackground: true, clip: { x: 0, y: 0, width: s, height: s } });
    }
  }
  await b.close();
})();
