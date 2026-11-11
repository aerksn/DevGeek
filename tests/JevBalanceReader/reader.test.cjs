const { test } = require('node:test');
const assert = require('node:assert/strict');
const reader = require('../../src/OtterMonitor.App/JevBalanceReader.js');

test('JEV reads only the exact HTTPS TypeSafe Billing route', () => {
  for (const url of ['https://console.typesafe.ai/settings/billing', 'https://console.typesafe.ai:443/settings/billing/?x=1', 'https://console.typesafe.ai/billing', 'https://console.typesafe.ai/billing/?x=1']) assert.equal(reader.isBilling(url), true);
  for (const url of ['http://console.typesafe.ai/settings/billing', 'https://console.typesafe.ai:8443/settings/billing', 'https://console.typesafe.ai.evil.test/settings/billing', 'https://typesafe.ai/settings/billing', 'https://console.typesafe.ai/usage', 'https://console.typesafe.ai/settings/billing/history', 'https://console.typesafe.ai/billing/history', 'invalid']) assert.equal(reader.isBilling(url), false);
});

test('Available credit values support zero, negative and grouped USD without accepting estimates or other currencies', () => {
  for (const [text, expected] of [['$5.00', '5.00'], ['$0.00', '0.00'], ['$-0.50', '-0.50'], ['US$1,234.56', '1234.56'], [' $5 ', '5.00']]) assert.equal(reader.dollars(text), expected);
  for (const text of ['Loading…', '', '$1,23', '$1.234', '$1e3', '$1000000000', 'CA$5', 'EUR $5', '$5 $6', '$5 remaining', '$5 / month', 'Balance $5']) assert.equal(reader.dollars(text), null);
});
