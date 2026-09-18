// Real Jellyfin and embedded Siphon UI; no mocked routes, stored tokens, or injected ApiClient.
import assert from 'node:assert/strict';
import { mkdir } from 'node:fs/promises';

let chromium;
try {
    ({ chromium } = await import('playwright'));
} catch {
    process.exit(77);
}
let input = '';
for await (const chunk of process.stdin) {
    input += chunk;
    if (input.length > 16384) throw new Error('Browser input exceeds limit');
}
const config = JSON.parse(input);
let browser;
try {
    browser = await chromium.launch({ headless: true, ...(config.chromiumExecutable ? { executablePath: config.chromiumExecutable } : {}) });
} catch {
    process.exit(77);
}
const errors = [];
let unexpectedProbes = 0;
const evidence = { personalLogin: false, nativeAdminLogin: false, preferenceSave: false, keyboardTabs: false, responsiveWidths: [], locales: [], memoryOnlyPortalSession: false, healthRefresh: false };
evidence.browserVersion = browser.version();
evidence.browserSelection = config.chromiumExecutable ? 'explicit-executable' : 'pinned-playwright';
const context = await browser.newContext({ viewport: { width: 1280, height: 900 }, locale: 'en-US' });
const page = await context.newPage();
page.setDefaultTimeout(30000);
page.on('pageerror', () => errors.push('uncaught-page-error'));
page.on('request', request => {
    const path = new URL(request.url()).pathname;
    if (request.method() === 'POST' && (path.endsWith('/Siphon/Addons/Validate') || (path.includes('/Siphon/Metadata/Providers/') && path.endsWith('/Test')))) unexpectedProbes++;
});
await mkdir(config.output, { recursive: true });

async function noOverflow(root) {
    const bounds = await page.locator(root).evaluate(element => ({ width: element.clientWidth, scroll: element.scrollWidth }));
    assert.ok(bounds.scroll <= bounds.width + 2, 'Page root overflows at current viewport');
}
async function keyboardTabs(root) {
    const first = page.locator(`${root} [role="tab"]`).first();
    await first.focus();
    await page.keyboard.press('ArrowRight');
    const second = page.locator(`${root} [role="tab"]`).nth(1);
    assert.equal(await second.getAttribute('aria-selected'), 'true');
    assert.ok(await second.evaluate(element => document.activeElement === element));
    await page.keyboard.press('Home');
    assert.equal(await first.getAttribute('aria-selected'), 'true');
}
let stage = 'personal-login';
try {
    await page.goto(config.base + '/Siphon/User');
    await page.locator('#username').fill('smoke-alice');
    await page.locator('#password').fill(config.password);
    await page.locator('#password').press('Enter');
    await page.locator('#authenticated').waitFor({ state: 'visible' });
    await page.locator('#searchMode').waitFor({ state: 'visible' });
    evidence.personalLogin = true;
    stage = 'personal-keyboard-and-preferences';
    await keyboardTabs('#SiphonUserPage');
    await page.locator('#preferencesTab').click();
    await page.locator('#searchMode').selectOption('Local');
    const save = page.waitForResponse(response => response.url().endsWith('/Siphon/Preferences') && response.request().method() === 'PUT');
    await page.locator('#savePreferences').click();
    assert.equal((await save).status(), 200);
    await page.locator('#reloadPreferences').click();
    await page.waitForFunction(() => document.querySelector('#searchMode')?.value === 'Local' && document.querySelector('#savePreferences')?.disabled);
    evidence.preferenceSave = true;
    for (const locale of ['fr', 'en']) {
        await page.locator('#portalLanguage').selectOption(locale);
        await page.waitForFunction(value => document.documentElement.lang === value, locale);
        assert.equal(await page.locator('#searchMode').inputValue(), 'Local');
        evidence.locales.push('personal-' + locale);
    }
    for (const width of [390, 1280]) {
        await page.setViewportSize({ width, height: 900 });
        await noOverflow('#SiphonUserPage');
        await page.screenshot({ path: `${config.output}/personal-${width}.png`, fullPage: true });
        evidence.responsiveWidths.push(width);
    }
    const storage = await page.evaluate(() => ({ local: { ...localStorage }, session: { ...sessionStorage }, url: location.href }));
    assert.ok(!JSON.stringify(storage).includes('AccessToken') && !JSON.stringify(storage).includes('api_key'));
    // Authenticate response token is not captured/persisted by the runner or browser storage.
    assert.ok(Object.keys(storage.local).every(key => /language|locale/i.test(key)), 'Portal stored non-language data');
    assert.equal(Object.keys(storage.session).length, 0);
    await page.reload();
    await page.locator('#signIn').waitFor({ state: 'visible' });
    assert.ok(await page.locator('#authenticated').isHidden());
    evidence.memoryOnlyPortalSession = true;

    // Native web login uses the actual form and Jellyfin's own ApiClient/session setup.
    stage = 'native-admin-login';
    // Jellyfin's native return URL avoids entering and tearing down the home
    // queries. Global network idleness is not a reliable SPA readiness signal.
    const adminRoute = '/configurationpage?name=siphon';
    await page.goto(config.base + '/web/index.html#/login?url=' + encodeURIComponent(adminRoute));
    stage = 'native-login-form';
    const manual = page.locator('#btnManual, .btnManual').or(page.getByRole('button', { name: /manual login|sign in manually/i })).first();
    const username = page.locator('input[autocomplete="username"]').first();
    // The manual button is visible in the initial template, then disappears when
    // the public-user response opens the form. Wait for a rendered login choice.
    const publicUserCard = page.locator('#divUsers .card').first();
    await Promise.any([publicUserCard.waitFor({ state: 'visible' }), username.waitFor({ state: 'visible' })]);
    stage = 'native-login-manual';
    if (!await username.isVisible()) await manual.click();
    stage = 'native-login-username';
    await username.fill('smoke-admin');
    stage = 'native-login-password';
    await page.locator('#txtManualPassword, input[name="password"]').first().fill(config.password);
    stage = 'native-admin-authentication';
    const [authenticated] = await Promise.all([
        page.waitForResponse(response => new URL(response.url()).pathname.toLowerCase().endsWith('/users/authenticatebyname')
            && response.request().method() === 'POST'),
        page.locator('#txtManualPassword, input[name="password"]').first().press('Enter')
    ]);
    evidence.nativeLoginStatus = authenticated.status();
    assert.equal(evidence.nativeLoginStatus, 200, 'Native administrator authentication failed');
    stage = 'native-admin-redirect';
    await page.waitForURL(url => url.hash === '#' + adminRoute, { timeout: 30000 });
    evidence.nativeAdminLogin = true;
    stage = 'embedded-admin-load';
    await page.locator('#SiphonConfigPage').waitFor({ state: 'visible' });
    await page.waitForFunction(() => document.querySelector('#siphonFields')?.disabled === false);
    await keyboardTabs('#SiphonConfigPage');
    evidence.keyboardTabs = true;
    stage = 'admin-health';
    await page.locator('#siphonTabDiagnostics').click();
    const health = page.waitForResponse(response => response.url().endsWith('/Siphon/Diagnostics/Health'));
    await page.locator('#siphonRefreshDiagnostics').click();
    assert.equal((await health).status(), 200);
    await page.locator('#siphonHealthFacts').waitFor({ state: 'visible' });
    evidence.healthRefresh = true;
    stage = 'admin-locales-and-responsive-layout';
    for (const locale of ['fr', 'en']) {
        stage = 'admin-locale-' + locale;
        await page.locator('#siphonLocale').selectOption(locale);
        await page.waitForFunction(value => document.querySelector('#SiphonConfigPage')?.lang === value, locale);
        evidence.locales.push('admin-' + locale);
    }
    for (const width of [390, 1280]) {
        stage = width === 390 ? 'admin-layout-mobile' : 'admin-layout-desktop';
        await page.setViewportSize({ width, height: 900 });
        await noOverflow('#SiphonConfigPage');
        await page.screenshot({ path: `${config.output}/admin-${width}.png`, fullPage: true });
    }
    stage = 'unexpected-provider-probes';
    assert.equal(unexpectedProbes, 0, 'Opening, translating or refreshing health initiated a provider probe');
    stage = 'uncaught-browser-exceptions';
    assert.equal(errors.length, 0, 'A browser page emitted an uncaught exception');
    process.stdout.write(JSON.stringify(evidence));
} catch (error) {
    // Never export browser exception/trace bodies: form values and URLs may carry credentials.
    evidence.failureCode = error?.name === 'TimeoutError' ? 'timeout' : error?.name === 'AssertionError' ? 'assertion' : 'error';
    process.stdout.write(JSON.stringify(evidence));
    process.stderr.write('SMOKE_STAGE=' + stage + '\n');
    process.exitCode = 1;
} finally {
    await context.close();
    await browser.close();
}
