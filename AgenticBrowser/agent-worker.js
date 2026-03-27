// Agent Worker - Uses Playwright accessibility snapshots (like OpenClaw)
// Communicates with C# host via JSON lines on stdin/stdout

const { chromium } = require('playwright');
const readline = require('readline');
const fs = require('fs');

let browser = null;
let context = null;
let page = null;

const rl = readline.createInterface({ input: process.stdin, terminal: false });

function respond(data) {
  process.stdout.write(JSON.stringify(data) + '\n');
}

function log(msg) {
  process.stderr.write(`[worker] ${msg}\n`);
}

// --- Connect to Chrome via CDP ---
async function connect(port) {
  browser = await chromium.connectOverCDP(`http://127.0.0.1:${port}`, { timeout: 15000 });
  context = browser.contexts()[0];
  if (!context) throw new Error('No browser context found');
  const pages = context.pages();
  page = pages.find(p => {
    try { return p.url() !== 'about:blank' && p.url() !== 'chrome://newtab/'; }
    catch { return false; }
  }) || pages[0];
  if (!page) page = await context.newPage();

  // Track new pages (popups, new tabs)
  context.on('page', newPage => {
    log(`New page opened: ${newPage.url()}`);
    page = newPage;
  });

  let title = '';
  try { title = await page.title(); } catch { title = '(unknown)'; }
  return { url: page.url(), title, pageCount: pages.length };
}

// --- Get page state: screenshot + accessibility snapshot ---
async function getState(screenshotPath) {
  // Re-acquire the active page (it may have changed after navigation)
  try {
    const pages = context.pages();
    if (pages.length > 0) {
      // Prefer the page that's not about:blank and was most recently active
      const activePage = pages.find(p => p.url() !== 'about:blank' && p.url() !== 'chrome://newtab/');
      if (activePage && activePage !== page) {
        log(`Switching to active page: ${activePage.url()}`);
        page = activePage;
      }
    }
  } catch (e) {
    log(`Page re-acquisition warning: ${e.message}`);
  }

  // Wait for page to settle — try load first, fall back gracefully
  try {
    await page.waitForLoadState('domcontentloaded', { timeout: 15000 });
  } catch (e) {
    log('waitForLoadState timed out, continuing: ' + e.message);
  }

  // Small extra wait for dynamic content
  await new Promise(r => setTimeout(r, 1000));

  // Take screenshot (JPEG, low quality for token savings)
  let screenshotBuffer;
  for (let attempt = 0; attempt < 3; attempt++) {
    try {
      screenshotBuffer = await page.screenshot({
        type: 'jpeg',
        quality: 30,
        scale: 'css',
        timeout: 15000
      });
      break;
    } catch (e) {
      log(`Screenshot attempt ${attempt + 1} failed: ${e.message}`);
      if (attempt === 2) throw e;
      // On retry, try re-acquiring the page again
      try {
        const pages = context.pages();
        const activePage = pages.find(p => p.url() !== 'about:blank') || pages[0];
        if (activePage) page = activePage;
        await page.waitForLoadState('domcontentloaded', { timeout: 5000 }).catch(() => {});
      } catch (_) {}
      await new Promise(r => setTimeout(r, 2000));
    }
  }
  log(`Screenshot size: ${(screenshotBuffer.length / 1024).toFixed(0)}KB`);
  const screenshotBase64 = screenshotBuffer.toString('base64');

  if (screenshotPath) {
    fs.writeFileSync(screenshotPath, screenshotBuffer);
  }

  // Get accessibility snapshot - this is what OpenClaw uses
  // It returns a compact tree of the page's accessible elements
  let ariaSnapshot = '';
  try {
    ariaSnapshot = await page.locator('body').first().ariaSnapshot({ timeout: 10000 });
  } catch (e) {
    log(`ariaSnapshot failed: ${e.message}, falling back to manual DOM extraction`);
    try {
      // Fallback: extract interactive elements manually
      ariaSnapshot = await page.evaluate(() => {
        const selectors = 'a[href],button,input,select,textarea,[role="button"],[role="link"],[role="textbox"],[role="checkbox"],[role="tab"],[role="menuitem"],[role="option"],[contenteditable="true"]';
        const results = [];
        document.querySelectorAll(selectors).forEach(el => {
          const rect = el.getBoundingClientRect();
          if (rect.width === 0 && rect.height === 0) return;
          if (rect.bottom < -50 || rect.top > window.innerHeight + 200) return;
          const tag = el.tagName.toLowerCase();
          const role = el.getAttribute('role') || (tag === 'a' ? 'link' : tag === 'button' ? 'button' : tag === 'input' ? 'textbox' : tag);
          const name = el.getAttribute('aria-label') || el.getAttribute('placeholder') || el.getAttribute('name') || (el.innerText || '').trim().substring(0, 50) || '';
          if (!name) return;
          const type = el.getAttribute('type') || '';
          const value = (el.value || '').substring(0, 30);
          let line = `- ${role} "${name}"`;
          if (type) line += ` type=${type}`;
          if (value) line += ` value="${value}"`;
          if (el.disabled) line += ' disabled';
          results.push(line);
        });
        return results.join('\n');
      });
      if (!ariaSnapshot) ariaSnapshot = '(no interactive elements found)';
    } catch (e2) {
      log(`Manual DOM extraction also failed: ${e2.message}`);
      ariaSnapshot = '(Could not extract page structure)';
    }
  }

  const url = page.url();
  const title = await page.title();

  // Get scroll info
  const scrollInfo = await page.evaluate(() => {
    const top = window.scrollY;
    const totalHeight = document.documentElement.scrollHeight;
    const viewHeight = window.innerHeight;
    const pageNum = Math.round(top / viewHeight) + 1;
    const totalPages = Math.max(1, Math.round(totalHeight / viewHeight));
    return `Scroll: page ${pageNum} of ${totalPages}`;
  });

  return {
    url,
    title,
    ariaSnapshot,
    screenshotBase64,
    scrollInfo
  };
}

// Fallback: format accessibility tree as text
function formatAccessibilityTree(node, depth) {
  if (!node) return '';
  const indent = '  '.repeat(depth);
  let result = '';

  const role = node.role || '';
  const name = node.name || '';
  const value = node.value || '';

  // Skip generic/none roles
  if (role && role !== 'none' && role !== 'generic') {
    let line = `${indent}- ${role}`;
    if (name) line += ` "${name}"`;
    if (value) line += ` value="${value}"`;
    if (node.checked !== undefined) line += ` checked=${node.checked}`;
    if (node.selected) line += ` selected`;
    if (node.disabled) line += ` disabled`;
    if (node.expanded !== undefined) line += ` expanded=${node.expanded}`;
    result += line + '\n';
  }

  if (node.children) {
    for (const child of node.children) {
      result += formatAccessibilityTree(child, depth + (role && role !== 'none' && role !== 'generic' ? 1 : 0));
    }
  }
  return result;
}

// --- Execute actions using Playwright's getByRole / getByLabel / locator ---
async function executeAction(action) {
  try {
    switch (action.type) {
      case 'click': {
        // Click by role + name (accessible name matching)
        log(`Click: role=${action.role || 'any'}, name="${action.name || ''}", index=${action.nth || 0}`);
        const locator = resolveLocator(action);
        await locator.scrollIntoViewIfNeeded({ timeout: 3000 }).catch(() => {});
        await locator.click({ timeout: 5000 });
        await page.waitForTimeout(1000);
        return { ok: true, message: `Clicked ${action.role || 'element'} "${action.name || ''}"` };
      }

      case 'fill': {
        log(`Fill: role=${action.role || 'any'}, name="${action.name || ''}", text="${action.text?.substring(0, 30)}"`);
        const locator = resolveLocator(action);
        try {
          await locator.fill(action.text, { timeout: 5000 });
        } catch (fillErr) {
          log(`fill() failed: ${fillErr.message}, trying click+type`);
          await locator.click({ timeout: 3000 });
          if (action.clear !== false) {
            await page.keyboard.press('Control+a');
            await page.keyboard.press('Delete');
          }
          await page.keyboard.type(action.text, { delay: 30 });
        }
        await page.waitForTimeout(500);
        return { ok: true, message: `Filled "${action.text?.substring(0, 30)}" into ${action.role || 'element'} "${action.name || ''}"` };
      }

      case 'navigate': {
        await page.goto(action.url, { waitUntil: 'domcontentloaded', timeout: 30000 });
        await page.waitForTimeout(2000);
        return { ok: true, message: `Navigated to ${action.url}` };
      }

      case 'scroll': {
        const direction = action.direction || 'down';
        const amount = action.pages || 1;
        await page.evaluate(({ dir, pg }) => {
          const distance = pg * window.innerHeight * 0.8 * (dir === 'down' ? 1 : -1);
          window.scrollBy({ top: distance, behavior: 'smooth' });
        }, { dir: direction, pg: amount });
        await page.waitForTimeout(800);
        return { ok: true, message: `Scrolled ${direction} ${amount} page(s)` };
      }

      case 'press': {
        log(`Pressing keys: ${action.key}`);
        await page.keyboard.press(action.key);
        await page.waitForTimeout(500);
        return { ok: true, message: `Pressed: ${action.key}` };
      }

      case 'type': {
        log(`Typing text: "${action.text?.substring(0, 30)}" (${action.text?.length} chars)`);
        if (action.text.length > 100) {
          // For long text, paste via evaluate instead of typing char by char
          await page.evaluate(async (text) => {
            const el = document.activeElement;
            if (el) {
              // Try setting via execCommand (works in contenteditable)
              if (el.getAttribute('contenteditable') !== null || el.isContentEditable) {
                document.execCommand('insertText', false, text);
              } else {
                // For input/textarea, use native setter
                const proto = el.tagName === 'TEXTAREA'
                  ? window.HTMLTextAreaElement.prototype
                  : window.HTMLInputElement.prototype;
                const setter = Object.getOwnPropertyDescriptor(proto, 'value')?.set;
                if (setter) {
                  setter.call(el, (el.value || '') + text);
                  el.dispatchEvent(new Event('input', { bubbles: true }));
                  el.dispatchEvent(new Event('change', { bubbles: true }));
                }
              }
            }
          }, action.text);
        } else {
          await page.keyboard.type(action.text, { delay: 30 });
        }
        await page.waitForTimeout(500);
        return { ok: true, message: `Typed: "${action.text?.substring(0, 30)}..." (${action.text?.length} chars)` };
      }

      case 'select_option': {
        const locator = resolveLocator(action);
        await locator.selectOption(action.value, { timeout: 5000 });
        await page.waitForTimeout(500);
        return { ok: true, message: `Selected "${action.value}"` };
      }

      case 'check': {
        const locator = resolveLocator(action);
        await locator.check({ timeout: 5000 });
        return { ok: true, message: `Checked ${action.role || 'checkbox'} "${action.name || ''}"` };
      }

      case 'uncheck': {
        const locator = resolveLocator(action);
        await locator.uncheck({ timeout: 5000 });
        return { ok: true, message: `Unchecked ${action.role || 'checkbox'} "${action.name || ''}"` };
      }

      case 'go_back': {
        await page.goBack({ waitUntil: 'domcontentloaded', timeout: 15000 });
        await page.waitForTimeout(1000);
        return { ok: true, message: 'Navigated back' };
      }

      case 'wait': {
        const ms = action.ms || 2000;
        await page.waitForTimeout(ms);
        return { ok: true, message: `Waited ${ms}ms` };
      }

      case 'new_tab': {
        page = await context.newPage();
        if (action.url) {
          await page.goto(action.url, { waitUntil: 'domcontentloaded', timeout: 30000 });
        }
        await page.waitForTimeout(1000);
        return { ok: true, message: `Opened new tab${action.url ? ': ' + action.url : ''}` };
      }

      case 'switch_tab': {
        const pages = context.pages();
        if (action.index >= 0 && action.index < pages.length) {
          page = pages[action.index];
          await page.bringToFront();
          return { ok: true, message: `Switched to tab [${action.index}]: ${page.url()}` };
        }
        return { ok: false, error: `Tab index ${action.index} out of range (${pages.length} tabs)` };
      }

      case 'click_text': {
        // Click element containing specific text
        log(`Clicking text: "${action.text}"`);
        await page.getByText(action.text, { exact: action.exact || false }).first().click({ timeout: 5000 });
        await page.waitForTimeout(1000);
        return { ok: true, message: `Clicked text "${action.text}"` };
      }

      case 'click_selector': {
        // Fallback: click by CSS selector
        log(`Clicking selector: ${action.selector}`);
        await page.locator(action.selector).first().click({ timeout: 5000 });
        await page.waitForTimeout(1000);
        return { ok: true, message: `Clicked selector ${action.selector}` };
      }

      default:
        return { ok: false, error: `Unknown action type: ${action.type}` };
    }
  } catch (e) {
    log(`Action error: ${e.message}`);
    return { ok: false, error: e.message.substring(0, 300) };
  }
}

// Resolve a Playwright locator from role/name/label/placeholder
function resolveLocator(action) {
  if (action.selector) {
    return page.locator(action.selector).nth(action.nth || 0);
  }
  if (action.label) {
    return page.getByLabel(action.label, { exact: action.exact || false }).nth(action.nth || 0);
  }
  if (action.placeholder) {
    return page.getByPlaceholder(action.placeholder, { exact: action.exact || false }).nth(action.nth || 0);
  }
  if (action.role) {
    const opts = {};
    if (action.name) opts.name = action.name;
    if (action.exact !== undefined) opts.exact = action.exact;
    return page.getByRole(action.role, opts).nth(action.nth || 0);
  }
  if (action.text) {
    return page.getByText(action.text, { exact: action.exact || false }).nth(action.nth || 0);
  }
  throw new Error('No locator strategy provided (need role, label, placeholder, selector, or text)');
}

// --- Main command loop ---
rl.on('line', async (line) => {
  const trimmed = line.trim();
  if (!trimmed) return;

  try {
    const cmd = JSON.parse(trimmed);
    let result;

    switch (cmd.cmd) {
      case 'connect':
        result = await connect(cmd.port);
        respond({ ok: true, data: result });
        break;

      case 'get_state':
        result = await getState(cmd.screenshotPath || null);
        respond({ ok: true, data: result });
        break;

      case 'execute':
        result = await executeAction(cmd.action);
        respond(result);
        break;

      case 'list_tabs': {
        const tabs = context.pages().map((p, i) => ({ index: i, url: p.url(), active: p === page }));
        respond({ ok: true, data: { tabs } });
        break;
      }

      case 'quit':
        respond({ ok: true });
        setTimeout(() => process.exit(0), 100);
        break;

      default:
        respond({ ok: false, error: 'Unknown command: ' + cmd.cmd });
    }
  } catch (e) {
    log(`Command error: ${e.message}`);
    respond({ ok: false, error: e.message });
  }
});

rl.on('close', () => process.exit(0));
process.on('uncaughtException', (e) => {
  log(`Uncaught exception: ${e.message}`);
  respond({ ok: false, error: 'Uncaught: ' + e.message });
});

// Signal ready
respond({ ok: true, ready: true });
