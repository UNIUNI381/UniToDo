const assert = require("node:assert/strict");
const { test } = require("node:test");
const { mobileSwipeDirection, initializeMobileNavigation } = require("../src/TaskManager.App/wwwroot/mobile-navigation.js");
const filesystem = require("node:fs");
const virtualMachine = require("node:vm");

test("初回タップではバーを出さず、横移動後は自動的に隠す", () => {
  // 本体のイベント処理を読み込み、タップからスクロールまで同じ領域で検証する。
  const source = filesystem.readFileSync(require.resolve("../src/TaskManager.App/wwwroot/app.js"), "utf8");
  const excerpt = source.slice(source.indexOf("function attachHorizontalDragScrolling("), source.indexOf("function scheduleDependencyGraphDrawing("));
  const callbacks = {};
  const timers = new Map();
  const classes = new Set();
  let timerSequence = 0;
  let captured = false;
  const context = virtualMachine.createContext({
    horizontalScrollbarHideTimers: new WeakMap(),
    window: {
      setTimeout(callback) {
        // 実時間を待たずタイマー発火を再現する。
        timers.set(++timerSequence, callback);
        return timerSequence;
      },
      clearTimeout(identifier) {
        // 再スクロール時の期限延長を再現する。
        timers.delete(identifier);
      }
    }
  });
  virtualMachine.runInContext(excerpt, context);
  const navigation = {
    id: "navigation", dataset: {}, scrollLeft: 0,
    classList: { add: value => classes.add(value), remove: value => classes.delete(value), contains: value => classes.has(value) },
    addEventListener: (name, callback) => { callbacks[name] = callback; },
    setPointerCapture: () => { captured = true; }, hasPointerCapture: () => captured,
    releasePointerCapture: () => { captured = false; }
  };
  const pressed = { button: 0, pointerId: 1, clientX: 100, target: { closest: () => false }, preventDefault() {} };
  context.attachHorizontalDragScrolling(navigation);
  callbacks.pointerdown(pressed);
  assert.equal(captured, false);
  callbacks.pointerup(pressed);
  callbacks.wheel();
  assert.equal(classes.has("scrollbar-active"), false);
  navigation.scrollLeft = 40;
  callbacks.scroll();
  assert.equal(classes.has("scrollbar-active"), true);
  for (const callback of [...timers.values()]) callback();
  assert.equal(classes.has("scrollbar-active"), false);
  timers.clear();
  callbacks.pointerdown(pressed);
  callbacks.pointermove({ ...pressed, clientX: 50 });
  assert.equal(captured, true);
  callbacks.scroll();
  callbacks.pointerup(pressed);
  assert.equal(classes.has("scrollbar-active"), true);
  for (const callback of [...timers.values()]) callback();
  assert.equal(classes.has("scrollbar-active"), false);
  callbacks.pointerdown(pressed);
  callbacks.pointerup(pressed);
  assert.equal(classes.has("scrollbar-active"), false);
});

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
