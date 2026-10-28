const assert = require("node:assert/strict");
const { test } = require("node:test");
const filesystem = require("node:fs");
const virtualMachine = require("node:vm");
const source = filesystem.readFileSync(require("node:path").join(__dirname, "../src/TaskManager.App/wwwroot/app.js"), "utf8");

/** 作業ログの実処理をフォームと保存先の代替環境へ読み込む。 */
function createTimeEntryScenario(activeEntry = false) {
  // hidden入力はvalueの変更が既定値にも反映されるため、resetでは消えない。
  const fields = Object.fromEntries(["identifier", "title", "taskIdentifier", "projectIdentifier", "startAt", "endAt"]
    .map(function createField(name) {
      // 入力名ごとに値と無効状態を保持する。
      return [name, { value: "", disabled: false }];
    }));
  const form = {
    dataset: {},
    elements: { namedItem(name) {
      // 指定名の入力項目を返す。
      return fields[name];
    } },
    reset() {
      // ブラウザーと同様にhidden以外の入力を初期化する。
      for (const [name, field] of Object.entries(fields)) {
        if (name !== "identifier") field.value = "";
      }
    },
    querySelector() {
      // 保存ボタンの表示先を返す。
      return {};
    }
  };
  const originalEntry = {
    identifier: "existing-entry", title: "元の記録", taskIdentifier: "task", projectIdentifier: "project",
    startAt: "2026-10-27T09:00:00+09:00", endAt: activeEntry ? null : "2026-10-27T10:00:00+09:00"
  };
  const records = new Map([[originalEntry.identifier, { ...originalEntry }]]);
  const requests = [];
  const context = virtualMachine.createContext({
    applicationState: { timeEntries: [originalEntry] },
    document: { getElementById(identifier) {
      // フォーム以外の表示操作は保存結果へ影響させない。
      return identifier === "time-entry-form" ? form : {
        classList: { toggle() { /* 表示切替を代替する。 */ } },
        showModal() { /* ダイアログ表示を代替する。 */ },
        close() { /* ダイアログ終了を代替する。 */ }
      };
    } },
    toLocalInput(value) { /* 日時変換を代替する。 */ return value || ""; },
    fromLocalInput(value) { /* 日時変換を代替する。 */ return value; },
    showNotice(message, failure) {
      // 保存失敗をテストの失敗として扱う。
      assert.ok(!failure, message);
    },
    async loadDashboard() { /* 保存後の再取得を代替する。 */ },
    async loadTimeReport() { /* 保存後の再取得を代替する。 */ },
    async apiRequest(address, options) {
      // 実際の送信先と方式に従い、別レコードの追加または既存値の更新を再現する。
      requests.push({ address, method: options.method });
      const identifier = options.method === "POST" ? `added-entry-${records.size}` : address.split("/")[4];
      const record = { ...records.get(identifier), ...JSON.parse(options.body), identifier };
      records.set(identifier, record);
      context.applicationState.timeEntries = [...records.values()];
      return record;
    }
  });
  virtualMachine.runInContext(source.slice(source.indexOf("function openTimeEntryDialog("), source.indexOf("/** 編集中の作業ログを集計対象外")), context);
  const event = { currentTarget: form, preventDefault() { /* 標準送信を抑止する。 */ } };
  return { context, fields, records, requests, originalEntry, event };
}

test("編集の保存・キャンセル後も連続追加は別の記録として保存する", async () => {
  // 編集で残ったIDが、次の新規登録やその後の連続登録へ引き継がれないことを検証する。
  for (const saveEditedEntry of [false, true]) {
    const scenario = createTimeEntryScenario();
    scenario.context.openTimeEntryDialog(scenario.originalEntry.identifier);
    if (saveEditedEntry) {
      scenario.fields.title.value = "編集済みの記録";
      await scenario.context.saveTimeEntry(scenario.event);
      assert.equal(scenario.requests.at(-1).method, "PUT");
    } else {
      scenario.context.closeTimeEntryDialog();
    }
    const preservedEntry = { ...scenario.records.get(scenario.originalEntry.identifier) };
    for (const title of ["追加した記録", "続けて追加した記録"]) {
      scenario.context.openTimeEntryDialog();
      assert.equal(scenario.fields.identifier.value, "");
      scenario.fields.title.value = title;
      await scenario.context.saveTimeEntry(scenario.event);
      assert.deepEqual(scenario.requests.at(-1), { address: "/api/v1/time-entries", method: "POST" });
    }
    assert.equal(scenario.records.size, 3);
    assert.deepEqual(scenario.records.get(scenario.originalEntry.identifier), preservedEntry);
    assert.deepEqual([...scenario.records.values()].slice(1).map(record => record.title), ["追加した記録", "続けて追加した記録"]);
  }
});

test("実行中ログの開始時刻修正後の追加は通常の新規登録になる", async () => {
  // タイムライン経由の編集と開始時刻専用APIを維持し、次の追加で編集状態を解除する。
  const scenario = createTimeEntryScenario(true);
  scenario.context.openTimeEntryDialog(scenario.originalEntry.identifier, scenario.originalEntry);
  scenario.fields.startAt.value = "2026-10-27T08:00:00+09:00";
  await scenario.context.saveTimeEntry(scenario.event);
  assert.deepEqual(scenario.requests[0], { address: "/api/v1/time-entries/existing-entry/start", method: "PUT" });
  scenario.context.openTimeEntryDialog();
  assert.equal(scenario.fields.identifier.value, "");
  assert.equal(scenario.fields.title.disabled, false);
  assert.equal(scenario.fields.endAt.disabled, false);
  scenario.fields.title.value = "新しい記録";
  await scenario.context.saveTimeEntry(scenario.event);
  assert.deepEqual(scenario.requests.at(-1), { address: "/api/v1/time-entries", method: "POST" });
  assert.equal(scenario.records.size, 2);
  assert.equal(scenario.records.get(scenario.originalEntry.identifier).startAt, "2026-10-27T08:00:00+09:00");
});

test("私用選択でチェックを有効にし、集計とログに同じ条件を送る", async () => {
  // 初期状態から私用選択・手動解除・仕事選択までの実際の取得処理を検証する。
  const selector = { value: "" };
  const checkbox = { checked: false };
  const requests = [];
  const context = virtualMachine.createContext({
    URLSearchParams,
    taskCategoryFilterPrefix: "category:",
    applicationState: { timeReportPeriod: "day", timeReportAnchor: new Date() },
    document: { getElementById(identifier) {
      // 条件入力を取得する。
      return identifier === "time-project-filter" ? selector : checkbox;
    } },
    clampTimeReportAnchorToToday() { /* 基準日の補正を代替する。 */ },
    formatLocalDate() { /* 固定の基準日を返す。 */ return "2026-07-21"; },
    renderTimeReport() { /* 描画を代替する。 */ },
    async apiRequest(address) {
      // 両APIの検索条件を収集する。
      requests.push(new URL(address, "http://localhost").searchParams);
      return { rangeStart: "2026-07-21", rangeEnd: "2026-07-22" };
    }
  });
  virtualMachine.runInContext(source.slice(source.indexOf("function handleTimeProjectFilterChange("), source.indexOf("/** 取得した作業時間レポート")), context);
  await context.loadTimeReport();
  assert.equal(requests[0].get("includePrivate"), "false");
  selector.value = "category:私用";
  await context.handleTimeProjectFilterChange();
  assert.equal(checkbox.checked, true);
  for (const parameters of requests.slice(-2)) {
    assert.equal(parameters.get("category"), "私用");
    assert.equal(parameters.get("includePrivate"), "true");
    assert.equal(parameters.has("projectIdentifier"), false);
  }
  checkbox.checked = false;
  await context.loadTimeReport();
  assert.equal(requests.at(-1).get("includePrivate"), "false");
  selector.value = "category:仕事";
  await context.handleTimeProjectFilterChange();
  assert.equal(checkbox.checked, false);
  assert.equal(requests.at(-1).get("category"), "仕事");
  selector.value = "__unassigned__";
  await context.handleTimeProjectFilterChange();
  assert.equal(requests.at(-1).get("projectIdentifier"), "__unassigned__");
  assert.equal(requests.at(-1).has("category"), false);
});
