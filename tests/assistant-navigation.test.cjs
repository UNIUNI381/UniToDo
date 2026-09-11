const assert = require("node:assert/strict");
const { test } = require("node:test");
const filesystem = require("node:fs");
const virtualMachine = require("node:vm");

function createHarness(mobile = true) {
  // 会話スクリプトを実行し、DOMと非同期のブラウザ履歴移動を再現する。
  const surfaces = new Map();
  const listeners = {};
  const entries = [null];
  let position = 0;
  let pendingBack = false;
  function surface(identifier) {
    // 表示状態とイベントを保持する最小の画面要素を返す。
    if (!surfaces.has(identifier)) {
      const classes = new Set(["hidden"]);
      surfaces.set(identifier, {
        classList: {
          contains: name => classes.has(name),
          add: name => classes.add(name),
          remove: name => classes.delete(name),
          toggle: (name, enabled) => enabled ? classes.add(name) : classes.delete(name)
        },
        listeners: {}, value: "編集中の依頼", style: { setProperty() {}, removeProperty() {} },
        addEventListener(name, callback) { /* 要素のイベントを保存する。 */ this.listeners[name] = callback; },
        contains: () => false, focus() {}, blur() {}, append() {}, replaceChildren() {},
        querySelectorAll: () => [], scrollHeight: 100, scrollTop: 0, clientHeight: 100
      });
    }
    return surfaces.get(identifier);
  }
  const history = {
    get state() { /* 現在の履歴状態を返す。 */ return entries[position]; },
    pushState(state) { /* 進む履歴を置き換えてページ内履歴を追加する。 */ entries.splice(++position, entries.length, state); },
    back() { /* 実ブラウザ同様、移動通知は後で配送する。 */ pendingBack = true; }
  };
  const window = {
    history, matchMedia: () => ({ matches: mobile }), innerHeight: 800,
    addEventListener: (name, callback) => { listeners[name] = callback; },
    setTimeout: () => 1, clearTimeout() {}, requestAnimationFrame: () => 1
  };
  const document = { getElementById: surface, addEventListener() {}, createElement: surface };
  virtualMachine.runInNewContext(filesystem.readFileSync("src/TaskManager.App/wwwroot/assistant.js", "utf8"), {
    window, document, apiRequest: async () => ({ status: "idle", messages: [], prompts: [] })
  });
  return {
    window, surface, entries,
    get position() { /* 履歴位置を検査用に返す。 */ return position; },
    back() {
      // 利用者または閉じるボタンから要求された戻る操作を配送する。
      assert.ok(position > 0);
      position--;
      pendingBack = false;
      listeners.popstate();
    },
    forward() { /* 進む操作を配送する。 */ position++; listeners.popstate(); },
    get pendingBack() { /* ボタンによる履歴移動要求を返す。 */ return pendingBack; }
  };
}

test("スマホの戻るで会話だけを閉じ、進むで再表示する", async () => {
  // 重複オープン、本文保持、ページ内の戻る・進むを確認する。
  const harness = createHarness();
  await harness.window.unitodoAssistant.open();
  await harness.window.unitodoAssistant.open();
  assert.equal(harness.entries.length, 2);
  harness.back();
  assert.equal(harness.position, 0);
  assert.ok(harness.surface("assistant-panel").classList.contains("hidden"));
  assert.equal(harness.surface("assistant-text").value, "編集中の依頼");
  harness.forward();
  assert.equal(harness.surface("assistant-panel").classList.contains("hidden"), false);
  assert.equal(harness.entries.length, 2);
});

test("閉じる直後の再オープンは履歴移動を待ち、履歴を蓄積しない", async () => {
  // 閉じるボタンと再表示の非同期競合を検証する。
  const harness = createHarness();
  await harness.window.unitodoAssistant.open();
  harness.surface("assistant-close").listeners.click();
  assert.ok(harness.pendingBack);
  const reopened = harness.window.unitodoAssistant.open();
  harness.back();
  await reopened;
  assert.equal(harness.position, 1);
  assert.equal(harness.entries.length, 2);
  assert.equal(harness.surface("assistant-panel").classList.contains("hidden"), false);
});

test("PCでの通常開閉はブラウザ履歴を変更しない", async () => {
  // PCの既存のパネル開閉を維持する。
  const harness = createHarness(false);
  await harness.window.unitodoAssistant.open();
  harness.surface("assistant-close").listeners.click();
  assert.equal(harness.entries.length, 1);
  assert.equal(harness.pendingBack, false);
});
