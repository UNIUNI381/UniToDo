const assert = require("node:assert/strict");
const { test } = require("node:test");
const filesystem = require("node:fs");
const virtualMachine = require("node:vm");
const source = filesystem.readFileSync(require("node:path").join(__dirname, "../src/TaskManager.App/wwwroot/app.js"), "utf8");

/** カレンダー要約の描画処理を読み込んだ検証環境を作成する。 */
function createContext() {
  // 描画結果を保持する要素とHTMLエスケープ処理を用意する。
  const summary = { innerHTML: "" };
  const context = virtualMachine.createContext({
    document: { getElementById() { return summary; } },
    escapeHtml(value) { return String(value).replaceAll("&", "&amp;").replaceAll("<", "&lt;").replaceAll(">", "&gt;"); }
  });
  const start = source.indexOf("function renderCalendarSummary(");
  const end = source.indexOf("/** 同期成否に応じて", start);
  virtualMachine.runInContext(source.slice(start, end), context);
  return { context, summary };
}

test("空き時間を時間と分で表示し、予定がない場合は説明を表示しない", () => {
  // 予定なしの要約に空き時間だけが残ることを確認する。
  const { context, summary } = createContext();
  context.renderCalendarSummary({ availableMinutes: 125, nextEventTitle: "", nextEventLocation: "", reason: "活動終了時刻まで予定はありません。" });
  assert.match(summary.innerHTML, /今の空き 2時間 5分/);
  assert.doesNotMatch(summary.innerHTML, /活動終了時刻まで予定はありません/);
  assert.doesNotMatch(summary.innerHTML, /calendar-next-event/);
});

test("次の予定がある場合は予定名と場所を表示する", () => {
  // 次の予定が存在する場合だけ右側の要約を描画する。
  const { context, summary } = createContext();
  context.renderCalendarSummary({ availableMinutes: 30, nextEventTitle: "会議", nextEventLocation: "会議室" });
  assert.match(summary.innerHTML, /今の空き 0時間 30分/);
  assert.match(summary.innerHTML, /次の予定：会議（会議室）/);
});
