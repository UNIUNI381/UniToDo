const assert = require("node:assert/strict");
const { test } = require("node:test");
const filesystem = require("node:fs");
const virtualMachine = require("node:vm");
const source = filesystem.readFileSync(require("node:path").join(__dirname, "../src/TaskManager.App/wwwroot/app.js"), "utf8");
const functions = source.slice(source.indexOf("async function executeTaskAction("), source.indexOf("/** 全タスクを取得して一覧と下書きを更新する。 */"));

test("編集画面は保存成功後だけ開始し、保存失敗と二重送信では開始を増やさない", async () => {
  // 実際の保存処理で保存・開始の順序と失敗経路を確認する。
  for (const failure of [false, true]) {
    const operations = [];
    const values = { identifier: "edited", status: "実行可能", deadlineOrigin: "none" };
    const form = { dataset: {}, elements: { namedItem(name) {
      // 未指定項目には空のフォーム値を返す。
      return { value: values[name] || "", checked: false };
    } } };
    const context = virtualMachine.createContext({
      fromLocalInput() { /* 期限なしを再現する。 */ return null; },
      async apiRequest() {
        // 保存失敗では開始処理へ進めないことを確認する。
        operations.push("save");
        if (failure) throw new Error("保存失敗");
      },
      closeTaskDialog() { /* ダイアログを閉じる順序を記録する。 */ operations.push("close"); },
      showNotice() { /* 通知を代替する。 */ },
      async loadTasks() { /* 一覧取得を代替する。 */ },
      async loadDashboard() { /* ダッシュボード取得を代替する。 */ },
      async executeTaskAction(identifier, action) {
        // 保存対象と開始対象が一致することを確認する。
        operations.push(`${identifier}:${action}`);
      }
    });
    virtualMachine.runInContext(source.slice(source.indexOf("async function saveTaskFromDialog("), source.indexOf("/** プロジェクト管理ダイアログと関連操作を初期化する。 */")), context);
    const event = { currentTarget: form, submitter: { id: "start-task-dialog" }, preventDefault() { /* 送信を代替する。 */ } };
    await Promise.all([context.saveTaskFromDialog(event), context.saveTaskFromDialog(event)]);
    assert.deepEqual(operations, failure ? ["save"] : ["save", "close", "edited:start"]);
    assert.equal(form.dataset.saving, "false");
  }
});

function createScenario(activeEntry, selection, changedEntry = activeEntry, failure = false) {
  // APIと選択画面を差し替え、実際の開始処理の呼出順を記録する。
  const requests = [];
  const notices = [];
  let reads = 0;
  const context = virtualMachine.createContext({
    applicationState: { taskStartPending: false },
    window: {},
    showNotice(message) {
      // エラーと利用者向け案内を保持する。
      notices.push(message);
    },
    async apiRequest(path, options) {
      // タイマーの競合と終了API失敗を再現する。
      if (!options) return reads++ === 0 ? activeEntry : changedEntry;
      requests.push(path);
      if (failure) throw new Error("終了失敗");
    },
    async loadDashboard() { /* ダッシュボード更新を代替する。 */ },
    async loadTasks() { /* タスク一覧更新を代替する。 */ }
  });
  virtualMachine.runInContext(functions, context);
  context.chooseTaskSwitchAction = async function chooseSelection() {
    // 指定された利用者の選択を返す。
    return selection;
  };
  return { context, requests, notices };
}

test("タイマーなしは直接開始し、同じタスクは再開始しない", async () => {
  // 開始前の状態に応じた更新回数を確認する。
  const idle = createScenario(null);
  await idle.context.executeTaskAction("next", "start");
  assert.deepEqual(idle.requests, ["/api/v1/tasks/next/actions/start"]);
  const same = createScenario({ identifier: "timer", taskIdentifier: "next" });
  await same.context.executeTaskAction("next", "start");
  assert.deepEqual(same.requests, []);
});

test("完了・中断・停止後に次のタスクを開始する", async () => {
  // 各終了操作の対象と順序を確認する。
  for (const selection of ["complete", "interrupt", "stop"]) {
    const taskIdentifier = selection === "stop" ? null : "previous";
    const scenario = createScenario({ identifier: "timer", taskIdentifier }, selection);
    await scenario.context.executeTaskAction("next", "start");
    assert.deepEqual(scenario.requests, [
      taskIdentifier ? `/api/v1/tasks/previous/actions/${selection}` : "/api/v1/time-entries/timer/stop",
      "/api/v1/tasks/next/actions/start"
    ]);
  }
});

test("キャンセル・活動変更・終了失敗では次を開始しない", async () => {
  // 更新不要の分岐と失敗時の停止を確認する。
  const active = { identifier: "timer", taskIdentifier: "previous" };
  for (const scenario of [createScenario(active, "cancel"), createScenario(active, "complete", null), createScenario(active, "interrupt", active, true)]) {
    await scenario.context.executeTaskAction("next", "start");
    assert.equal(scenario.requests.some(path => path.endsWith("/start")), false);
    assert.equal(scenario.context.applicationState.taskStartPending, false);
  }
});

test("確認中の二重クリックは一度だけ開始する", async () => {
  // 同時に呼び出しても開始処理が重複しないことを確認する。
  const scenario = createScenario(null);
  await Promise.all([scenario.context.executeTaskAction("next", "start"), scenario.context.executeTaskAction("next", "start")]);
  assert.equal(scenario.requests.length, 1);
});
