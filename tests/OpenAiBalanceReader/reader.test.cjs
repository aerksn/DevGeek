const { test } = require('node:test');
const assert = require('node:assert/strict');
const reader = require('../../src/PortPilot.App/OpenAiBalanceReader.js');
const billing = 'https://platform.openai.com/settings/organization/billing/overview';

test('USD amounts: zero, negative and thousands; malformed/foreign values rejected', () => {
  for (const [text, expected] of [['$0', '0.00'], ['$24.50', '24.50'], ['-$0.50', '-0.50'], ['$-2.75', '-2.75'], ['US$1,234.56', '1234.56']]) assert.deepEqual(reader.dollars(text), [expected]);
  for (const text of ['$1,23', '$1.234', '$1,234,56', '$1e3', 'CA$7.20', 'AU$7.20', 'EUR $7.20', 'CAD $7', '€7', '$1000000000']) assert.deepEqual(reader.dollars(text), []);
});
test('Only the real Billing overview origin/route is readable', () => {
  assert.equal(reader.isBilling(billing), true);
  assert.equal(reader.isBilling(billing + '?test=1'), true);
  for (const url of ['http://platform.openai.com/account/billing', 'https://platform.openai.com:8443/account/billing', 'https://evil.test/account/billing', 'https://platform.openai.com.evil.test/account/billing', 'https://platform.openai.com/settings/organization/billing/history', 'not-url']) assert.equal(reader.isBilling(url), false);
});
// A DOM fixture matching the observed label/span and sibling amount card. Warnings live outside it.
class Element {
  constructor(tag, text = '', children = [], hidden = false) { this.tag = tag; this.tagName = tag.toUpperCase(); this.text = text; this.children = children; this.hidden = hidden; children.forEach(child => child.parentElement = this); }
  get textContent() { return this.text + this.children.map(child => child.textContent).join(' '); }
  get innerText() { return this.hidden ? '' : this.text + this.children.filter(child => !child.hidden).map(child => child.innerText).join(' '); }
  getClientRects() { return this.hidden || this.parentElement?.getClientRects().length === 0 ? [] : [{}]; }
  querySelector() { return this.children.flatMap(child => [child, ...child.descendants()]).find(child => ['button', 'a', 'h1', 'h2', 'h3', 'input', 'select'].includes(child.tag)); }
  descendants() { return this.children.flatMap(child => [child, ...child.descendants()]); }
}
const el = (tag, text, children, hidden) => new Element(tag, text, children, hidden);
function card(amount = '$24.50', hidden = false) { return el('div', '', [el('span', '', [el('span', 'API credit balance')]), el('div', amount)], hidden); }
function document(...children) { const root = el('div', '', children); return { querySelectorAll: selector => root.descendants().filter(element => selector.split(',').includes(element.tag)), defaultView: { getComputedStyle: element => ({ visibility: element.hidden ? 'hidden' : 'visible' }) } }; }
test('Exact balance card wins over $0 warning and Buy credits button', () => {
  assert.deepEqual(reader.read(document(card(), el('p', 'When your credit reaches $0, requests stop.'), el('button', 'Buy credits')), billing), { schema: 1, state: 'ok', amount: '24.50', currency: 'USD' });
});
test('Hidden, conflicting, missing or malformed balance fields never become a guessed amount', () => {
  for (const doc of [document(card(''), el('p', 'Your credit reaches $0')), document(card('', false), el('p', 'Your credit reaches $0'), el('button', 'Buy credits')), document(card('$7 $8')), document(card('$7'), card('$8')), document(card('$7', true)), document(card('Loading…'), el('p', '$0')), document(card('EUR $7'))]) assert.deepEqual(reader.read(doc, billing), { schema: 1, state: 'unreadable' });
  assert.deepEqual(reader.read(document(card()), 'https://platform.openai.com/home'), { schema: 1, state: 'paused' });
});
