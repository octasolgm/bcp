/**
 * Verifies dark/light theme switching across all Reguliq routes.
 * Usage: node scripts/verify-theme.mjs
 */
import { chromium } from 'playwright';

const BASE = process.env.APP_URL ?? 'http://localhost:3002';
const ROUTES = [
  '/dashboard',
  '/analyse',
  '/gap-analysis',
  '/regulations',
  '/documents',
  '/dual-verify',
];

const EXPECTED = {
  dark: 'rgb(11, 18, 30)',
  light: 'rgb(248, 250, 252)',
};

async function readShellBg(page) {
  return page.evaluate(() => {
    const el = document.querySelector('.shell');
    return el ? getComputedStyle(el).backgroundColor : null;
  });
}

async function readThemeAttrs(page) {
  return page.evaluate(() => ({
    dataTheme: document.documentElement.getAttribute('data-theme'),
    colorScheme: document.documentElement.style.colorScheme,
    stored: localStorage.getItem('reguliq-theme'),
  }));
}

async function openSettings(page) {
  await page.click('button[aria-label="Settings"]');
  await page.waitForSelector('.settings-panel', { state: 'visible' });
}

async function pickTheme(page, label) {
  await page.locator('.theme-option', { hasText: label }).click();
}

async function waitForTheme(page, theme) {
  await page.waitForFunction(
    (expected) => document.documentElement.getAttribute('data-theme') === expected,
    theme,
    { timeout: 5000 },
  );
}

async function main() {
  const browser = await chromium.launch({ channel: 'chrome', headless: true });
  const page = await browser.newPage();
  const failures = [];

  try {
    await page.goto(BASE, { waitUntil: 'networkidle', timeout: 60000 });

    for (const theme of ['Dark', 'Light']) {
      await openSettings(page);
      await pickTheme(page, theme);
      const resolved = theme.toLowerCase();
      await waitForTheme(page, resolved);

      const attrs = await readThemeAttrs(page);
      if (attrs.dataTheme !== resolved) {
        failures.push(`Theme attr mismatch for ${resolved}: got ${attrs.dataTheme}`);
      }
      if (attrs.colorScheme !== resolved) {
        failures.push(`colorScheme mismatch for ${resolved}: got ${attrs.colorScheme}`);
      }
      if (attrs.stored !== resolved) {
        failures.push(`localStorage mismatch for ${resolved}: got ${attrs.stored}`);
      }

      for (const route of ROUTES) {
        await page.goto(`${BASE}${route}`, { waitUntil: 'networkidle', timeout: 60000 });
        const bg = await readShellBg(page);
        const expected = EXPECTED[resolved];
        if (bg !== expected) {
          failures.push(`${route} @ ${resolved}: shell bg ${bg}, expected ${expected}`);
        }
        const dataTheme = await page.evaluate(() =>
          document.documentElement.getAttribute('data-theme'),
        );
        if (dataTheme !== resolved) {
          failures.push(`${route} @ ${resolved}: data-theme is ${dataTheme}`);
        }
      }

      await openSettings(page);
      await page.click('button[aria-label="Close"]');
    }

    // System mode persists
    await openSettings(page);
    await pickTheme(page, 'System');
    const systemAttrs = await readThemeAttrs(page);
    if (systemAttrs.stored !== 'system') {
      failures.push(`System mode not stored: ${systemAttrs.stored}`);
    }

    if (failures.length) {
      console.error('Theme verification FAILED:\n' + failures.map((f) => `  - ${f}`).join('\n'));
      process.exit(1);
    }

    console.log(`Theme verification PASSED on ${ROUTES.length} routes (dark + light).`);
  } finally {
    await browser.close();
  }
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
