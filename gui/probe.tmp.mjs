import { chromium } from 'playwright';
import fs from 'node:fs';
const OUT = process.argv[2];
const env = Object.fromEntries(
  fs.readFileSync('/home/whym/Documents/SQLFlow/deploy/compose/.env', 'utf8')
    .split('\n').filter(l => l.includes('=') && !l.startsWith('#'))
    .map(l => { const i = l.indexOf('='); return [l.slice(0, i).trim(), l.slice(i + 1).trim()]; })
);
const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1400, height: 900 }, colorScheme: 'dark' });
await page.goto('http://localhost:8081/login', { waitUntil: 'networkidle' });
await page.getByTestId('login-username').fill(env.SQLFLOW_ADMIN_USERNAME || 'admin');
await page.getByTestId('login-password').fill(env.SQLFLOW_ADMIN_PASSWORD);
await page.getByTestId('login-submit').click();
await page.waitForTimeout(2500);
await page.goto('http://localhost:8081/semantic-layer', { waitUntil: 'networkidle' });
await page.waitForTimeout(1500);

await page.getByTestId('semantic-tree-filter').fill('Customer');
await page.waitForTimeout(2500);
await page.screenshot({ path: OUT + '/03-search.png' });

const rows = page.locator('[data-tree-row]');
console.log('rows:', await rows.count());
for (let i = 0; i < await rows.count(); i++) {
  await rows.nth(i).click();
  await page.waitForTimeout(1800);
  if (await page.getByTestId('semantic-deny-all').count()) {
    console.log('opened:', (await rows.nth(i).textContent() || '').trim().slice(0,60));
    break;
  }
}
await page.screenshot({ path: OUT + '/04-table.png' });
if (!(await page.getByTestId('semantic-deny-all').count())) { console.log('NO DENY ALL'); await browser.close(); process.exit(0); }

await page.getByTestId('semantic-deny-all').click();
await page.waitForTimeout(1500);
await page.screenshot({ path: OUT + '/05-dialog-dark.png' });
const info = await page.evaluate(() => {
  const el = document.querySelector('[data-testid="confirm-dialog"]');
  if (!el) return { found: false };
  const walk = [];
  let cur = el;
  while (cur && cur !== document.body) {
    const cs = getComputedStyle(cur);
    walk.push({ tag: cur.tagName, slot: cur.getAttribute('data-slot'), cls: (cur.className||'').toString().slice(0,120), bg: cs.backgroundColor, bgImage: cs.backgroundImage.slice(0,80) });
    cur = cur.parentElement;
  }
  const overlay = document.querySelector('[data-slot="alert-dialog-overlay"]');
  return { found: true, walk, overlayBg: overlay ? getComputedStyle(overlay).backgroundColor : null };
});
console.log(JSON.stringify(info, null, 2));
await browser.close();
