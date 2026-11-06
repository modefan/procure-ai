// The README screenshots (docs/readme-assets.md): light theme, 1440 x 900, against a running deployment with fresh demo data.
//   node tools/readme/screenshots.js <output folder> [shot names...]
// PLAYWRIGHT_CORE: path of a playwright-core package (default: resolve 'playwright-core'); CHROME_PATH: a Chromium to use (optional);
// PROCUREAI_URL: the app (default https://procure.localtest.me); ARGOCD_URL and ARGOCD_PASSWORD: also take the Argo CD shot.
const path = require('path');
const { chromium } = require(process.env.PLAYWRIGHT_CORE || 'playwright-core');
const BASE = process.env.PROCUREAI_URL || 'https://procure.localtest.me';
const OUT = process.argv[2];
const only = process.argv.slice(3);
const T = 30000;
const labels = { tom: 'Requester (Tom)', maria: 'Manager (Maria)', thandi: 'Finance (Thandi)', admin: 'Admin' };

async function login(browser, who) {
  const ctx = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1440, height: 900 }, colorScheme: 'light' });
  const page = await ctx.newPage();
  await page.goto(BASE);
  await page.getByTestId('demo-login').getByRole('button').filter({ has: page.getByTestId('demo-label').getByText(labels[who], { exact: true }) }).click({ timeout: T });
  await page.waitForURL(/\/app\/dashboard$/, { timeout: T });
  await page.getByTestId('user-menu').waitFor({ timeout: T });
  return page;
}
const settle = (page, ms = 2500) => page.waitForTimeout(ms);
const shot = (page, name, h) => page.screenshot({ path: path.join(OUT, name + '.png'), clip: h ? { x: 0, y: 0, width: 1440, height: h } : undefined });
async function openRequest(page, title) {
  await page.goto(BASE + '/app/requests');
  await page.getByRole('link', { name: title }).first().click({ timeout: T });
  await page.waitForURL(/\/app\/requests\/[0-9a-f-]{36}$/, { timeout: T });
}
const want = (n) => only.length === 0 || only.includes(n);

(async () => {
  const browser = await chromium.launch({ headless: true, executablePath: process.env.CHROME_PATH });
  try {
    const tom = await login(browser, 'tom');
    const maria = await login(browser, 'maria');
    const thandi = await login(browser, 'thandi');
    const admin = await login(browser, 'admin');

    if (want('dashboard')) { await maria.goto(BASE + '/app/dashboard'); await settle(maria, 4000); await shot(maria, 'dashboard'); }
    if (want('approvals')) { await maria.goto(BASE + '/app/approvals'); await settle(maria); await shot(maria, 'approvals', 400); }
    if (want('request-detail')) {
      await openRequest(tom, 'Laptops for the new analysts');
      await maria.goto(tom.url());
      await settle(maria, 2000);
      await tom.getByText(/also viewing/i).first().waitFor({ timeout: T }).catch(() => {});
      await settle(tom, 1500);
      await shot(tom, 'request-detail');
    }
    if (want('order-detail')) {
      await openRequest(tom, 'Ergonomic chairs for the help desk');
      const region = tom.getByRole('region', { name: 'Order and delivery' });
      await region.waitFor({ timeout: T });
      const po = (await region.innerText()).match(/PO-\d+/)[0];
      await tom.goto(BASE + '/app/orders');
      await tom.getByRole('link', { name: po, exact: true }).first().click({ timeout: T });
      await tom.waitForURL(/\/app\/orders\/[0-9a-f-]{36}$/, { timeout: T });
      await settle(tom); await shot(tom, 'order-detail');
    }
    if (want('invoice-match')) {
      await thandi.goto(BASE + '/app/invoices'); await settle(thandi);
      await thandi.getByRole('row').filter({ hasText: 'Mismatched' }).first().getByRole('link').first().click({ timeout: T });
      await thandi.waitForURL(/\/app\/invoices\/[0-9a-f-]{36}$/, { timeout: T });
      await settle(thandi); await shot(thandi, 'invoice-match');
    }
    if (want('budgets')) { await thandi.goto(BASE + '/app/budgets'); await settle(thandi); await shot(thandi, 'budgets', 400); }
    if (want('assistant')) {
      await tom.goto(BASE + '/app/assistant'); await settle(tom);
      await tom.getByTestId('question').fill('Who approves R 250 000 for IT-200?');
      await tom.getByTestId('send').click();
      await tom.getByTestId('send').waitFor({ timeout: 60000 });
      await tom.getByTestId('answer').last().locator('.md-answer table').waitFor({ timeout: T });
      await settle(tom, 1500); await shot(tom, 'assistant');
    }
    if (want('admin-matrix')) { await admin.goto(BASE + '/app/admin/authorization'); await settle(admin); await shot(admin, 'admin-matrix'); }
    if (want('admin-system-status')) { await admin.goto(BASE + '/app/admin/system'); await settle(admin, 12000); await shot(admin, 'admin-system-status'); }
    if (want('admin-access-check')) {
      await admin.goto(BASE + '/app/admin/access-check');
      await admin.locator('#check-user').fill('Maria');
      await admin.getByRole('option', { name: /^Maria/ }).first().click({ timeout: T });
      await admin.locator('#check-action').selectOption('approve');
      await admin.locator('#check-request').fill('PR-0016');
      await admin.getByRole('button', { name: 'Check' }).click();
      await settle(admin, 3000); await shot(admin, 'admin-access-check');
    }
    if (want('admin-data-changes')) { await admin.goto(BASE + '/app/admin/data-changes'); await settle(admin, 4000); await shot(admin, 'admin-data-changes'); }
    if (want('policy-documents')) { await admin.goto(BASE + '/app/admin/policies'); await settle(admin); await shot(admin, 'policy-documents', 540); }
    if (want('argocd-apps') && process.env.ARGOCD_PASSWORD) {
      const ctx = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1440, height: 900 }, colorScheme: 'light' });
      const page = await ctx.newPage();
      const argo = process.env.ARGOCD_URL || 'https://argocd.procure.localtest.me';
      await page.goto(argo + '/login');
      await page.locator('input').first().fill('admin');
      await page.locator('input[type=password]').fill(process.env.ARGOCD_PASSWORD);
      await page.getByRole('button', { name: /sign in/i }).click();
      await page.waitForURL(/applications/, { timeout: T });
      await settle(page, 4000); await shot(page, 'argocd-apps');
    }
  } finally {
    await browser.close();
  }
})().catch((e) => { console.error(e); process.exit(1); });
