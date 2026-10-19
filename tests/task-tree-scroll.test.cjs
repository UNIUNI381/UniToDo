const assert = require("node:assert/strict");
const { test } = require("node:test");
const filesystem = require("node:fs");
const virtualMachine = require("node:vm");

// 本体の描画とスクロール初期化を読み込み、非同期の描画順序を検証する。
const source = filesystem.readFileSync(require.resolve("../src/TaskManager.App/wwwroot/app.js"), "utf8");
const rendering = source.slice(source.indexOf("function renderTaskDependencyGraph("), source.indexOf("/** タスクツリー内の編集ボタン"));
const scrolling = source.slice(source.indexOf("function initializeDependencyGraphScrolling("), source.indexOf("/** 横スクロール領域を背景ドラッグ"));

function createScenario() {
  // DOMの再生成、非表示状態、アニメーションフレームを再現する。
  const frames = [];
  let visible = false;
  let containers = [];
  const panel = {
    id: "task-dependency-panel", open: true, dataset: {},
    classList: { add() { /* 表示属性の更新を代替する。 */ }, remove() { /* 表示属性の更新を代替する。 */ } },
    querySelectorAll() { /* 現行の系列を返す。 */ return containers; },
    contains(container) { /* 置換済みの系列を判定する。 */ return containers.includes(container); },
    set innerHTML(components) {
      // 再描画で横位置が失われるブラウザーの動作を再現する。
      containers = components.map(function createContainer(component) {
        // 各系列には最初の未完了段の座標と系列識別子を与える。
        return {
          dataset: { componentIdentifiers: component.identifier, initialLevelIndex: "2" },
          scrollLeft: 0,
          get offsetParent() { /* 非表示中の測定不能を再現する。 */ return visible && panel.open ? panel : null; },
          querySelector() { /* 未完了段の座標を返す。 */ return { offsetLeft: component.offset }; }
        };
      });
    }
  };
  const context = virtualMachine.createContext({
    applicationState: { tasks: [], dependencyGraphScrollShouldReset: true },
    document: { getElementById() { /* 対象パネルを返す。 */ return panel; } },
    window: { requestAnimationFrame(callback) { /* 次フレームの処理を予約する。 */ frames.push(callback); } },
    createDependencyGraphPresentation(components) { /* 系列構成を描画へ渡す。 */ return components; },
    createDependencyPanelContent(title, components) { /* DOM生成へ系列構成を渡す。 */ return components; },
    initializeTaskDependencyEditing() { /* 編集操作の接続を代替する。 */ },
    attachHorizontalDragScrolling(container) { /* ドラッグ初期化済みを記録する。 */ container.dataset.dragScrollingInitialized = "true"; },
    scheduleDependencyGraphDrawing() { /* 矢印描画の予約を代替する。 */ }
  });
  virtualMachine.runInContext(rendering + scrolling, context);
  return {
    context, panel,
    show() { /* タブ復元後の表示状態にする。 */ visible = true; },
    flushNext() { /* 最初の描画待ち処理だけを実行する。 */ frames.shift()(); },
    flush() {
      // 同じフレームへ予約された処理を先に実行し、次フレーム分は保留する。
      for (const callback of frames.splice(0)) callback();
    },
    render(components = [{ identifier: "series", offset: 512 }]) {
      // 本体の描画を通して保持済みDOMを置き換える。
      context.renderTaskDependencyGraph(components);
    },
    positions() { /* 系列ごとの横位置を返す。 */ return containers.map(container => container.scrollLeft); }
  };
}

test("アイコン再読込のタブ復元と直後のデータ更新でも未完了段を表示する", () => {
  // 非表示の初回取得からタブ復元後の再取得まで順に再現する。
  const scenario = createScenario();
  scenario.render();
  scenario.flush();
  assert.equal(scenario.context.applicationState.dependencyGraphScrollShouldReset, true);
  scenario.show();
  scenario.context.initializeDependencyGraphScrolling(scenario.panel, true);
  scenario.flush();
  assert.deepEqual(scenario.positions(), [500]);
  assert.equal(scenario.context.applicationState.dependencyGraphScrollShouldReset, false);
  scenario.render();
  scenario.flush();
  scenario.render();
  scenario.flush();
  assert.deepEqual(scenario.positions(), [500]);
});

test("利用者の先頭位置も系列ごとに引き継ぎ、新しい系列だけ未完了段へ移動する", () => {
  // 系列の表示順が変わっても識別子で位置を復元する。
  const scenario = createScenario();
  scenario.show();
  scenario.render();
  scenario.flush();
  scenario.panel.querySelectorAll()[0].scrollLeft = 0;
  scenario.render([{ identifier: "new-series", offset: 252 }, { identifier: "series", offset: 512 }]);
  scenario.flush();
  assert.deepEqual(scenario.positions(), [240, 0]);
  scenario.context.initializeDependencyGraphScrolling(scenario.panel, true);
  scenario.flush();
  assert.deepEqual(scenario.positions(), [240, 500]);
});

test("描画待ちに置換された古い系列は初期位置のリセットを消費しない", () => {
  // 初期化フレーム前にDOMを差し替えても現行系列だけを移動する。
  const scenario = createScenario();
  scenario.show();
  scenario.render();
  scenario.render([{ identifier: "replacement", offset: 732 }]);
  scenario.flushNext();
  assert.equal(scenario.context.applicationState.dependencyGraphScrollShouldReset, true);
  scenario.flush();
  assert.deepEqual(scenario.positions(), [720]);
});

test("閉じたツリーの更新を繰り返しても再展開時に保存位置を復元する", () => {
  // レイアウトが未確定の間も最後の利用者位置を引き継ぐ。
  const scenario = createScenario();
  scenario.show();
  scenario.render();
  scenario.flush();
  scenario.panel.querySelectorAll()[0].scrollLeft = 320;
  scenario.panel.open = false;
  scenario.render();
  scenario.flush();
  scenario.render();
  scenario.flush();
  scenario.panel.open = true;
  scenario.context.initializeDependencyGraphScrolling(scenario.panel, false);
  scenario.flush();
  assert.deepEqual(scenario.positions(), [320]);
});
