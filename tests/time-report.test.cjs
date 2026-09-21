const assert = require("node:assert/strict");
const { test } = require("node:test");
const filesystem = require("node:fs");
const virtualMachine = require("node:vm");
const source = filesystem.readFileSync(require("node:path").join(__dirname, "../src/TaskManager.App/wwwroot/app.js"), "utf8");

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
