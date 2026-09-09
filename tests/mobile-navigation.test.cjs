const assert = require("node:assert/strict");
const { test } = require("node:test");
const { mobileSwipeDirection, initializeMobileNavigation } = require("../src/TaskManager.App/wwwroot/mobile-navigation.js");

test("スワイプ距離と横方向優勢の境界", () => {
  // 誤タップ、縦移動、閾値ちょうどの方向を確認する。
  assert.equal(mobileSwipeDirection(-63, 0), 0);
  assert.equal(mobileSwipeDirection(-64, 0), 1);
  assert.equal(mobileSwipeDirection(64, 0), -1);
  assert.equal(mobileSwipeDirection(90, 61), 0);
  assert.equal(mobileSwipeDirection(90, 60), -1);
});

test("タッチ操作の競合・中断・表示タブ限定", () => {
  // DOMイベントの入口を通して実際のジェスチャー管理を検証する。
  const listeners = {};
  let active = "dashboard";
  let overlay = false;
  let mobile = true;
  let selectedText = false;
  let transitions = 0;
  class Surface {
    constructor() {
      // 除外条件と横スクロールの寸法を保持する。
      this.excluded = false;
      this.scrollWidth = 100;
      this.clientWidth = 100;
      this.parentElement = null;
    }
    closest() {
      // 編集領域などの祖先一致を再現する。
      return this.excluded ? this : null;
    }
  }
  const content = { addEventListener: (name, callback) => { listeners[name] = callback; } };
  const target = new Surface();
  const buttons = ["dashboard", "tasks", "settings", "history"].map(view => ({ dataset: { view }, getClientRects: () => view === "settings" ? [] : [{}] }));
  global.Element = Surface;
  global.getComputedStyle = () => ({ overflowX: "auto" });
  global.document = {
    documentElement: { clientWidth: 390 }, activeElement: null,
    querySelector: selector => selector === ".main-content" ? content : overlay,
    querySelectorAll: () => buttons
  };
  global.window = { matchMedia: () => ({ matches: mobile }), getSelection: () => ({ type: selectedText ? "Range" : "None" }) };
  initializeMobileNavigation(() => active, destination => { active = destination; transitions++; });

  function touch(horizontal, vertical = 100) {
    // 単一指の座標を生成する。
    return { identifier: 1, clientX: horizontal, clientY: vertical };
  }
  function start(horizontal = 250) {
    // 本文上のタッチ開始を配送する。
    listeners.touchstart({ target, touches: [touch(horizontal)] });
  }
  function end(horizontal = 100, vertical = 100) {
    // 指を離した座標を配送する。
    listeners.touchend({ target, touches: [], changedTouches: [touch(horizontal, vertical)] });
  }
  start(); end(); assert.equal(active, "tasks");
  start(); end(); assert.equal(active, "history");
  start(); end(); assert.equal(transitions, 2);
  start(100); end(250); assert.equal(active, "tasks");
  start(10); end(200); assert.equal(active, "tasks");
  start(); listeners.touchcancel(); end(); assert.equal(active, "tasks");
  start(); listeners.touchmove({ touches: [touch(245, 130)], cancelable: true }); end(); assert.equal(active, "tasks");
  start(); listeners.touchmove({ touches: [touch(200), touch(210)], cancelable: true }); end(); assert.equal(active, "tasks");
  target.excluded = true; start(); end(); assert.equal(active, "tasks"); target.excluded = false;
  target.scrollWidth = 300; start(); end(); assert.equal(active, "tasks"); target.scrollWidth = 100;
  overlay = true; start(); end(); assert.equal(active, "tasks"); overlay = false;
  mobile = false; start(); end(); assert.equal(active, "tasks"); mobile = true;
  selectedText = true; start(); end(); assert.equal(active, "tasks"); selectedText = false;
  let prevented = false;
  start(); listeners.touchmove({ touches: [touch(210)], cancelable: true, preventDefault: () => { prevented = true; } }); end();
  assert.equal(prevented, true); assert.equal(active, "history");
});
