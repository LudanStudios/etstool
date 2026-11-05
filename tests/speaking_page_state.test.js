// Read-only DOM fixtures. Never connects to ETS or changes a real page.
const fs = require('node:fs');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '..', 'ets_speaking_auto.py'), 'utf8');
const expression = source.match(/PAGE_STATE_JS = r"""([\s\S]*?)"""/)[1];

function state(label, surrounding, body, visible = true) {
  const doc = {
    body: { innerText: body },
    defaultView: { getComputedStyle: () => ({ visibility: visible ? 'visible' : 'hidden' }) },
    querySelector: () => null,
    querySelectorAll: selector => selector === 'span,div,p,label' ? [node] : [],
  };
  const parent = { innerText: surrounding, parentElement: null };
  const node = { innerText: label, parentElement: parent, ownerDocument: doc,
    getClientRects: () => visible ? [{}] : [] };
  return JSON.parse(vm.runInNewContext(expression, { document: doc }));
}
let current = state('录音中', '录音中\n00:30', '总计 25:00\n录音中\n00:30');
assert.equal(current.recording, true);
assert.equal(current.recording_remaining_seconds, 30, 'Must not use the overall 25-minute timer');
assert.equal(state('录音中30秒', '录音中30秒', '录音中30秒').recording_remaining_seconds, 30);
assert.equal(state('Recording', 'Recording\n0:05', 'Recording\n0:05').recording_remaining_seconds, 5);
assert.equal(state('答题准备', '答题准备\n00:30', '答题准备\n00:30').recording_remaining_seconds, null);
assert.equal(state('录音中', '录音中\n00:30', '总计25:00', false).recording_remaining_seconds, null);
console.log('PASS: recording countdown, seconds notation, English label, preparation exclusion, hidden-label exclusion');
