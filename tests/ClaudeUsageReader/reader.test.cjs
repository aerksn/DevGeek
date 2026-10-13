const { test } = require('node:test');
const assert = require('node:assert/strict');
const reader = require('../../src/PortPilot.App/ClaudeUsageReader.js');
const url = 'https://claude.ai/settings/usage';

// Small structural DOM fixtures. The same cases also run in real WebView2.
class Element {
  constructor(tag, text = '', children = [], attributes = {}, hidden = false) {
    this.tagName = tag.toUpperCase(); this.text = text; this.children = children; this.attributes = attributes; this.hidden = hidden;
    children.forEach(child => child.parentElement = this);
  }
  get textContent() { return this.text + this.children.map(child => child.textContent).join(' '); }
  get innerText() { return this.hidden ? '' : this.text + this.children.filter(child => !child.hidden).map(child => child.innerText).join(' '); }
  getClientRects() { return this.hidden || this.parentElement?.getClientRects().length === 0 ? [] : [{}]; }
  contains(other) { return other === this || this.children.some(child => child.contains(other)); }
  getAttribute(name) { return this.attributes[name] ?? null; }
  descendants() { return this.children.flatMap(child => [child, ...child.descendants()]); }
  querySelectorAll(selector) {
    return this.descendants().filter(element => selector.split(',').some(part => {
      const match = /^([a-z0-9]+)?(?:\[([a-z-]+)(?:="([^"]+)")?\])?$/.exec(part.trim());
      return match && (!match[1] || element.tagName === match[1].toUpperCase())
        && (!match[2] || (element.getAttribute(match[2]) !== null && (!match[3] || element.getAttribute(match[2]) === match[3])));
    }));
  }
  querySelector(selector) { return this.querySelectorAll(selector)[0] ?? null; }
}
const el = (tag, text = '', children = [], attrs = {}, hidden = false) => new Element(tag, text, children, attrs, hidden);
function document(...children) {
  const root = el('body', '', children);
  return { querySelectorAll: s => root.querySelectorAll(s), defaultView: { getComputedStyle: e => ({ visibility: e.hidden ? 'hidden' : 'visible' }) } };
}
function row(label, value, deep = false) {
  let title = el(deep ? 'h5' : 'h3', label);
  if (deep) for (let i = 0; i < 6; i++) title = el('div', '', [title]);
  return el('section', '', [title, typeof value === 'string' ? el('p', value) : value]);
}
const windows = (...rows) => reader.read(document(...rows), url);

test('Current settings dialog selects session/all-model rows and excludes the sidebar, Fable and cloud credits', () => {
  const modal = el('div', '', [el('h2', 'Plan usage limits'), row('Current session', '0% used', true), el('h2', 'Weekly limits'),
    row('All models', '13% used', true), row('Fable', '16% used'), row('Cloud session credits', '$250 of $250 left')], { role: 'dialog' });
  assert.deepEqual(reader.read(document(row('Current session', '99% used'), modal), url), {
    state: 'ok', windows: [{ minutes: 300, usedPercent: 0 }, { minutes: 10080, usedPercent: 13 }]
  });
});
test('Prefix and explicit remaining percentages preserve their meaning', () => {
  assert.deepEqual(windows(row('Current session', 'Used 29%'), row('All models', '87% remaining')).windows,
    [{ minutes: 300, usedPercent: 29 }, { minutes: 10080, usedPercent: 13 }]);
});
test('Native and accessible fractional progress agree with rendered percentages', () => {
  const progress = el('progress', '', [], { max: '1', value: '0.29' });
  assert.equal(windows(row('Current session', progress)).windows[0].usedPercent, 29);
  const bar = el('div', '', [], { role: 'progressbar', 'aria-valuemin': '0', 'aria-valuemax': '1', 'aria-valuenow': '0.29' });
  assert.equal(windows(row('Current session', el('div', '29% used', [bar]))).windows[0].usedPercent, 29);
});
test('Missing, conflicting, negative and out-of-range values never become guessed or zero usage', () => {
  for (const value of ['', 'Loading…', '-5% used', '120% used', '1,2,3% used', '20% used 50% used']) assert.equal(windows(row('Current session', value)).state, 'unreadable');
  assert.equal(windows(row('Current session', '20% used'), row('Current session', '40% used')).state, 'unreadable');
});
test('Model-only and paid-credit fields cannot become an all-model weekly quota', () => {
  assert.equal(windows(el('h2', 'Weekly limits'), row('Fable', '16% used'), row('Included credit', '$250 left')).state, 'unreadable');
});
test('Swedish labels, genuine zero and hidden duplicates remain supported', () => {
  assert.deepEqual(windows(el('div', '', [row('Aktuell session', '80% använt')], {}, true), row('Aktuell session', '0% använt'), row('Alla modeller', '45% använt')).windows,
    [{ minutes: 300, usedPercent: 0 }, { minutes: 10080, usedPercent: 45 }]);
});
test('Only the HTTPS Claude usage page can supply quota observations', () => {
  for (const location of ['http://claude.ai/settings/usage', 'https://claude.ai.example.com/settings/usage', 'https://claude.ai:8443/settings/usage', 'invalid']) {
    assert.equal(reader.isUsage(location), false);
    assert.notEqual(reader.read(document(row('Current session', '40% used')), location).state, 'ok');
  }
});

test('The real SPA usage dialog remains readable after its route changes to /new', () => {
  const modal = el('div', '', [el('h3', 'Plan usage limits'), row('Current session', '0% used'),
    el('h3', 'Weekly limits'), row('All models', '13% used'), row('Fable', '16% used')], { role: 'dialog' });
  assert.deepEqual(reader.read(document(row('Current session', '99% used'), modal), 'https://claude.ai/new'), {
    state: 'ok', windows: [{ minutes: 300, usedPercent: 0 }, { minutes: 10080, usedPercent: 13 }]
  });
});
test('Other Claude routes require a visible usage dialog and never read chat or unrelated dialogs', () => {
  assert.equal(reader.read(document(row('Current session', '20% used')), 'https://claude.ai/new').state, 'paused');
  const unrelated = el('div', '', [row('Current session', '20% used')], { role: 'dialog' });
  assert.equal(reader.read(document(unrelated), 'https://claude.ai/new').state, 'paused');
  const hidden = el('div', '', [el('h3', 'Plan usage limits'), row('Current session', '20% used')], { role: 'dialog' }, true);
  assert.equal(reader.read(document(hidden), 'https://claude.ai/new').state, 'paused');
});
test('A usage dialog cannot bypass the exact HTTPS origin or read login pages', () => {
  const modal = el('div', '', [el('h3', 'Plan usage limits'), row('Current session', '0% used')], { role: 'dialog' });
  for (const location of ['https://claude.ai.example.com/new', 'http://claude.ai/new', 'https://claude.ai:8443/new', 'https://claude.ai/login'])
    assert.notEqual(reader.read(document(modal), location).state, 'ok');
});
