const { test } = require('node:test');
const assert = require('node:assert/strict');
const reader = require('../../src/OtterMonitor.App/AnthropicBalanceReader.js');
const billing = 'https://platform.claude.com/settings/billing';

test('USD amounts: zero, negative and thousands; malformed/foreign values rejected', () => {
  for (const [text, expected] of [['$0', '0.00'], ['$24.50', '24.50'], ['-$0.50', '-0.50'], ['$-2.75', '-2.75'], ['US$1,234.56', '1234.56']]) assert.deepEqual(reader.dollars(text), [expected]);
  for (const text of ['$1,23', '$1.234', '$1,234,56', '$1e3', 'CA$7.20', 'AU$7.20', 'EUR $7.20', 'CAD $7', '€7', '$1000000000']) assert.deepEqual(reader.dollars(text), []);
});
test('Only the real Billing overview origin/route is readable', () => {
  assert.equal(reader.isBilling('https://console.anthropic.com/settings/billing/'), true);
  assert.equal(reader.isBilling('https://claude.ai/settings/usage'), false);
  assert.equal(reader.isBilling(billing), true);
  assert.equal(reader.isBilling(billing + '?test=1'), true);
  for (const url of ['http://platform.claude.com/settings/billing', 'https://platform.claude.com:8443/settings/billing', 'https://evil.test/settings/billing', 'https://platform.claude.com.evil.test/settings/billing', 'https://platform.claude.com/settings/organization/billing/history', 'not-url']) assert.equal(reader.isBilling(url), false);
});
// A DOM fixture matching the observed label/span and sibling amount card. Warnings live outside it.
class Element {
  constructor(tag, text = '', children = [], hidden = false, attributes = {}) { this.tag = tag; this.tagName = tag.toUpperCase(); this.text = text; this.children = children; this.hidden = hidden; this.attributes = attributes; children.forEach(child => child.parentElement = this); }
  getAttribute(name) { return this.attributes[name] ?? null; }
  get textContent() { return this.text + this.children.map(child => child.textContent).join(' '); }
  get innerText() { return this.hidden ? '' : this.text + this.children.filter(child => !child.hidden).map(child => child.innerText).join(' '); }
  getClientRects() { return this.hidden || this.parentElement?.getClientRects().length === 0 ? [] : [{}]; }
  querySelector() { return this.children.flatMap(child => [child, ...child.descendants()]).find(child => ['button', 'a', 'h1', 'h2', 'h3', 'input', 'select'].includes(child.tag)); }
  descendants() { return this.children.flatMap(child => [child, ...child.descendants()]); }
}
const el = (tag, text, children, hidden) => new Element(tag, text, children, hidden);
function card(amount = '$24.50', hidden = false) { return el('div', '', [el('span', '', [el('span', 'Credit balance')]), el('div', amount)], hidden); }
function document(...children) { const root = el('div', '', children); return { querySelectorAll: selector => root.descendants().filter(element => selector.includes('header a[href]') ? element.tag === 'a' && element.getAttribute('href') && ancestors(element).some(parent => parent.tag === 'header' || parent.getAttribute('role') === 'banner') : selector.split(',').includes(element.tag)), defaultView: { getComputedStyle: element => ({ visibility: element.hidden ? 'hidden' : 'visible' }) } }; }
function ancestors(element) { const parents=[]; for(let parent=element.parentElement;parent;parent=parent.parentElement)parents.push(parent); return parents; }
function total(amount, href='/settings/billing', hidden=false) { return el('header','',[new Element('a',amount,[],hidden,{href})]); }
test('Exact balance card wins over $0 warning and Buy credits button', () => {
  assert.deepEqual(reader.read(document(card(), el('p', 'When your credit reaches $0, requests stop.'), el('button', 'Buy credits')), billing), { schema: 1, state: 'ok', amount: '24.50', currency: 'USD' });
});
test('Hidden, conflicting, missing or malformed balance fields never become a guessed amount', () => {
  for (const doc of [document(card(''), el('p', 'Your credit reaches $0')), document(card('', false), el('p', 'Your credit reaches $0'), el('button', 'Buy credits')), document(card('$7 $8')), document(card('$7'), card('$8')), document(card('$7', true)), document(card('Loading…'), el('p', '$0')), document(card('EUR $7'))]) assert.deepEqual(reader.read(doc, billing), { schema: 1, state: 'unreadable' });
  assert.deepEqual(reader.read(document(card()), 'https://platform.claude.com/home'), { schema: 1, state: 'paused' });
});
test('Current Console total comes from its header, independently of purchased, promotional and invoice amounts', () => {
  const doc=document(total('$41.27'),card('$1.25'),el('p','$40.00 promotional credit available'),el('button','Buy credits $100'),el('table','$99.00'));
  assert.deepEqual(reader.read(doc,billing),{schema:1,state:'ok',amount:'41.27',currency:'USD'});
  assert.deepEqual(reader.read(document(total('$0.00'),card('Loading…')),billing),{schema:1,state:'ok',amount:'0.00',currency:'USD'});
});
test('The total must be a visible exact USD amount on the real header Billing link', () => {
  for(const doc of [document(total('$7.00','https://evil.test/settings/billing')),document(total('$7.00','/settings/billing',true)),document(total('$7.00','/usage')),document(total('$7.00'),total('$8.00')),document(total('$1,23')),document(total('EUR $7.00')),document(total('Buy credits $50'))])
    assert.deepEqual(reader.read(doc,billing),{schema:1,state:'unreadable'});
  assert.deepEqual(reader.read(document(total('$7.00','https://console.anthropic.com/settings/billing')),billing),{schema:1,state:'ok',amount:'7.00',currency:'USD'});
});
