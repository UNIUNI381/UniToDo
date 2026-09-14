"use strict";

// 画面全体で共有する現在データを保持する。
const applicationState = {
  // サーバーで判定したリモート接続状態を保持する。
  remoteAccess: false,
  tasks: [],
  // プロジェクト画面とタスク選択肢で共有する集約を保持する。
  projects: [],
  // 履歴画面へ表示する取得済みレコードを保持する。
  history: [],
  settings: null,
  activeView: "dashboard",
  // 経過時間表示を定期更新するタイマーIDを保持する。
  elapsedTimeTimerIdentifier: null,
  // 依存関係図ごとに一意なDOM識別子を発行する連番を保持する。
  dependencyGraphSequence: 0,
  // 依存線の再描画をまとめるアニメーションフレームIDを保持する。
  dependencyGraphDrawingFrameIdentifier: null,
  // タスク画面へ移動した際に系列の未完了段へスクロールし直すかを保持する。
  dependencyGraphScrollShouldReset: true,
  // 7日表示と埋め込みの展開状態を保持する。
  calendarRangeExpanded: false,
  calendarEmbedExpanded: false,
  // 埋め込み済みURLとタイムラインの横位置を保持する。
  loadedCalendarEmbedUrl: "",
  calendarTimelineScrollPosition: null,
  // ダッシュボードへ戻った際に現在時刻へスクロールし直すかを保持する。
  calendarTimelineScrollShouldReset: false,
  // カレンダーウィジェットの自動更新タイマーIDを保持する。
  calendarRefreshTimerIdentifier: null,
  // 長時間警告と自動停止を短い周期で反映するタイマーIDを保持する。
  timeTrackingRefreshTimerIdentifier: null,
  // 変更通知の接続、遅延更新、画面固有の編集状態を保持する。
  uiChangeClientIdentifier: createUiChangeClientIdentifier(),
  uiChangeStream: null,
  uiChangeRefreshTimerIdentifier: null,
  uiChangeRefreshPending: false,
  uiChangeRefreshInProgress: false,
  settingsFormDirty: false,
  // 最新ダッシュボード応答をウィジェット操作で再利用する。
  dashboardResult: null,
  // 空き時間外でも推薦候補を一時表示するかを保持する。
  forceRecommendationDisplay: false,
  // タスク開始の確認中・送信中の重複操作を防ぐ。
  taskStartPending: false,
  // 作業時間画面の期間、基準日、集計、ログを保持する。
  timeReportPeriod: "day",
  timeReportAnchor: new Date(),
  timeReport: null,
  timeEntries: []
};

// 現行システムと同じ状態候補を定義する。
const taskStatuses = ["受信箱", "下書き", "要確認", "実行可能", "実行中", "待機中", "完了", "中止"];

// プロジェクト未設定だけを絞り込む選択値を保持する。
const unassignedProjectFilterValue = "__unassigned_project__";

// 作業時間APIでプロジェクト未割当だけを絞り込む選択値を保持する。
const unassignedTimeProjectFilterValue = "__unassigned__";

// タスク区分だけを絞り込む選択値の接頭辞を保持する。
const taskCategoryFilterPrefix = "category:";

// アイコン再読込後に復元する画面名の一時保存キーを保持する。
const reloadViewStorageKey = "task-manager-reload-view";

// 横スクロールバーを非操作時に隠すタイマーを要素単位で保持する。
const horizontalScrollbarHideTimers = new WeakMap();

/** ローカル画面を初期化する。 */
async function initializeApplication() {
  // ナビゲーションと主要操作を接続して初期データを取得する。
  initializeNavigation();
  initializeMobileNavigation(() => applicationState.activeView, viewName => {
    // スワイプとクリックで同じ画面切替・エラー表示を使用する。
    switchView(viewName).catch(error => showNotice(error.message, true));
  });
  initializeNavigationScrollbar();
  initializeTaskDialog();
  initializeCustomFilters();
  initializeProjectDialogs();
  initializeTimeTrackingActions();
  initializeSettingsActions();
  document.getElementById("reload-application").addEventListener("click", reloadCurrentView);
  for (const codexTaskButton of document.querySelectorAll("[data-open-codex-task-thread]")) {
    codexTaskButton.addEventListener("click", openCodexTaskThread);
  }
  document.getElementById("refresh-dashboard").addEventListener("click", loadDashboard);
  document.getElementById("sync-calendar-dashboard").addEventListener("click", synchronizeCalendar);
  document.getElementById("toggle-calendar-range").addEventListener("click", toggleCalendarRange);
  document.getElementById("toggle-calendar-embed").addEventListener("click", toggleCalendarEmbed);
  document.getElementById("dashboard-recommendation-filter").addEventListener("change", loadDashboard);
  document.getElementById("task-search").addEventListener("input", renderTaskTable);
  document.getElementById("task-status-filter").addEventListener("change", renderTaskTable);
  document.getElementById("task-project-filter").addEventListener("change", renderTaskTable);
  document.getElementById("task-sort-order").addEventListener("change", renderTaskTable);
  document.getElementById("show-completed-tasks").addEventListener("change", renderTaskTable);
  document.getElementById("project-search").addEventListener("input", renderProjectList);
  document.getElementById("show-archived-projects").addEventListener("change", renderProjectList);
  document.getElementById("show-calendar-history").addEventListener("change", renderHistory);
  document.getElementById("show-time-history").addEventListener("change", renderHistory);
  window.addEventListener("resize", scheduleDependencyGraphDrawing);
  document.addEventListener("toggle", handleDependencyPanelToggle, true);
  document.addEventListener("visibilitychange", refreshApplicationWhenVisible);
  document.addEventListener("close", applyPendingUiChangeAfterEditing, true);
  document.getElementById("settings-form").addEventListener("input", markSettingsFormDirty);
  populateStatusSelectors();
  if (applicationState.elapsedTimeTimerIdentifier === null) {
    applicationState.elapsedTimeTimerIdentifier = window.setInterval(function updateTimedDisplays() {
      // 経過時間とカレンダーの現在時刻線を同じ周期で更新する。
      updateElapsedTimeDisplays();
      updateActiveTimeEntryDisplays();
      updateCalendarCurrentTimeMarker();
    }, 15000);
  }
  if (applicationState.calendarRefreshTimerIdentifier === null) {
    applicationState.calendarRefreshTimerIdentifier = window.setInterval(refreshVisibleDashboard, 300000);
  }
  if (applicationState.timeTrackingRefreshTimerIdentifier === null) {
    applicationState.timeTrackingRefreshTimerIdentifier = window.setInterval(refreshVisibleTimeTracker, 30000);
  }
  try {
    // 認証済み接続経路を読み、PC固有操作を初期表示から除外する。
    const access = await apiRequest("/api/v1/access");
    applicationState.remoteAccess = access.remote;
    for (const localControl of document.querySelectorAll('.nav-button[data-view="settings"]')) {
      localControl.classList.toggle("hidden", applicationState.remoteAccess);
    }
    // 作業ログを初回から保存色で描画できるようプロジェクトを先に読み込む。
    await loadProjects();
    await Promise.all([loadDashboard(), loadSettings()]);
    await loadTasks();
    document.getElementById("service-status").classList.add("online");
    await restoreReloadedView();
    initializeUiChangeMonitoring();
  } catch (error) {
    showNotice(error.message, true);
  }
}

/** スマホ上部メニューの横スクロールバーを操作時だけ表示する。 */
function initializeNavigationScrollbar() {
  // 既存の横スクロール処理を再利用し、タブのタップ操作と指による横移動を両立させる。
  const navigation = document.getElementById("navigation");
  attachHorizontalDragScrolling(navigation);
}

/** 変更通知で画面自身を識別する一意な値を作成する。 */
function createUiChangeClientIdentifier() {
  // 対応ブラウザでは暗号学的UUIDを使い、古い環境だけ時刻と乱数へフォールバックする。
  if (window.crypto?.randomUUID) return window.crypto.randomUUID();
  return `${Date.now()}-${Math.random().toString(36).slice(2)}`;
}

/** サーバーの変更通知ストリームを購読する。 */
function initializeUiChangeMonitoring() {
  // EventSourceの自動再接続を利用し、接続回復時にも表示中画面を整合させる。
  if (applicationState.uiChangeStream) return;
  const changeStream = new EventSource("/api/v1/changes");
  applicationState.uiChangeStream = changeStream;
  changeStream.addEventListener("ready", scheduleUiChangeRefresh);
  changeStream.addEventListener("change", handleUiChangeNotification);
  window.addEventListener("beforeunload", function closeUiChangeStream() {
    // ページ終了時は再接続を試みず現在のストリームを閉じる。
    changeStream.close();
  }, { once: true });
}

/** 受信した変更通知が別クライアント由来なら画面更新を予約する。 */
function handleUiChangeNotification(event) {
  // 同じ画面の保存処理は既存の直後更新へ任せ、二重描画を防ぐ。
  try {
    const notification = JSON.parse(event.data);
    if (notification.clientIdentifier === applicationState.uiChangeClientIdentifier) return;
  } catch {
    // 不完全な通知でも安全側として表示データを再取得する。
  }
  scheduleUiChangeRefresh();
}

/** 複数の変更通知を短時間にまとめて表示中画面の更新を予約する。 */
function scheduleUiChangeRefresh() {
  // タスク状態遷移で複数レコードが変わっても最後に1回だけ再取得する。
  applicationState.uiChangeRefreshPending = true;
  if (applicationState.uiChangeRefreshTimerIdentifier !== null) {
    window.clearTimeout(applicationState.uiChangeRefreshTimerIdentifier);
  }
  applicationState.uiChangeRefreshTimerIdentifier = window.setTimeout(applyPendingUiChange, 300);
}

/** 編集を妨げない時点で予約済みの外部変更を表示へ反映する。 */
async function applyPendingUiChange() {
  // 非表示、ダイアログ編集中、設定未保存、既存更新中は次の安全な契機まで保留する。
  applicationState.uiChangeRefreshTimerIdentifier = null;
  if (!applicationState.uiChangeRefreshPending
    || document.visibilityState !== "visible"
    || document.querySelector("dialog[open]")
    || (applicationState.activeView === "settings" && applicationState.settingsFormDirty)
    || applicationState.uiChangeRefreshInProgress) {
    return;
  }
  applicationState.uiChangeRefreshPending = false;
  applicationState.uiChangeRefreshInProgress = true;
  try {
    await refreshActiveView();
  } catch (error) {
    showNotice(error.message, true);
  } finally {
    applicationState.uiChangeRefreshInProgress = false;
    if (applicationState.uiChangeRefreshPending) scheduleUiChangeRefresh();
  }
}

/** 現在選択中の画面を変えずに必要なデータだけ再取得する。 */
async function refreshActiveView() {
  // 画面ごとの共有データ依存を満たす順序で読み込み、タブ選択と入力条件を維持する。
  const viewName = applicationState.activeView;
  if (viewName === "dashboard") await loadDashboard();
  if (viewName === "tasks" || viewName === "drafts") {
    await loadProjects();
    await loadTasks();
  }
  if (viewName === "projects") await loadProjects();
  if (viewName === "time") {
    await loadProjects();
    await loadTasks();
    await loadTimeReport();
  }
  if (viewName === "settings") await loadSettings();
  if (viewName === "history") await loadHistory();
}

/** ダイアログを閉じた後に保留中の外部変更を反映する。 */
function applyPendingUiChangeAfterEditing() {
  // closeイベントのDOM反映後に開いているダイアログを再確認する。
  window.setTimeout(applyPendingUiChange, 0);
}

/** 設定フォームに未保存の利用者入力があることを記録する。 */
function markSettingsFormDirty() {
  // 自動更新で入力途中の設定を上書きしないため保存完了まで保持する。
  applicationState.settingsFormDirty = true;
}

/** 現在表示中の画面名を保存してアプリを再読み込みする。 */
function reloadCurrentView() {
  // 同じブラウザータブの次回読込だけで使う一時値として現在画面を保存する。
  window.sessionStorage.setItem(reloadViewStorageKey, applicationState.activeView);
  window.location.reload();
}

/** アイコンからの再読み込み前に表示していた画面を復元する。 */
async function restoreReloadedView() {
  // 一時値は1回で消費し、通常のブラウザー再読込には影響させない。
  const restoredViewName = window.sessionStorage.getItem(reloadViewStorageKey);
  window.sessionStorage.removeItem(reloadViewStorageKey);
  if (!restoredViewName || !document.getElementById(`view-${restoredViewName}`)) return;
  await switchView(restoredViewName);
}

/** サイドナビゲーションの切替を初期化する。 */
function initializeNavigation() {
  // 表示先に応じて必要データだけを更新する。
  for (const navigationButton of document.querySelectorAll(".nav-button")) {
    navigationButton.addEventListener("click", async function handleNavigationClick() {
      const viewName = navigationButton.dataset.view;
      try { await switchView(viewName); }
      catch (error) { showNotice(error.message, true); }
    });
  }
}

/** 状態、プロジェクト、並び順のカスタム選択メニューを初期化する。 */
function initializeCustomFilters() {
  // 画面内の選択欄へ共通の開閉・選択操作を接続する。
  for (const selectIdentifier of ["dashboard-recommendation-filter", "task-status-filter", "task-project-filter", "task-sort-order", "time-project-filter", "free-activity-project"]) {
    document.getElementById(`${selectIdentifier}-button`).addEventListener("click", toggleCustomFilterMenu);
    document.getElementById(`${selectIdentifier}-menu`).addEventListener("click", selectCustomFilterOption);
  }
  renderCustomFilterOptions(
    "task-sort-order",
    [
      { value: "created", label: "登録順", color: null },
      { value: "priority", label: "優先順", color: null }
    ],
    false);
  document.addEventListener("click", closeCustomFiltersWhenOutside);
  document.addEventListener("keydown", closeCustomFiltersWithEscape);
}

/** 押されたカスタム絞り込みメニューの開閉状態を切り替える。 */
function toggleCustomFilterMenu(event) {
  // 他の候補を閉じてから対象メニューだけを切り替える。
  event.stopPropagation();
  const selectIdentifier = event.currentTarget.dataset.filterSelect;
  const menu = document.getElementById(`${selectIdentifier}-menu`);
  const opensMenu = menu.classList.contains("hidden");
  closeCustomFilterMenus();
  menu.classList.toggle("hidden", !opensMenu);
  event.currentTarget.setAttribute("aria-expanded", String(opensMenu));
}

/** 選択された候補を対応する絞り込み値へ反映する。 */
function selectCustomFilterOption(event) {
  // ドットや文字を押した場合も対応する候補ボタンから値を取得する。
  const optionButton = event.target.closest("[data-filter-value]");
  if (!optionButton) return;
  const selectIdentifier = event.currentTarget.dataset.filterSelect;
  const filterSelect = document.getElementById(selectIdentifier);
  filterSelect.value = optionButton.dataset.filterValue;
  updateCustomFilterDisplay(selectIdentifier);
  closeCustomFilterMenus();
  filterSelect.dispatchEvent(new Event("change", { bubbles: true }));
}

/** 絞り込みメニュー外のクリックで開いている候補を閉じる。 */
function closeCustomFiltersWhenOutside(event) {
  // いずれかのカスタム選択欄内にいる場合は現在表示を維持する。
  if (!event.target.closest(".project-filter-control")) closeCustomFilterMenus();
}

/** Escapeキーで開いている絞り込み候補を閉じる。 */
function closeCustomFiltersWithEscape(event) {
  // キーボード操作でもすべての候補を閉じられるようにする。
  if (event.key === "Escape") closeCustomFilterMenus();
}

/** すべてのカスタム絞り込みメニューを閉じる。 */
function closeCustomFilterMenus() {
  // 表示状態とアクセシビリティ属性を全選択欄で同期する。
  for (const menu of document.querySelectorAll(".project-filter-menu")) menu.classList.add("hidden");
  for (const button of document.querySelectorAll(".project-filter-button")) button.setAttribute("aria-expanded", "false");
}

/** 指定画面へ切り替えて最新データを読み込む。 */
async function switchView(viewName) {
  // 再読込で保存された設定タブもリモート接続時は開かない。
  if (applicationState.remoteAccess && viewName === "settings") return;
  // 表示状態とナビゲーション選択状態を同期する。
  const previousViewName = applicationState.activeView;
  applicationState.activeView = viewName;
  if (viewName === "dashboard" && previousViewName !== "dashboard") {
    // 左ナビゲーションから戻る場合は以前の横位置ではなく現在時刻を表示する。
    applicationState.calendarTimelineScrollShouldReset = true;
  }
  if (viewName === "tasks" && previousViewName !== "tasks") {
    // タスク画面へ戻るたび完了済み段を左側へ隠す初期位置を再適用する。
    applicationState.dependencyGraphScrollShouldReset = true;
  }
  for (const view of document.querySelectorAll(".view")) {
    view.classList.toggle("active", view.id === `view-${viewName}`);
  }
  for (const navigationButton of document.querySelectorAll(".nav-button")) {
    navigationButton.classList.toggle("active", navigationButton.dataset.view === viewName);
  }
  // 先に選択先の保持済みDOMを描画し、データ取得を待たず切替を見せる。
  await new Promise(resolve => window.requestAnimationFrame(() => window.setTimeout(resolve, 0)));
  if (applicationState.activeView !== viewName) return;
  if (viewName === "dashboard") await loadDashboard();
  if (viewName === "tasks" || viewName === "drafts") await loadTasks();
  if (viewName === "projects") await loadProjects();
  if (viewName === "time") await loadTimeReport();
  if (viewName === "settings") await loadSettings();
  if (viewName === "history") await loadHistory();
  if (applicationState.uiChangeRefreshPending) {
    window.setTimeout(applyPendingUiChange, 0);
  }
}

/** APIへJSONリクエストを送信する。 */
async function apiRequest(path, options = {}) {
  // 更新系へローカル保護ヘッダーを付与し、エラー本文を共通表示する。
  const requestOptions = { ...options };
  requestOptions.headers = { ...(options.headers || {}) };
  if (options.body && !(options.body instanceof FormData)) {
    requestOptions.headers["Content-Type"] = "application/json";
  }
  if (options.method && options.method !== "GET") {
    requestOptions.headers["X-TaskManager-Request"] = "local";
    requestOptions.headers["X-TaskManager-Source"] = "screen";
    requestOptions.headers["X-TaskManager-Client"] = applicationState.uiChangeClientIdentifier;
  }
  const response = await fetch(path, requestOptions);
  const responseText = await response.text();
  const responseBody = responseText ? JSON.parse(responseText) : null;
  if (!response.ok) {
    const requestError = new Error(responseBody?.error || `処理に失敗しました（${response.status}）`);
    requestError.status = response.status;
    requestError.details = responseBody;
    throw requestError;
  }
  return responseBody;
}

/** PCとスマホで共用するCodex会話パネルを開く。 */
async function openCodexTaskThread() {
  // 外部アプリへの移動をなくし、同じ会話をこの画面に表示する。
  await window.unitodoAssistant.open();
}

/** 現在の推薦を読み込んでダッシュボードへ表示する。 */
async function loadDashboard() {
  // 推薦、確認待ち、カレンダーウィジェット、システム異常終了を同じ画面へ反映する。
  const result = await apiRequest(`/api/v1/dashboard${buildDashboardRecommendationQuery()}`);
  applicationState.dashboardResult = result;
  renderRecommendation(result);
  renderFollowUp(result.followUpTask);
  renderTimeTracker(result.activeTimeEntry);
  renderTimeReviewCards(result.reviewTimeEntries || []);
  renderCalendarWidget(result);
  await loadPendingSystemIncident();
}

/** 未確認のシステム異常終了を読み込んでダッシュボードへ表示する。 */
async function loadPendingSystemIncident() {
  // 障害情報APIだけの一時失敗で通常の推薦表示を妨げない。
  try {
    const incident = await apiRequest("/api/v1/system-incidents/pending");
    renderSystemIncident(incident);
  } catch (error) {
    showNotice(`システムエラー情報を取得できません：${error.message}`, true);
  }
}

/** システム異常終了を確認操作まで残る警告カードとして描画する。 */
function renderSystemIncident(incident) {
  // 自動復旧日時、終了コード、ログ場所とCodex共有操作をまとめて表示する。
  const card = document.getElementById("system-incident-card");
  if (!incident) {
    card.classList.add("hidden");
    card.innerHTML = "";
    return;
  }
  const recoveredText = incident.recoveredAt ? formatDateTime(incident.recoveredAt) : "復旧処理中";
  const logPath = incident.crashLogPath || incident.recoveryLogPath || "ログパスなし";
  card.classList.remove("hidden");
  card.innerHTML = `
    <div class="system-incident-content">
      <span class="system-incident-label">SYSTEM ERROR</span>
      <strong>UniToDoが異常終了し、自動復旧しました</strong>
      <p>${formatDateTime(incident.occurredAt)}に異常終了を検出し、${recoveredText}に復旧しました。終了コード：${escapeHtml(String(incident.exitCode))}</p>
      <small>ログ：${escapeHtml(logPath)}</small>
    </div>
    <div class="system-incident-actions">
      <button type="button" class="secondary-button" data-incident-copy>情報をコピー</button>
      <button type="button" class="primary-button" data-incident-codex>コピーしてCodexを開く</button>
      <button type="button" class="text-button" data-incident-acknowledge>確認済みにする</button>
    </div>`;
  card.querySelector("[data-incident-copy]").addEventListener("click", async function handleIncidentCopy() {
    // Codexへ貼り付けられる定型情報をクリップボードへ保存する。
    await copySystemIncidentSummary(incident);
  });
  card.querySelector("[data-incident-codex]").addEventListener("click", async function handleIncidentCodex() {
    // エラー情報をコピーしてから設定済みの開発スレッドを開く。
    if (await copySystemIncidentSummary(incident, false)) {
      await openCodexTaskThread();
      showNotice("エラー情報をコピーしてCodexを開きました。貼り付けて修正を依頼してください。");
    }
  });
  card.querySelector("[data-incident-acknowledge]").addEventListener("click", async function handleIncidentAcknowledge() {
    // ユーザーが内容を確認した場合だけ永続警告を閉じる。
    try {
      await apiRequest(`/api/v1/system-incidents/${encodeURIComponent(incident.identifier)}/acknowledge`, {
        method: "POST",
        body: "{}"
      });
      renderSystemIncident(null);
      showNotice("システムエラー情報を確認済みにしました。");
    } catch (error) {
      showNotice(error.message, true);
    }
  });
}

/** システム異常終了のCodex共有用テキストを生成する。 */
function buildSystemIncidentSummary(incident) {
  // 識別子、時刻、終了コード、ログ場所を再調査に必要な最小情報として整形する。
  return [
    "UniToDoの異常終了を修正してください。",
    `インシデントID: ${incident.identifier}`,
    `発生日時: ${incident.occurredAt}`,
    `復旧日時: ${incident.recoveredAt || "未復旧"}`,
    `終了コード: ${incident.exitCode}`,
    `再起動回数: ${incident.restartCount}`,
    `クラッシュログ: ${incident.crashLogPath || "なし"}`,
    `復旧ログ: ${incident.recoveryLogPath || "なし"}`
  ].join("\n");
}

/** システム異常終了の共有用テキストをクリップボードへコピーする。 */
async function copySystemIncidentSummary(incident, showSuccessNotice = true) {
  // ローカルクリップボードへ保存し、失敗時は画面上で理由を知らせる。
  const summaryText = buildSystemIncidentSummary(incident);
  try {
    if (navigator.clipboard?.writeText) {
      await navigator.clipboard.writeText(summaryText);
    } else {
      copyTextWithTemporaryElement(summaryText);
    }
    if (showSuccessNotice) showNotice("エラー情報をコピーしました。");
    return true;
  } catch (clipboardError) {
    // Clipboard APIが制限される内蔵ブラウザでは一時入力によるコピーへ切り替える。
    try {
      copyTextWithTemporaryElement(summaryText);
      if (showSuccessNotice) showNotice("エラー情報をコピーしました。");
      return true;
    } catch (fallbackError) {
      showNotice(`エラー情報をコピーできません：${fallbackError.message || clipboardError.message}`, true);
      return false;
    }
  }
}

/** 一時入力要素を利用してテキストをクリップボードへコピーする。 */
function copyTextWithTemporaryElement(text) {
  // Clipboard APIを利用できないWebView向けに選択中テキストの標準コピー命令を使う。
  const temporaryInput = document.createElement("textarea");
  temporaryInput.value = text;
  temporaryInput.setAttribute("readonly", "");
  temporaryInput.style.position = "fixed";
  temporaryInput.style.left = "-9999px";
  document.body.appendChild(temporaryInput);
  temporaryInput.select();
  const copySucceeded = document.execCommand("copy");
  temporaryInput.remove();
  if (!copySucceeded) throw new Error("ブラウザがコピー操作を拒否しました。");
}

/** ダッシュボードの選択範囲を推薦APIのクエリへ変換する。 */
function buildDashboardRecommendationQuery() {
  // 区分、プロジェクト、強制表示状態を排他的なクエリとして組み立てる。
  const selectedValue = document.getElementById("dashboard-recommendation-filter").value;
  const queryParameters = new URLSearchParams();
  if (selectedValue.startsWith("category:")) {
    queryParameters.set("category", selectedValue.slice("category:".length));
  } else if (selectedValue.startsWith("project:")) {
    queryParameters.set("projectIdentifier", selectedValue.slice("project:".length));
  }
  if (applicationState.forceRecommendationDisplay) {
    queryParameters.set("forceRecommendation", "true");
  }
  const queryText = queryParameters.toString();
  return queryText ? `?${queryText}` : "";
}

/** 表示中のダッシュボードへタイマー警告と自動停止を短い周期で反映する。 */
async function refreshVisibleTimeTracker() {
  // タイマー状態だけを軽量取得し、終了を検出した場合だけダッシュボード全体を更新する。
  if (applicationState.activeView !== "dashboard") return;
  try {
    const activeTimeEntry = await apiRequest("/api/v1/time-entries/active");
    const previousActiveEntry = applicationState.dashboardResult?.activeTimeEntry;
    if (!activeTimeEntry && previousActiveEntry) {
      await loadDashboard();
      return;
    }
    if (!activeTimeEntry) return;
    if (!activeTimeEntry.warningAt
      && activeTimeEntry.nextWarningAt
      && Date.parse(activeTimeEntry.nextWarningAt) <= Date.now()) {
      // 定期処理直前でも警告を見せ、延長API側で期限到達を再検証する。
      activeTimeEntry.warningAt = activeTimeEntry.nextWarningAt;
    }
    if (applicationState.dashboardResult) {
      applicationState.dashboardResult.activeTimeEntry = activeTimeEntry;
    }
    renderTimeTracker(activeTimeEntry);
  } catch {
    // 5分周期の通常更新に任せ、短周期取得の一時失敗は通知を増やさない。
  }
}

/** カレンダーウィジェットへ空き時間、予定、同期状態を描画する。 */
function renderCalendarWidget(result) {
  // 表示前のスクロール位置を保持して再読込後も同じ時間帯を見せる。
  const previousScrollContainer = document.querySelector(".calendar-timeline-scroll");
  if (previousScrollContainer && !applicationState.calendarTimelineScrollShouldReset) {
    applicationState.calendarTimelineScrollPosition = previousScrollContainer.scrollLeft;
  }
  renderCalendarSummary(result.currentSlot);
  renderCalendarSyncState(result);
  renderCalendarTimeline(result.calendarWidget);
  renderCalendarEmbed(result.calendarWidget);
}

/** 現在の空き時間と次の予定を小さな要約として表示する。 */
function renderCalendarSummary(currentSlot) {
  // 独立カードを廃止し、カレンダー見出し直下へ必要情報だけをまとめる。
  const nextEventText = currentSlot.nextEventTitle
    ? `次の予定：${escapeHtml(currentSlot.nextEventTitle)}${currentSlot.nextEventLocation ? `（${escapeHtml(currentSlot.nextEventLocation)}）` : ""}`
    : escapeHtml(currentSlot.reason || "活動終了時刻まで予定はありません。");
  document.getElementById("calendar-summary").innerHTML = `
    <span class="calendar-availability-chip">今の空き ${currentSlot.availableMinutes}分</span>
    <span class="calendar-next-event">${nextEventText}</span>`;
}

/** 同期成否に応じて控えめな状態表示または警告を描画する。 */
function renderCalendarSyncState(result) {
  // 正常時は小さなフッターだけにし、失敗と未接続だけを警告表示する。
  const alertContainer = document.getElementById("calendar-sync-alert");
  const stateContainer = document.getElementById("calendar-sync-state");
  if (result.calendarError) {
    const cacheMessage = result.calendarUpdatedAt
      ? `最終成功：${formatDateTime(result.calendarUpdatedAt)}。保存済みの予定を表示しています。`
      : "利用できる予定キャッシュがありません。";
    alertContainer.classList.remove("hidden");
    alertContainer.innerHTML = `
      <div>
        <strong>Google Calendarを同期できません</strong>
        <p>${escapeHtml(cacheMessage)}</p>
        <details><summary>エラー詳細</summary><p>${escapeHtml(result.calendarError)}</p></details>
      </div>
      <div class="calendar-alert-actions">
        <button type="button" class="secondary-button" data-calendar-command="synchronize">再同期</button>
        <button type="button" class="primary-button" data-calendar-command="connect">Googleへ再接続</button>
      </div>`;
    stateContainer.textContent = "同期エラー";
    stateContainer.className = "calendar-sync-state calendar-sync-state-error";
  } else if (!result.calendarUpdatedAt) {
    alertContainer.classList.remove("hidden");
    alertContainer.innerHTML = `
      <div><strong>Google Calendarは未接続です</strong><p>設定を確認してGoogleへ接続してください。</p></div>
      <div class="calendar-alert-actions">
        <button type="button" class="secondary-button" data-calendar-command="settings">設定を開く</button>
        <button type="button" class="primary-button" data-calendar-command="connect">Googleへ接続</button>
      </div>`;
    stateContainer.textContent = "未同期";
    stateContainer.className = "calendar-sync-state calendar-sync-state-error";
  } else {
    alertContainer.classList.add("hidden");
    alertContainer.innerHTML = "";
    stateContainer.textContent = `● ${formatCalendarSyncTime(result.calendarUpdatedAt)}同期済み`;
    stateContainer.className = "calendar-sync-state";
  }
  for (const commandButton of alertContainer.querySelectorAll("[data-calendar-command]")) {
    commandButton.addEventListener("click", executeCalendarWidgetCommand);
  }
}

/** カレンダー警告内の再同期、再接続、設定遷移を実行する。 */
async function executeCalendarWidgetCommand(event) {
  // ボタンの指示に対応する既存操作を再利用する。
  const commandName = event.currentTarget.dataset.calendarCommand;
  if (commandName === "synchronize") await synchronizeCalendar();
  if (commandName === "connect") await connectCalendar();
  if (commandName === "settings") await switchView("settings");
}

/** 今日または7日分の横時間軸をカレンダー予定から描画する。 */
function renderCalendarTimeline(calendarWidget) {
  // APIが古い場合も画面全体を壊さず空表示へ戻す。
  const timelineContainer = document.getElementById("calendar-timeline");
  if (!calendarWidget || !calendarWidget.rangeStart) {
    timelineContainer.className = "calendar-timeline-loading";
    timelineContainer.textContent = "カレンダー表示データを取得できません。";
    return;
  }
  const displayDayCount = applicationState.calendarRangeExpanded ? 7 : 1;
  const rangeStart = new Date(calendarWidget.rangeStart);
  const calendarEvents = Array.isArray(calendarWidget.events) ? calendarWidget.events : [];
  const taskDeadlines = Array.isArray(calendarWidget.deadlines) ? calendarWidget.deadlines : [];
  const timeEntries = Array.isArray(calendarWidget.timeEntries) ? calendarWidget.timeEntries : [];
  const activityStartMinute = parseClockTimeToMinute(calendarWidget.activityStart, 0);
  const activityEndMinute = parseClockTimeToMinute(calendarWidget.activityEnd, 1440);
  const dayRows = [];
  for (let dayOffset = 0; dayOffset < displayDayCount; dayOffset += 1) {
    const dayStart = new Date(rangeStart);
    dayStart.setDate(rangeStart.getDate() + dayOffset);
    const dayEnd = new Date(dayStart);
    dayEnd.setDate(dayStart.getDate() + 1);
    dayRows.push(renderCalendarDayRow(
      dayStart,
      dayEnd,
      calendarEvents,
      taskDeadlines,
      timeEntries,
      dayOffset === 0,
      activityStartMinute,
      activityEndMinute));
  }
  timelineContainer.className = "calendar-timeline";
  timelineContainer.innerHTML = `
    <div class="calendar-timeline-scroll">
      <div class="calendar-timeline-content">
        ${renderCalendarTimeAxis(activityStartMinute, activityEndMinute)}
        ${dayRows.join("")}
      </div>
    </div>`;
  const scrollContainer = timelineContainer.querySelector(".calendar-timeline-scroll");
  attachHorizontalDragScrolling(scrollContainer);
  for (const deadlineButton of timelineContainer.querySelectorAll("[data-deadline-task]")) {
    deadlineButton.addEventListener("click", function handleDeadlineClick() {
      // 締め切りに対応するタスクを共通の編集画面で開く。
      openTaskDialog(deadlineButton.dataset.deadlineTask);
    });
  }
  for (const timeEntryBlock of timelineContainer.querySelectorAll("[data-time-entry]")) {
    timeEntryBlock.addEventListener("click", function handleTimeEntryBlockClick() {
      // 完了済みは全項目、実行中は開始時刻だけを編集できる共通画面を開く。
      const timeEntry = timeEntries.find(entry => entry.identifier === timeEntryBlock.dataset.timeEntry);
      if (timeEntry) openTimeEntryDialog(timeEntry.identifier, timeEntry);
    });
  }
  scrollContainer.addEventListener("scroll", function rememberCalendarScroll() {
    // 表示切替や自動更新をまたいで同じ時間帯を維持する。
    applicationState.calendarTimelineScrollPosition = scrollContainer.scrollLeft;
  }, { passive: true });
  const resetsScrollPosition = applicationState.calendarTimelineScrollShouldReset;
  const initialScrollPosition = resetsScrollPosition || applicationState.calendarTimelineScrollPosition === null
    ? Math.max(0, calculateCurrentMinuteOfDay() * 64 / 60 - 128)
    : applicationState.calendarTimelineScrollPosition;
  applicationState.calendarTimelineScrollShouldReset = false;
  scrollContainer.dataset.initializingScroll = "true";
  window.requestAnimationFrame(function positionCalendarTimeline() {
    // レイアウト確定後に横スクロールと現在時刻線を合わせる。
    scrollContainer.scrollLeft = initialScrollPosition;
    applicationState.calendarTimelineScrollPosition = scrollContainer.scrollLeft;
    updateCalendarCurrentTimeMarker();
    window.requestAnimationFrame(function finishCalendarScrollInitialization() {
      // 初期移動では操作中スクロールバーを表示しない。
      delete scrollContainer.dataset.initializingScroll;
    });
  });
}

/** 0時から24時までの共通時間軸を描画する。 */
function renderCalendarTimeAxis(activityStartMinute, activityEndMinute) {
  // 1時間64pxの固定縮尺で2時間ごとのラベルを付ける。
  const hourLabels = [];
  for (let hour = 0; hour <= 24; hour += 2) {
    const edgeClass = hour === 0 ? " edge-start" : hour === 24 ? " edge-end" : "";
    hourLabels.push(`<span class="calendar-hour-label${edgeClass}" style="left:${hour * 64}px">${String(hour).padStart(2, "0")}:00</span>`);
  }
  return `
    <div class="calendar-time-axis-row">
      <div class="calendar-date-gutter calendar-axis-corner">日付</div>
      <div class="calendar-time-axis">${renderInactiveTimeMarkup(activityStartMinute, activityEndMinute)}${hourLabels.join("")}</div>
    </div>`;
}

/** 1日分の予定を時間ブロックと重複レーンへ変換する。 */
function renderCalendarDayRow(dayStart, dayEnd, calendarEvents, taskDeadlines, timeEntries, isToday, activityStartMinute, activityEndMinute) {
  // 日境界へ重なる予定と作業ログを別々のレーンへ配置する。
  const matchingEvents = calendarEvents.filter(function filterCalendarEvent(calendarEvent) {
    return new Date(calendarEvent.endAt) > dayStart && new Date(calendarEvent.startAt) < dayEnd;
  });
  // 同時刻かつ同じ期限種別の締切を、丸と線を共有する1グループへまとめる。
  const deadlineGroupsByKey = new Map();
  for (const taskDeadline of taskDeadlines) {
    const deadlineAt = new Date(taskDeadline.deadlineAt);
    if (deadlineAt < dayStart || deadlineAt >= dayEnd) continue;
    const deadlineType = taskDeadline.deadlineType === "厳守" ? "厳守" : "目安";
    const deadlineGroupKey = `${deadlineAt.getTime()}:${deadlineType}`;
    let deadlineGroup = deadlineGroupsByKey.get(deadlineGroupKey);
    if (!deadlineGroup) {
      deadlineGroup = {
        deadlineAt,
        deadlineMinute: Math.max(0, Math.min(1440, (deadlineAt.getTime() - dayStart.getTime()) / 60000)),
        deadlineType,
        deadlines: [],
        laneIndex: 0
      };
      deadlineGroupsByKey.set(deadlineGroupKey, deadlineGroup);
    }
    deadlineGroup.deadlines.push(taskDeadline);
  }
  const matchingDeadlineGroups = Array.from(deadlineGroupsByKey.values())
    .sort(function compareDeadlineGroups(leftGroup, rightGroup) {
      // 同時刻では厳守を目安より先に配置し、必ず上段へ表示する。
      const timeDifference = leftGroup.deadlineMinute - rightGroup.deadlineMinute;
      if (timeDifference !== 0) return timeDifference;
      if (leftGroup.deadlineType === rightGroup.deadlineType) return 0;
      return leftGroup.deadlineType === "厳守" ? -1 : 1;
    });

  // 約180px幅のラベル群が重ならない連続した段へグループ単位で割り当てる。
  const deadlineLaneEndMinutes = [];
  const deadlineLabelDurationMinutes = 170;
  for (const deadlineGroup of matchingDeadlineGroups) {
    let laneIndex = 0;
    while (true) {
      const hasCollision = deadlineGroup.deadlines.some(function checkDeadlineLaneCollision(unusedDeadline, offset) {
        // グループが使用する各段について、先に配置したラベルとの横方向の重なりを調べる。
        const laneEndMinute = deadlineLaneEndMinutes[laneIndex + offset] ?? Number.NEGATIVE_INFINITY;
        return laneEndMinute > deadlineGroup.deadlineMinute;
      });
      if (!hasCollision) break;
      laneIndex += 1;
    }
    for (let offset = 0; offset < deadlineGroup.deadlines.length; offset += 1) {
      deadlineLaneEndMinutes[laneIndex + offset] = deadlineGroup.deadlineMinute + deadlineLabelDurationMinutes;
    }
    deadlineGroup.laneIndex = laneIndex;
  }
  const deadlineBandHeight = matchingDeadlineGroups.length > 0
    ? Math.max(1, deadlineLaneEndMinutes.length) * 30 + 5
    : 0;
  const allDayEvents = matchingEvents.filter(calendarEvent => calendarEvent.isAllDay);
  const timedSegments = matchingEvents
    .filter(calendarEvent => !calendarEvent.isAllDay)
    .map(calendarEvent => createCalendarEventSegment(calendarEvent, dayStart, dayEnd))
    .sort((leftSegment, rightSegment) =>
      leftSegment.startMinute - rightSegment.startMinute
      || leftSegment.endMinute - rightSegment.endMinute);
  const laneEndMinutes = [];
  for (const segment of timedSegments) {
    // 重複していない最初のレーンへ予定を割り当てる。
    let laneIndex = laneEndMinutes.findIndex(endMinute => endMinute <= segment.startMinute);
    if (laneIndex < 0) {
      laneIndex = laneEndMinutes.length;
      laneEndMinutes.push(segment.endMinute);
    } else {
      laneEndMinutes[laneIndex] = segment.endMinute;
    }
    segment.laneIndex = laneIndex;
  }
  const allDayHeight = allDayEvents.length > 0 ? allDayEvents.length * 25 + 7 : 0;
  const laneCount = Math.max(1, laneEndMinutes.length);
  const scheduleBandHeight = allDayHeight + laneCount * 46 + 7;
  const canvasHeight = Math.max(54, scheduleBandHeight + deadlineBandHeight);
  const deadlineMarkup = matchingDeadlineGroups.map(function renderTaskDeadlineGroup(deadlineGroup) {
    // 予定帯の下でグループごとに1つの丸と線を描き、各タスク名を縦に並べる。
    const markerPosition = deadlineGroup.deadlineMinute * 64 / 60;
    const markerTop = scheduleBandHeight + deadlineGroup.laneIndex * 30 + 4;
    const markerHeight = (deadlineGroup.deadlines.length - 1) * 30 + 24;
    const alignsLeft = markerPosition > 1320;
    const deadlineClass = deadlineGroup.deadlineType === "厳守" ? " strict" : " target";
    const alignmentClass = alignsLeft ? " align-left" : "";
    const deadlineLabelsMarkup = deadlineGroup.deadlines.map(function renderTaskDeadlineLabel(taskDeadline) {
      // ラベル本文は名称だけとし、詳細はタスクごとのツールチップへ保持する。
      const projectColor = getProjectColor(taskDeadline.projectIdentifier);
      const tooltipText = buildTaskDeadlineTooltip(taskDeadline);
      return `<button type="button" class="calendar-deadline-label" data-deadline-task="${escapeAttribute(taskDeadline.taskIdentifier)}" style="--project-color:${projectColor}" title="${escapeAttribute(tooltipText)}" aria-label="${escapeAttribute(`${taskDeadline.title}を編集`)}"><span>${escapeHtml(taskDeadline.title)}</span></button>`;
    }).join("");
    return `<div class="calendar-deadline-marker${deadlineClass}${alignmentClass}" style="left:${markerPosition}px;top:${markerTop}px;height:${markerHeight}px">
      <span class="calendar-deadline-labels">${deadlineLabelsMarkup}</span>
    </div>`;
  }).join("");
  const allDayMarkup = allDayEvents.map(function renderAllDayEvent(calendarEvent, eventIndex) {
    // 終日予定は時間幅を持たせず専用帯へ積み上げる。
    const title = buildCalendarEventTooltip(calendarEvent);
    return `<div class="calendar-all-day-event${calendarEvent.isBusy ? "" : " free-event"}" style="top:${eventIndex * 25 + 4}px" title="${escapeAttribute(title)}">終日　${escapeHtml(calendarEvent.title || "予定")}</div>`;
  }).join("");
  const eventMarkup = timedSegments.map(function renderTimedSegment(segment) {
    // 実時間を1時間64pxへ換算し、短時間予定にも選択可能な最小幅を確保する。
    const leftPosition = segment.startMinute * 64 / 60;
    const segmentWidth = Math.max(8, (segment.endMinute - segment.startMinute) * 64 / 60);
    const topPosition = allDayHeight + segment.laneIndex * 46 + 4;
    const classNames = [
      "calendar-event-block",
      segment.event.isBusy ? "" : "free-event",
      segment.continuesBefore ? "continues-before" : "",
      segment.continuesAfter ? "continues-after" : ""
    ].filter(Boolean).join(" ");
    const eventTime = `${formatCalendarTime(segment.visibleStart)}–${formatCalendarTime(segment.visibleEnd)}`;
    const locationMarkup = segmentWidth >= 150 && segment.event.location
      ? `<small>${escapeHtml(segment.event.location)}</small>`
      : "";
    return `<div class="${classNames}" style="left:${leftPosition}px;top:${topPosition}px;width:${segmentWidth}px" title="${escapeAttribute(buildCalendarEventTooltip(segment.event))}">
      <strong>${escapeHtml(segment.event.title || "予定")}</strong><span>${eventTime}</span>${locationMarkup}
    </div>`;
  }).join("");
  const emptyMarkup = matchingEvents.length === 0
    ? `<span class="calendar-day-empty">予定なし</span>`
    : "";
  const todayClass = isToday ? " today" : "";
  const currentTimeMarkup = isToday
    ? `<div class="calendar-current-time group-marker" data-current-time-marker><span></span></div>`
    : "";
  const inactiveTimeMarkup = renderInactiveTimeMarkup(activityStartMinute, activityEndMinute);
  const matchingTimeEntries = isToday ? timeEntries.filter(function filterTimeEntry(timeEntry) {
    const effectiveEnd = timeEntry.endAt ? new Date(timeEntry.endAt) : new Date();
    return effectiveEnd > dayStart && new Date(timeEntry.startAt) < dayEnd && !timeEntry.voidedAt;
  }) : [];
  const timeEntrySegments = matchingTimeEntries
    .map(timeEntry => createTimeEntrySegment(timeEntry, dayStart, dayEnd))
    .sort((leftSegment, rightSegment) =>
      leftSegment.startMinute - rightSegment.startMinute
      || leftSegment.endMinute - rightSegment.endMinute);
  const workLaneEndMinutes = [];
  for (const segment of timeEntrySegments) {
    // 同一時刻に重なる承認済みログは作業ログ内の別レーンへ積み上げる。
    let laneIndex = workLaneEndMinutes.findIndex(endMinute => endMinute <= segment.startMinute);
    if (laneIndex < 0) {
      laneIndex = workLaneEndMinutes.length;
      workLaneEndMinutes.push(segment.endMinute);
    } else {
      workLaneEndMinutes[laneIndex] = segment.endMinute;
    }
    segment.laneIndex = laneIndex;
  }
  const workLaneCount = Math.max(1, workLaneEndMinutes.length);
  const workCanvasHeight = Math.max(54, workLaneCount * 46 + 7);
  const timeEntryMarkup = timeEntrySegments.map(function renderTimeEntrySegment(segment) {
    // プロジェクトID由来の固定色で作業区間を時間幅に比例して描画する。
    const leftPosition = segment.startMinute * 64 / 60;
    const segmentWidth = Math.max(8, (segment.endMinute - segment.startMinute) * 64 / 60);
    const topPosition = segment.laneIndex * 46 + 4;
    const projectColor = getProjectColor(segment.entry.projectIdentifier);
    const stateClasses = [
      "calendar-time-entry-block",
      segment.entry.endAt ? "" : "running",
      segment.entry.needsReview ? "needs-review" : ""
    ].filter(Boolean).join(" ");
    const endText = segment.entry.endAt ? formatCalendarTime(segment.visibleEnd) : "実行中";
    const activeData = segment.entry.endAt
      ? ""
      : ` data-active-start="${escapeAttribute(segment.entry.startAt)}" data-day-start="${escapeAttribute(dayStart.toISOString())}"`;
    return `<button type="button" class="${stateClasses}" data-time-entry="${escapeAttribute(segment.entry.identifier)}"${activeData}
      style="--entry-color:${projectColor};left:${leftPosition}px;top:${topPosition}px;width:${segmentWidth}px"
      title="${escapeAttribute(buildTimeEntryTooltip(segment.entry))}">
      <strong>${escapeHtml(segment.entry.title)}</strong>
      <span>${formatCalendarTime(segment.visibleStart)}–${endText}</span>
    </button>`;
  }).join("");
  const workEmptyMarkup = matchingTimeEntries.length === 0
    ? `<span class="calendar-day-empty">作業ログなし</span>`
    : "";
  const workRowMarkup = isToday
    ? `<div class="calendar-day-row${todayClass}">
        <div class="calendar-date-gutter">
          <span class="calendar-lane-label work">作業ログ</span>
        </div>
        <div class="calendar-day-canvas" style="height:${workCanvasHeight}px">
          ${inactiveTimeMarkup}${timeEntryMarkup}${workEmptyMarkup}
        </div>
      </div>`
    : "";
  return `
    <div class="calendar-day-group${isToday ? " has-work-log" : ""}">
      <div class="calendar-day-row${todayClass}">
        <div class="calendar-date-gutter">
          <strong>${formatCalendarWeekday(dayStart)}</strong>
          <span class="calendar-date-value">${formatCalendarDate(dayStart)}</span>
        </div>
        <div class="calendar-day-canvas" style="height:${canvasHeight}px">
          ${inactiveTimeMarkup}${allDayMarkup}${eventMarkup}${emptyMarkup}${deadlineMarkup}
        </div>
      </div>
      ${workRowMarkup}
      ${currentTimeMarkup}
    </div>`;
}

/** 活動時間外を時間軸上で薄く覆う帯へ変換する。 */
function renderInactiveTimeMarkup(activityStartMinute, activityEndMinute) {
  // 日中活動では両端、日またぎ活動では中央の非活動区間だけをグレー表示する。
  const minuteToPixel = 64 / 60;
  if (activityStartMinute === activityEndMinute) return "";
  if (activityStartMinute < activityEndMinute) {
    const beforeWidth = activityStartMinute * minuteToPixel;
    const afterLeft = activityEndMinute * minuteToPixel;
    const afterWidth = (1440 - activityEndMinute) * minuteToPixel;
    return `${beforeWidth > 0 ? `<span class="calendar-inactive-period" style="left:0;width:${beforeWidth}px"></span>` : ""}
      ${afterWidth > 0 ? `<span class="calendar-inactive-period" style="left:${afterLeft}px;width:${afterWidth}px"></span>` : ""}`;
  }
  const inactiveLeft = activityEndMinute * minuteToPixel;
  const inactiveWidth = (activityStartMinute - activityEndMinute) * minuteToPixel;
  return `<span class="calendar-inactive-period" style="left:${inactiveLeft}px;width:${inactiveWidth}px"></span>`;
}

/** 日をまたぐ作業ログを指定日の表示区間へ切り詰める。 */
function createTimeEntrySegment(timeEntry, dayStart, dayEnd) {
  // 実行中ログは現在時刻を暫定終了とし、日境界内の分数へ変換する。
  const entryStart = new Date(timeEntry.startAt);
  const entryEnd = timeEntry.endAt ? new Date(timeEntry.endAt) : new Date();
  const visibleStart = entryStart < dayStart ? dayStart : entryStart;
  const visibleEnd = entryEnd > dayEnd ? dayEnd : entryEnd;
  return {
    entry: timeEntry,
    visibleStart,
    visibleEnd,
    startMinute: Math.max(0, (visibleStart.getTime() - dayStart.getTime()) / 60000),
    endMinute: Math.min(1440, Math.max(0, (visibleEnd.getTime() - dayStart.getTime()) / 60000)),
    laneIndex: 0
  };
}

/** 作業ログブロックのツールチップ文を作成する。 */
function buildTimeEntryTooltip(timeEntry) {
  // 名称、プロジェクト、開始・終了、確認状態を改行区切りで返す。
  const endText = timeEntry.endAt ? formatDateTime(timeEntry.endAt) : "実行中";
  return [
    timeEntry.title,
    timeEntry.projectNameSnapshot || "プロジェクト未割当",
    `${formatDateTime(timeEntry.startAt)} ～ ${endText}`,
    timeEntry.needsReview ? "要確認" : ""
  ].filter(Boolean).join("\n");
}

/** プロジェクトIDから保存済みの表示色を返す。 */
function getProjectColor(projectIdentifier) {
  // 未割当または取得できないプロジェクトは共通グレーへ統一する。
  if (!projectIdentifier) return "#a7b0aa";
  const project = findProject(projectIdentifier);
  if (!project) return "#a7b0aa";
  return convertHsvToCssColor(project.colorHue, project.colorSaturation, project.colorValue);
}

/** HSV値をブラウザで利用できるRGB色へ変換する。 */
function convertHsvToCssColor(hueValue, saturationValue, brightnessValue) {
  // HSVの各値を有効範囲へ収め、色相区間ごとのRGB成分を求める。
  const normalizedHue = ((Number(hueValue) % 360) + 360) % 360;
  const normalizedSaturation = Math.min(100, Math.max(0, Number(saturationValue))) / 100;
  const normalizedBrightness = Math.min(100, Math.max(0, Number(brightnessValue))) / 100;
  const chroma = normalizedBrightness * normalizedSaturation;
  const hueSection = normalizedHue / 60;
  const secondaryComponent = chroma * (1 - Math.abs((hueSection % 2) - 1));
  let redComponent = 0;
  let greenComponent = 0;
  let blueComponent = 0;
  if (hueSection < 1) {
    redComponent = chroma;
    greenComponent = secondaryComponent;
  } else if (hueSection < 2) {
    redComponent = secondaryComponent;
    greenComponent = chroma;
  } else if (hueSection < 3) {
    greenComponent = chroma;
    blueComponent = secondaryComponent;
  } else if (hueSection < 4) {
    greenComponent = secondaryComponent;
    blueComponent = chroma;
  } else if (hueSection < 5) {
    redComponent = secondaryComponent;
    blueComponent = chroma;
  } else {
    redComponent = chroma;
    blueComponent = secondaryComponent;
  }
  const matchingComponent = normalizedBrightness - chroma;
  const redValue = Math.round((redComponent + matchingComponent) * 255);
  const greenValue = Math.round((greenComponent + matchingComponent) * 255);
  const blueValue = Math.round((blueComponent + matchingComponent) * 255);
  return `rgb(${redValue} ${greenValue} ${blueValue})`;
}

/** 日をまたぐ予定を指定日の表示区間へ切り詰める。 */
function createCalendarEventSegment(calendarEvent, dayStart, dayEnd) {
  // 日付境界との大小比較から開始分、終了分、継続方向を求める。
  const eventStart = new Date(calendarEvent.startAt);
  const eventEnd = new Date(calendarEvent.endAt);
  const visibleStart = eventStart < dayStart ? dayStart : eventStart;
  const visibleEnd = eventEnd > dayEnd ? dayEnd : eventEnd;
  return {
    event: calendarEvent,
    visibleStart,
    visibleEnd,
    startMinute: Math.max(0, (visibleStart.getTime() - dayStart.getTime()) / 60000),
    endMinute: Math.min(1440, (visibleEnd.getTime() - dayStart.getTime()) / 60000),
    continuesBefore: eventStart < dayStart,
    continuesAfter: eventEnd > dayEnd,
    laneIndex: 0
  };
}

/** 予定ブロックのツールチップ文を作成する。 */
function buildCalendarEventTooltip(calendarEvent) {
  // 名称、元の時間範囲、場所を改行区切りで返す。
  const timeText = calendarEvent.isAllDay
    ? "終日"
    : `${formatDateTime(calendarEvent.startAt)} ～ ${formatDateTime(calendarEvent.endAt)}`;
  return [calendarEvent.title || "予定", timeText, calendarEvent.location].filter(Boolean).join("\n");
}

/** タスク締切マーカーのツールチップ文を作成する。 */
function buildTaskDeadlineTooltip(taskDeadline) {
  // プロジェクト、名称、期限種別、状態を改行区切りで返す。
  const project = findProject(taskDeadline.projectIdentifier);
  const projectName = project?.canonicalName || "プロジェクト未割当";
  return [
    projectName,
    taskDeadline.title,
    `${formatDateTime(taskDeadline.deadlineAt)}（${taskDeadline.deadlineType}）`,
    taskDeadline.status
  ].filter(Boolean).join("\n");
}

/** 今日表示と7日表示を切り替える。 */
function toggleCalendarRange() {
  // 保持済み応答から再描画し追加通信なしで表示日数を切り替える。
  applicationState.calendarRangeExpanded = !applicationState.calendarRangeExpanded;
  document.getElementById("toggle-calendar-range").textContent = applicationState.calendarRangeExpanded
    ? "今日だけ表示"
    : "7日間を表示";
  if (applicationState.dashboardResult?.calendarWidget) {
    renderCalendarTimeline(applicationState.dashboardResult.calendarWidget);
  }
}

/** Google Calendar公式埋め込みの表示状態を切り替える。 */
async function toggleCalendarEmbed() {
  // URL未設定時は設定欄へ誘導し、設定済みなら同じカード内で開閉する。
  const calendarWidget = applicationState.dashboardResult?.calendarWidget;
  if (!calendarWidget || !isTrustedCalendarEmbedUrl(calendarWidget.embedUrl)) {
    await switchView("settings");
    const embedUrlInput = document.querySelector("[name='calendarEmbedUrl']");
    embedUrlInput?.focus();
    showNotice("Google Calendarの埋め込みURLを設定してください。");
    return;
  }
  applicationState.calendarEmbedExpanded = !applicationState.calendarEmbedExpanded;
  renderCalendarEmbed(calendarWidget);
}

/** 埋め込みURLを初回展開時だけiframeへ設定する。 */
function renderCalendarEmbed(calendarWidget) {
  // サーバー検証済みでも画面側でGoogleの埋め込みURLだけを再確認する。
  const embedContainer = document.getElementById("calendar-embed-container");
  const embedButton = document.getElementById("toggle-calendar-embed");
  const embedFrameHost = document.getElementById("calendar-embed-frame-host");
  const hasTrustedUrl = isTrustedCalendarEmbedUrl(calendarWidget?.embedUrl);
  if (!hasTrustedUrl) {
    applicationState.calendarEmbedExpanded = false;
    applicationState.loadedCalendarEmbedUrl = "";
    embedFrameHost.innerHTML = "";
    embedContainer.classList.add("hidden");
    embedButton.textContent = "埋め込みを設定";
    return;
  }
  embedButton.textContent = applicationState.calendarEmbedExpanded
    ? "Google Calendarを閉じる"
    : "Google Calendarを表示";
  embedContainer.classList.toggle("hidden", !applicationState.calendarEmbedExpanded);
  if (!applicationState.calendarEmbedExpanded
      || applicationState.loadedCalendarEmbedUrl === calendarWidget.embedUrl) {
    return;
  }
  embedFrameHost.innerHTML = "";
  const calendarFrame = document.createElement("iframe");
  calendarFrame.className = "calendar-embed-frame";
  calendarFrame.title = "Google Calendar";
  calendarFrame.loading = "lazy";
  calendarFrame.src = calendarWidget.embedUrl;
  embedFrameHost.appendChild(calendarFrame);
  applicationState.loadedCalendarEmbedUrl = calendarWidget.embedUrl;
}

/** Google Calendar公式埋め込みURLかを画面側でも確認する。 */
function isTrustedCalendarEmbedUrl(value) {
  // 任意URLをiframeへ渡さないよう送信元とパスを固定する。
  if (!value) return false;
  try {
    const calendarUrl = new URL(value);
    return calendarUrl.protocol === "https:"
      && calendarUrl.hostname === "calendar.google.com"
      && calendarUrl.pathname === "/calendar/embed"
      && calendarUrl.searchParams.has("src");
  } catch {
    return false;
  }
}

/** 現在時刻線を24時間軸上の最新位置へ移動する。 */
function updateCalendarCurrentTimeMarker() {
  // 1日の経過分を1時間64pxの横位置へ換算する。
  const markerPosition = calculateCurrentMinuteOfDay() * 64 / 60;
  for (const currentTimeMarker of document.querySelectorAll("[data-current-time-marker]")) {
    currentTimeMarker.style.left = `${markerPosition}px`;
  }
}

/** 現在時刻を0時からの分数で返す。 */
function calculateCurrentMinuteOfDay() {
  // 時、分、秒を分へ換算して滑らかな現在位置を求める。
  const currentTime = new Date();
  return currentTime.getHours() * 60 + currentTime.getMinutes() + currentTime.getSeconds() / 60;
}

/** 時分文字列を0時からの分数へ変換する。 */
function parseClockTimeToMinute(value, fallbackMinute) {
  // 設定値が不正な場合は画面を壊さず指定された境界値へ戻す。
  const matchedTime = /^(\d{1,2}):(\d{2})$/.exec(String(value || ""));
  if (!matchedTime) return fallbackMinute;
  const hour = Number(matchedTime[1]);
  const minute = Number(matchedTime[2]);
  if (hour < 0 || hour > 24 || minute < 0 || minute > 59 || (hour === 24 && minute !== 0)) {
    return fallbackMinute;
  }
  return hour * 60 + minute;
}

/** ダッシュボード表示中だけ5分周期で再読込する。 */
async function refreshVisibleDashboard() {
  // 非表示タブと他画面では通信せず、必要な場合だけ最新状態へ更新する。
  if (applicationState.activeView !== "dashboard" || document.visibilityState !== "visible") return;
  try {
    await loadDashboard();
  } catch (error) {
    showNotice(error.message, true);
  }
}

/** ブラウザへ戻った時点で表示中画面を更新する。 */
async function refreshApplicationWhenVisible() {
  // スリープ復帰やSSE切断中の変更を選択タブを維持したまま整合させる。
  if (document.visibilityState === "visible") {
    applicationState.uiChangeRefreshPending = true;
    await applyPendingUiChange();
  }
}

/** 同期日時を時分だけの控えめな表示へ変換する。 */
function formatCalendarSyncTime(value) {
  // 日付を強調せず直近の同期時刻だけを返す。
  return new Intl.DateTimeFormat("ja-JP", { hour: "2-digit", minute: "2-digit" }).format(new Date(value));
}

/** カレンダーの時刻を時分形式へ変換する。 */
function formatCalendarTime(value) {
  // 24時間表記の短い時刻を予定ブロックへ表示する。
  return new Intl.DateTimeFormat("ja-JP", { hour: "2-digit", minute: "2-digit", hour12: false }).format(new Date(value));
}

/** カレンダー日付を月日形式へ変換する。 */
function formatCalendarDate(value) {
  // 日付ラベルを狭い固定列へ収める。
  return new Intl.DateTimeFormat("ja-JP", { month: "numeric", day: "numeric" }).format(value);
}

/** カレンダー曜日を短い日本語で返す。 */
function formatCalendarWeekday(value) {
  // 7日表示で日付を識別できるよう曜日だけを返す。
  return new Intl.DateTimeFormat("ja-JP", { weekday: "short" }).format(value);
}

/** ダッシュボードへ現在の作業タイマーと操作を表示する。 */
function renderTimeTracker(activeTimeEntry) {
  // タイマーなしでは自由活動開始、実行中では停止・完了・延長を表示する。
  const panel = document.getElementById("time-tracker-panel");
  if (!activeTimeEntry) {
    panel.className = "time-tracker-panel time-tracker-empty";
    panel.innerHTML = `
      <div class="time-tracker-main"><strong>作業タイマーは停止中です</strong><span>タスクを開始するか、自由活動を記録できます。</span></div>
      <div class="time-tracker-actions"><button type="button" class="secondary-button" data-time-command="free-start">自由活動を開始</button></div>`;
  } else {
    const warningActive = Boolean(activeTimeEntry.warningAt);
    panel.className = `time-tracker-panel${warningActive ? " time-tracker-warning" : ""}`;
    panel.innerHTML = `
      <div class="time-tracker-main">
        <strong>${escapeHtml(activeTimeEntry.title)}</strong>
        <span>${escapeHtml(activeTimeEntry.projectNameSnapshot || "プロジェクト未割当")}</span>
        <span class="time-tracker-running" data-time-entry-elapsed="${escapeAttribute(activeTimeEntry.startAt)}">計測中</span>
        ${warningActive ? `<span>長時間継続しています。1時間延長するか停止してください。</span>` : ""}
      </div>
      <div class="time-tracker-actions">
        ${activeTimeEntry.taskIdentifier ? `<button type="button" class="primary-button" data-time-command="complete" data-task="${escapeAttribute(activeTimeEntry.taskIdentifier)}">完了</button>` : ""}
        ${warningActive ? `<button type="button" class="secondary-button" data-time-command="extend" data-time-entry="${escapeAttribute(activeTimeEntry.identifier)}">1時間延長</button>` : ""}
        <button type="button" class="secondary-button" data-time-command="stop" data-time-entry="${escapeAttribute(activeTimeEntry.identifier)}">停止</button>
        <button type="button" class="secondary-button" data-time-command="free-start">別の自由活動</button>
      </div>`;
  }
  bindTimeTrackerButtons(panel);
  updateActiveTimeEntryDisplays();
}

/** 要確認の自動停止ログをダッシュボードへ表示する。 */
function renderTimeReviewCards(reviewEntries) {
  // 時刻修正または現状確定の選択肢をログごとに用意する。
  const container = document.getElementById("time-review-cards");
  container.innerHTML = reviewEntries.map(function renderReviewCard(timeEntry) {
    return `<article class="time-review-card">
      <div><strong>記録を確認してください：${escapeHtml(timeEntry.title)}</strong>
      <span>${formatDateTime(timeEntry.startAt)} ～ ${timeEntry.endAt ? formatDateTime(timeEntry.endAt) : "実行中"}・${escapeHtml(timeEntry.reviewReason || "確認が必要です。")}</span></div>
      <div class="time-tracker-actions">
        <button type="button" class="secondary-button" data-review-edit="${escapeAttribute(timeEntry.identifier)}">記録を修正</button>
        <button type="button" class="primary-button" data-review-confirm="${escapeAttribute(timeEntry.identifier)}">このままで確定</button>
      </div>
    </article>`;
  }).join("");
  for (const editButton of container.querySelectorAll("[data-review-edit]")) {
    editButton.addEventListener("click", function handleReviewEdit() {
      const entry = reviewEntries.find(item => item.identifier === editButton.dataset.reviewEdit);
      if (entry) openTimeEntryDialog(entry.identifier, entry);
    });
  }
  for (const confirmButton of container.querySelectorAll("[data-review-confirm]")) {
    confirmButton.addEventListener("click", async function handleReviewConfirm() {
      await executeTimeEntryCommand(confirmButton.dataset.reviewConfirm, "confirm");
    });
  }
}

/** 現在タイマー内の各操作ボタンを接続する。 */
function bindTimeTrackerButtons(container) {
  // 自由活動、タスク完了、停止、長時間延長を対応APIへ振り分ける。
  for (const commandButton of container.querySelectorAll("[data-time-command]")) {
    commandButton.addEventListener("click", async function handleTimeTrackerCommand() {
      const commandName = commandButton.dataset.timeCommand;
      if (commandName === "free-start") {
        openFreeActivityDialog();
      } else if (commandName === "complete") {
        await executeTaskAction(commandButton.dataset.task, "complete");
      } else {
        await executeTimeEntryCommand(commandButton.dataset.timeEntry, commandName);
      }
    });
  }
}

/** 作業ログの停止・延長・確認操作を実行する。 */
async function executeTimeEntryCommand(timeEntryIdentifier, commandName) {
  // 操作後はダッシュボード、タスク、表示中なら作業時間統計を更新する。
  try {
    await apiRequest(
      `/api/v1/time-entries/${encodeURIComponent(timeEntryIdentifier)}/${commandName}`,
      { method: "POST", body: "{}" });
    showNotice(commandName === "extend" ? "次の警告を1時間後へ延長しました。" : "作業ログを更新しました。");
    await Promise.all([loadDashboard(), loadTasks()]);
    if (applicationState.activeView === "time") await loadTimeReport();
  } catch (error) {
    showNotice(error.message, true);
  }
}

/** 作業タイマーの経過表示と実行中ブロック幅を現在時刻へ更新する。 */
function updateActiveTimeEntryDisplays() {
  // テキストは秒単位、カレンダーブロックは1時間64pxの現在幅へ更新する。
  for (const elapsedElement of document.querySelectorAll("[data-time-entry-elapsed]")) {
    const startTimestamp = Date.parse(elapsedElement.dataset.timeEntryElapsed);
    if (!Number.isFinite(startTimestamp)) continue;
    const elapsedSeconds = Math.max(0, Math.floor((Date.now() - startTimestamp) / 1000));
    elapsedElement.textContent = `計測中 ${formatDuration(elapsedSeconds)}`;
  }
  for (const activeBlock of document.querySelectorAll(".calendar-time-entry-block[data-active-start][data-day-start]")) {
    const activeStart = new Date(activeBlock.dataset.activeStart);
    const dayStart = new Date(activeBlock.dataset.dayStart);
    const dayEnd = new Date(dayStart);
    dayEnd.setDate(dayEnd.getDate() + 1);
    const visibleStart = activeStart < dayStart ? dayStart : activeStart;
    const visibleEnd = new Date() > dayEnd ? dayEnd : new Date();
    const width = Math.max(8, (visibleEnd.getTime() - visibleStart.getTime()) / 60000 * 64 / 60);
    activeBlock.style.width = `${width}px`;
  }
}

/** 推薦カードへ1件のタスクまたは休憩案内を表示する。 */
function renderRecommendation(result) {
  // 推薦なしの場合も次の行動を明確に表示する。
  const card = document.getElementById("recommendation-card");
  card.classList.remove("loading-card");
  if (!result.recommendation) {
    const forceDisplayAction = result.canForceRecommendation
      ? `<div class="action-row wait-recommendation-actions"><button id="force-recommendation-display" class="secondary-button" type="button">タスクを表示</button></div>`
      : "";
    card.innerHTML = `
      <span class="task-category">WAIT</span>
      <h2 class="recommendation-title">休憩または簡単な整理</h2>
      <p class="recommendation-reason">${escapeHtml(result.emptyReason)}</p>
      ${forceDisplayAction}`;
    document.getElementById("force-recommendation-display")?.addEventListener("click", async function forceRecommendationDisplay() {
      // 現在の待機判定は変更せず、優先度最上位の候補だけを一時表示する。
      applicationState.forceRecommendationDisplay = true;
      await loadDashboard();
    });
    return;
  }
  const evaluation = result.recommendation;
  const task = evaluation.task;
  const isRunning = task.status === "実行中";
  const nextEventText = result.currentSlot.nextEventTitle
    ? ` 次の予定「${escapeHtml(result.currentSlot.nextEventTitle)}」までに進められます。`
    : "";
  const executionSummary = isRunning
    ? `<div class="execution-summary"><span class="execution-status">● 実行中</span><span class="elapsed-time" data-started-at="${escapeAttribute(task.startedAt || "")}">開始から0分経過</span></div>`
    : "";
  card.innerHTML = `
    <span class="task-category">${escapeHtml(task.category)}</span>
    ${executionSummary}
    <h2 class="recommendation-title">${escapeHtml(task.title)}</h2>
    <p class="recommendation-reason">${escapeHtml(evaluation.reason)}${nextEventText}</p>
    <div class="recommendation-meta">
      <div class="meta-item"><strong>${evaluation.suggestedMinutes}分</strong><span>推奨作業時間</span></div>
      <div class="meta-item"><strong>${task.deadlineAt ? formatDateTime(task.deadlineAt) : "期限なし"}</strong><span>期限</span></div>
      <div class="meta-item"><strong>${evaluation.priorityScore}</strong><span>優先度スコア</span></div>
    </div>
    <p><strong>完了条件：</strong>${escapeHtml(task.completionCondition || `${task.title}を完了する`)}</p>
    <div class="action-row recommendation-actions">
      <button class="primary-button dashboard-task-action-button${isRunning ? " running-button" : ""}" data-action="start" data-task="${escapeAttribute(task.identifier)}" ${isRunning ? "disabled aria-disabled=\"true\"" : ""}>${isRunning ? "実行中" : "開始"}</button>
      <button class="${isRunning ? "primary-button" : "secondary-button"} dashboard-task-action-button" data-action="complete" data-task="${escapeAttribute(task.identifier)}" ${isRunning ? "" : "data-confirm-non-running-completion=\"true\""}>完了</button>
      <button class="secondary-button dashboard-task-action-button" data-action="continue" data-task="${escapeAttribute(task.identifier)}" ${isRunning ? "" : "disabled aria-disabled=\"true\""}>続行</button>
      <button class="secondary-button dashboard-task-action-button" data-action="interrupt" data-task="${escapeAttribute(task.identifier)}" ${isRunning ? "" : "disabled aria-disabled=\"true\""}>中断</button>
      <button class="secondary-button" data-action="postpone" data-task="${escapeAttribute(task.identifier)}">1時間延期</button>
      <button type="button" class="secondary-button recommendation-edit-button" data-dashboard-task-edit="${escapeAttribute(task.identifier)}">編集</button>
    </div>`;
  bindTaskActionButtons(card);
  card.querySelector("[data-dashboard-task-edit]").addEventListener("click", async function handleRecommendationTaskEditClick() {
    // 推薦タスクの最新情報を取得して既存の編集ダイアログを開く。
    try {
      await loadTasks();
      openTaskDialog(task.identifier);
    } catch (error) {
      showNotice(error.message, true);
    }
  });
  updateElapsedTimeDisplays();
}

/** 実行中タスクの経過分数を現在時刻へ更新する。 */
function updateElapsedTimeDisplays() {
  // 表示中の開始日時を読み取り、再読み込みなしで分数を書き換える。
  for (const elapsedTimeElement of document.querySelectorAll(".elapsed-time[data-started-at]")) {
    const startedAt = elapsedTimeElement.dataset.startedAt;
    const elapsedMinutes = calculateElapsedMinutes(startedAt);
    elapsedTimeElement.textContent = elapsedMinutes === null
      ? "開始時刻を確認できません"
      : `開始から${elapsedMinutes}分経過`;
  }
}

/** 開始日時から現在までの経過時間を分単位で計算する。 */
function calculateElapsedMinutes(startedAt) {
  // ミリ秒の差を分へ変換し、未来日時や端数を0以上の整数へ丸める。
  const startedTimestamp = Date.parse(startedAt);
  if (!Number.isFinite(startedTimestamp)) return null;
  const elapsedMilliseconds = Date.now() - startedTimestamp;
  return Math.max(0, Math.floor(elapsedMilliseconds / 60000));
}

/** 完了確認カードを表示または非表示にする。 */
function renderFollowUp(task) {
  // 確認期限を過ぎた実行中タスクだけを表示する。
  const card = document.getElementById("follow-up-card");
  if (!task) {
    card.classList.add("hidden");
    card.innerHTML = "";
    return;
  }
  card.classList.remove("hidden");
  card.innerHTML = `
    <strong>完了しましたか？</strong>
    <p>${escapeHtml(task.title)}</p>
    <div class="action-row">
      <button class="primary-button dashboard-task-action-button" data-action="complete" data-task="${escapeAttribute(task.identifier)}">完了</button>
      <button class="secondary-button dashboard-task-action-button" data-action="continue" data-task="${escapeAttribute(task.identifier)}">続行</button>
      <button class="secondary-button dashboard-task-action-button" data-action="interrupt" data-task="${escapeAttribute(task.identifier)}">中断</button>
    </div>`;
  bindTaskActionButtons(card);
}

/** コンテナ内のタスク操作ボタンへイベントを接続する。 */
function bindTaskActionButtons(container) {
  // 操作後にタスク一覧と推薦を同時更新する。
  for (const actionButton of container.querySelectorAll("[data-action]")) {
    actionButton.addEventListener("click", async function handleTaskActionClick() {
      if (!confirmNonRunningCompletion(actionButton)) return;
      await executeTaskAction(actionButton.dataset.task, actionButton.dataset.action);
    });
  }
}

/** 非実行中タスクをダッシュボードから完了する前に利用者へ確認する。 */
function confirmNonRunningCompletion(actionButton) {
  // 確認対象として描画された完了ボタンだけにダイアログを表示する。
  if (actionButton.dataset.action !== "complete"
    || actionButton.dataset.confirmNonRunningCompletion !== "true") return true;
  const taskIdentifier = actionButton.dataset.task;
  const task = applicationState.tasks.find(function findCompletionTask(item) {
    return item.identifier === taskIdentifier;
  }) || applicationState.dashboardResult?.recommendation?.task;
  const taskLabel = task?.title || taskIdentifier;
  return window.confirm(`「${taskLabel}」は現在実行中ではありません。\n完了にしますか？`);
}

/** 指定タスクへ状態操作を適用する。 */
async function executeTaskAction(taskIdentifier, actionName) {
  // 重大な中止操作だけ確認してからAPIを呼び出す。
  if (actionName === "cancel" && !window.confirm(`タスク ${taskIdentifier} を中止しますか？`)) return;
  if (actionName === "start" && applicationState.taskStartPending) return;
  if (actionName === "start") applicationState.taskStartPending = true;
  try {
    // 別の活動が進行中なら終了方法を選んでから開始する。
    if (actionName === "start" && !await prepareTaskStart(taskIdentifier)) return;
    await apiRequest(`/api/v1/tasks/${encodeURIComponent(taskIdentifier)}/actions/${actionName}`, {
      method: "POST",
      body: JSON.stringify({ postponeMinutes: actionName === "postpone" ? 60 : null })
    });
    if (actionName === "complete" || actionName === "interrupt") {
      // 強制表示したタスクの終了後は通常の待機表示へ戻す。
      applicationState.forceRecommendationDisplay = false;
    }
    showNotice("タスクを更新しました。");
    await Promise.all([loadDashboard(), loadTasks()]);
  } catch (error) {
    showNotice(error.message, true);
  } finally {
    if (actionName === "start") applicationState.taskStartPending = false;
  }
}

/** 現在の活動を確認し、利用者が選んだ終了操作を適用する。 */
async function prepareTaskStart(taskIdentifier) {
  // 最新のタイマーを読み、同じタスクの再開始は行わない。
  const activeEntry = await apiRequest("/api/v1/time-entries/active");
  if (!activeEntry) return true;
  if (activeEntry.taskIdentifier === taskIdentifier) {
    showNotice("このタスクは既に実行中です。");
    return false;
  }
  const selection = await chooseTaskSwitchAction(activeEntry);
  if (selection === "cancel") return false;
  // 選択中に別画面でタイマーが変わった場合は切替を取り消す。
  const currentEntry = await apiRequest("/api/v1/time-entries/active");
  if (currentEntry?.identifier !== activeEntry.identifier) {
    throw new Error("実行中の活動が変わりました。もう一度実行してください。");
  }
  const commandPath = activeEntry.taskIdentifier
    ? `/api/v1/tasks/${encodeURIComponent(activeEntry.taskIdentifier)}/actions/${selection}`
    : `/api/v1/time-entries/${encodeURIComponent(activeEntry.identifier)}/stop`;
  await apiRequest(commandPath, { method: "POST", body: "{}" });
  // 後続の開始に失敗しても終了済みの状態を画面へ反映する。
  await Promise.all([loadDashboard(), loadTasks()]);
  return true;
}

/** タスク切替時の終了方法を選ぶダイアログを表示する。 */
function chooseTaskSwitchAction(activeEntry) {
  // キャンセルとEscapeは現在の活動を維持する。
  return new Promise(function awaitSwitchSelection(resolve) {
    // 活動名はエスケープし、タスクと自由活動で選択肢を切り替える。
    const dialog = document.createElement("dialog");
    dialog.setAttribute("aria-labelledby", "task-switch-title");
    dialog.innerHTML = `<form method="dialog" style="padding:24px">
      <div class="dialog-header"><h2 id="task-switch-title">現在の活動をどうしますか？</h2></div>
      <p>${escapeHtml(activeEntry.title)}${activeEntry.taskIdentifier ? " を実行中です。" : " を計測中です。"}</p>
      <p>終了方法を選ぶと、選択したタスクを実行します。</p>
      <div class="dialog-actions">
        ${activeEntry.taskIdentifier
          ? `<button value="complete" class="primary-button">完了</button><button value="interrupt" class="secondary-button">中断</button>`
          : `<button value="stop" class="primary-button">停止</button>`}
        <button value="cancel" class="secondary-button" autofocus>キャンセル</button>
      </div></form>`;
    dialog.addEventListener("close", function handleSwitchClose() {
      // ダイアログを除去して選択結果を呼び出し元へ返す。
      const selection = dialog.returnValue || "cancel";
      dialog.remove();
      resolve(selection);
    }, { once: true });
    document.body.append(dialog);
    dialog.showModal();
  });
}

/** 全タスクを取得して一覧と下書きを更新する。 */
async function loadTasks() {
  // 取得結果を画面内キャッシュへ保存して複数ビューで共有する。
  applicationState.tasks = await apiRequest("/api/v1/tasks");
  populateTimeTaskSelector();
  renderTaskTable();
  renderDrafts();
}

/** 作業ログ編集フォームへタスク候補を設定する。 */
function populateTimeTaskSelector() {
  // 現在値を維持し、下書き以外のタスクを新しい順で候補にする。
  const selector = document.querySelector("#time-entry-form [name='taskIdentifier']");
  if (!selector) return;
  const currentValue = selector.value;
  selector.innerHTML = `<option value="">自由活動</option>`;
  const selectableTasks = [...applicationState.tasks]
    .filter(task => task.status !== "下書き")
    .sort((firstTask, secondTask) => (Date.parse(secondTask.createdAt) || 0) - (Date.parse(firstTask.createdAt) || 0));
  for (const task of selectableTasks) {
    selector.insertAdjacentHTML(
      "beforeend",
      `<option value="${escapeAttribute(task.identifier)}">${escapeHtml(task.title)} [${escapeHtml(task.status)}]</option>`);
  }
  selector.value = currentValue;
}

/** 現在の検索条件と並び順に一致するタスクを返す。 */
function getFilteredTasks() {
  // 終了状態表示、検索、状態、区分・プロジェクト、並び順を一度だけ評価して表と図で共有する。
  const searchText = document.getElementById("task-search").value.trim().toLowerCase();
  const selectedStatus = document.getElementById("task-status-filter").value;
  const selectedProject = document.getElementById("task-project-filter").value;
  const selectedSortOrder = document.getElementById("task-sort-order").value;
  const showClosedTasks = document.getElementById("show-completed-tasks").checked;
  return applicationState.tasks.filter(function filterTask(task) {
    // チェックされていない間は完了と中止を一覧から除外する。
    const matchesClosedVisibility = showClosedTasks || (task.status !== "完了" && task.status !== "中止");
    const matchesStatus = !selectedStatus || task.status === selectedStatus;
    const matchesProjectScope = selectedProject.startsWith(taskCategoryFilterPrefix)
      ? task.category === selectedProject.slice(taskCategoryFilterPrefix.length)
      : selectedProject === unassignedProjectFilterValue
        ? !task.projectIdentifier
        : !selectedProject || task.projectIdentifier === selectedProject;
    const searchableText = `${task.identifier} ${task.title} ${task.details}`.toLowerCase();
    return matchesClosedVisibility && matchesStatus && matchesProjectScope
      && (!searchText || searchableText.includes(searchText));
  }).sort(function sortFilteredTasks(firstTask, secondTask) {
    // 登録順では作成日時の降順を使い、同時刻の場合もIDで表示順を安定させる。
    const firstCreatedTimestamp = Date.parse(firstTask.createdAt) || 0;
    const secondCreatedTimestamp = Date.parse(secondTask.createdAt) || 0;
    if (selectedSortOrder === "created") {
      if (firstCreatedTimestamp !== secondCreatedTimestamp) {
        return secondCreatedTimestamp - firstCreatedTimestamp;
      }
      return String(secondTask.identifier || "").localeCompare(String(firstTask.identifier || ""), "ja");
    }

    // 既定の優先順位順ではスコアを降順にし、同点では新しいタスクを上へ表示する。
    const firstPriorityScore = Number.isFinite(Number(firstTask.priorityScore))
      ? Number(firstTask.priorityScore)
      : 0;
    const secondPriorityScore = Number.isFinite(Number(secondTask.priorityScore))
      ? Number(secondTask.priorityScore)
      : 0;
    if (firstPriorityScore !== secondPriorityScore) {
      return secondPriorityScore - firstPriorityScore;
    }
    if (firstCreatedTimestamp !== secondCreatedTimestamp) {
      return secondCreatedTimestamp - firstCreatedTimestamp;
    }
    return String(secondTask.identifier || "").localeCompare(String(firstTask.identifier || ""), "ja");
  });
}

/** 計算済みの優先度スコアを一覧表示用の文字列へ整形する。 */
function formatTaskPriorityScore(task) {
  // 推薦評価の対象外で計算結果がないタスクは数値の0と区別してダッシュ表示にする。
  const priorityScore = Number(task.priorityScore);
  if (!(Number(task.suggestedMinutes) > 0) || !Number.isFinite(priorityScore)) return "—";
  return priorityScore.toFixed(1).replace(/\.0$/, "");
}

/** 検索条件に一致するタスクを表へ表示する。 */
function renderTaskTable() {
  // 同じ絞り込み結果を一覧表と依存関係図の両方へ反映する。
  const tableBody = document.getElementById("task-table-body");
  const filteredTasks = getFilteredTasks();
  renderTaskDependencyGraph(filteredTasks);
  if (filteredTasks.length === 0) {
    tableBody.innerHTML = `<tr><td colspan="8" class="muted">該当するタスクはありません。</td></tr>`;
    return;
  }
  tableBody.innerHTML = filteredTasks.map(function renderTaskRow(task) {
    const project = findProject(task.projectIdentifier);
    const projectColor = getProjectColor(task.projectIdentifier);
    const deadline = task.deadlineAt ? formatTaskDeadline(task.deadlineAt) : null;
    return `<tr style="--project-color:${projectColor}">
      <td class="task-project-column task-project-accent">${project ? escapeHtml(project.canonicalName) : "—"}</td>
      <td class="task-title-cell"><strong>${escapeHtml(task.title)}</strong><small>${escapeHtml(task.identifier)}${task.validationResult ? ` · ${escapeHtml(task.validationResult)}` : ""}</small></td>
      <td><span class="status-badge ${escapeAttribute(task.status)}">${escapeHtml(task.status)}</span></td>
      <td class="task-deadline-column">${deadline ? `<span class="task-deadline-date">${escapeHtml(deadline.date)}</span><span class="task-deadline-time">${escapeHtml(deadline.time)}</span>` : "—"}</td>
      <td class="task-remaining-column">${task.remainingMinutes}分</td>
      <td class="task-importance-column">${task.importance}</td>
      <td class="task-score-column">${formatTaskPriorityScore(task)}</td>
      <td><div class="task-actions"><button class="text-button edit-task-button" data-task="${escapeAttribute(task.identifier)}">編集</button><button class="text-button start-task-button" data-task="${escapeAttribute(task.identifier)}" ${task.status !== "実行可能" ? "disabled" : ""}>実行</button></div></td>
    </tr>`;
  }).join("");
  for (const editButton of tableBody.querySelectorAll(".edit-task-button")) {
    editButton.addEventListener("click", function handleEditClick() { openTaskDialog(editButton.dataset.task); });
  }
  for (const startButton of tableBody.querySelectorAll(".start-task-button")) {
    startButton.addEventListener("click", async function handleStartClick() {
      // 一覧の選択タスクを共通の切替確認経由で開始する。
      await executeTaskAction(startButton.dataset.task, "start");
    });
  }
}

/** タスク画面へ現在の絞り込みに対応する依存関係図を表示する。 */
function renderTaskDependencyGraph(filteredTasks) {
  // 中止タスクを除外しつつ、絞り込み外の先行タスクも補助表示して処理順を保つ。
  const dependencyPanel = document.getElementById("task-dependency-panel");
  const wasOpen = dependencyPanel.open;
  const cancelledTaskIdentifiers = new Set(applicationState.tasks
    .filter(function selectCancelledTask(task) { return task.status === "中止"; })
    .map(function selectCancelledTaskIdentifier(task) { return task.identifier; }));
  const graphPrimaryTasks = filteredTasks.filter(function excludeCancelledPrimaryTask(task) {
    return task.status !== "中止";
  });
  const presentation = createDependencyGraphPresentation(
    graphPrimaryTasks,
    applicationState.tasks,
    cancelledTaskIdentifiers,
    true);
  if (!presentation) {
    dependencyPanel.classList.add("hidden");
    dependencyPanel.innerHTML = "";
    return;
  }
  dependencyPanel.classList.remove("hidden");
  dependencyPanel.innerHTML = createDependencyPanelContent("タスクツリー", presentation);
  dependencyPanel.open = dependencyPanel.dataset.rendered ? wasOpen : true;
  dependencyPanel.dataset.rendered = "true";
  initializeTaskDependencyEditing(dependencyPanel);
  initializeDependencyGraphScrolling(dependencyPanel, applicationState.dependencyGraphScrollShouldReset);
  scheduleDependencyGraphDrawing();
}

/** タスクツリー内の編集ボタンを既存のタスク編集ダイアログへ接続する。 */
function initializeTaskDependencyEditing(dependencyPanel) {
  // 参照切れを除く各カードから対応するタスクを直接編集できるようにする。
  for (const editButton of dependencyPanel.querySelectorAll("[data-dependency-task-edit]")) {
    editButton.addEventListener("click", function handleDependencyTaskEditClick(event) {
      event.stopPropagation();
      openTaskDialog(editButton.dataset.dependencyTaskEdit);
    });
  }
}

/** 下書きタスクをバッチ単位で表示する。 */
function renderDrafts() {
  // バッチIDがない旧下書きも単独グループとして表示する。
  const draftContainer = document.getElementById("draft-list");
  const draftTasks = applicationState.tasks.filter(function filterDraft(task) { return task.status === "下書き"; });
  if (draftTasks.length === 0) {
    draftContainer.innerHTML = `<div class="empty-state">承認待ちのAI下書きはありません。</div>`;
    return;
  }
  const groupedDrafts = new Map();
  for (const draftTask of draftTasks) {
    const batchIdentifier = draftTask.draftBatchIdentifier || "旧下書き";
    const batchTasks = groupedDrafts.get(batchIdentifier) || [];
    batchTasks.push(draftTask);
    groupedDrafts.set(batchIdentifier, batchTasks);
  }
  draftContainer.innerHTML = Array.from(groupedDrafts.entries()).map(function renderDraftBatch(entry) {
    // 各バッチ内の依存関係だけを独立した図として先に表示する。
    const batchIdentifier = entry[0];
    const tasks = entry[1];
    const hasValidationError = tasks.some(function hasError(task) { return Boolean(task.validationResult); });
    const presentation = createDependencyGraphPresentation(tasks, applicationState.tasks);
    const dependencyHtml = presentation
      ? `<details class="dependency-panel draft-dependency-panel" open>${createDependencyPanelContent("この下書きの処理順", presentation)}</details>`
      : "";
    const taskHtml = tasks.map(function renderDraftTask(task) {
      // 図と照合できるようにIDと依存先を文字でも残す。
      return `<div class="draft-task"><strong>${escapeHtml(task.title)}</strong><div class="muted">${task.estimatedMinutes}分 · 依存: ${escapeHtml(getDependencyIdentifiers(task).join(", ") || "なし")}</div>${task.validationResult ? `<div class="validation-error">${escapeHtml(task.validationResult)}</div>` : ""}</div>`;
    }).join("");
    return `<article class="draft-batch"><div class="draft-batch-header"><div><span class="task-category">${escapeHtml(batchIdentifier)}</span><h2>${tasks.length}件の下書き</h2></div>${batchIdentifier !== "旧下書き" ? `<button class="primary-button approve-draft-button" data-batch="${escapeAttribute(batchIdentifier)}" ${hasValidationError ? "disabled" : ""}>一括承認</button>` : ""}</div>${dependencyHtml}<div class="draft-task-list">${taskHtml}</div></article>`;
  }).join("");
  for (const approveButton of draftContainer.querySelectorAll(".approve-draft-button")) {
    approveButton.addEventListener("click", async function handleApprovalClick() {
      // 押されたバッチだけを既存の承認処理へ渡す。
      await approveDraftBatch(approveButton.dataset.batch);
    });
  }
  initializeDependencyGraphScrolling(draftContainer, false);
  scheduleDependencyGraphDrawing();
}

/** タスクが持つ依存先IDを重複なしで返す。 */
function getDependencyIdentifiers(task) {
  // 古いデータで配列が欠けていても空配列として安全に扱う。
  return Array.isArray(task?.dependencyIdentifiers)
    ? Array.from(new Set(task.dependencyIdentifiers.filter(Boolean)))
    : [];
}

/** 表示対象とその全先行タスクから依存グラフを構築する。 */
function buildDependencyGraph(primaryTasks, availableTasks, excludedTaskIdentifiers = new Set()) {
  // 依存先を持つ表示対象だけを起点にして無関係な独立タスクを除外する。
  const primaryIdentifiers = new Set(primaryTasks.map(function selectPrimaryIdentifier(task) {
    // 絞り込み外かどうかを後で判定できるようIDだけを保持する。
    return task.identifier;
  }));
  const availableTaskMap = new Map();
  for (const availableTask of availableTasks) {
    if (availableTask.identifier
      && !excludedTaskIdentifiers.has(availableTask.identifier)
      && !availableTaskMap.has(availableTask.identifier)) {
      availableTaskMap.set(availableTask.identifier, availableTask);
    }
  }

  const includedTaskMap = new Map();
  const missingIdentifiers = new Set();
  const pendingIdentifiers = primaryTasks
    .filter(function selectDependentTask(task) {
      // 依存先を持たない独立タスクは図の起点にしない。
      return getDependencyIdentifiers(task).length > 0;
    })
    .map(function selectDependentIdentifier(task) {
      // 再帰探索の開始IDへ変換する。
      return task.identifier;
    });

  let pendingIdentifierIndex = 0;
  while (pendingIdentifierIndex < pendingIdentifiers.length) {
    // 配列先頭の削除を避け、探索件数に比例する時間で先行タスクを走査する。
    const taskIdentifier = pendingIdentifiers[pendingIdentifierIndex];
    pendingIdentifierIndex += 1;
    if (!taskIdentifier || includedTaskMap.has(taskIdentifier)) continue;
    const task = availableTaskMap.get(taskIdentifier);
    if (!task) {
      missingIdentifiers.add(taskIdentifier);
      continue;
    }
    includedTaskMap.set(taskIdentifier, task);
    for (const dependencyIdentifier of getDependencyIdentifiers(task)) {
      // 中止など明示的な除外対象は参照切れカードにもせず図から完全に除外する。
      if (excludedTaskIdentifiers.has(dependencyIdentifier)) continue;
      if (availableTaskMap.has(dependencyIdentifier)) {
        pendingIdentifiers.push(dependencyIdentifier);
      } else {
        missingIdentifiers.add(dependencyIdentifier);
      }
    }
  }

  const nodes = [];
  for (const task of includedTaskMap.values()) {
    nodes.push({
      identifier: task.identifier,
      title: task.title,
      parentIdentifier: task.parentIdentifier || null,
      parentTitle: availableTaskMap.get(task.parentIdentifier)?.title || null,
      status: task.status,
      remainingMinutes: task.remainingMinutes,
      deadlineAt: task.deadlineAt,
      projectIdentifier: task.projectIdentifier,
      isMissing: false,
      isOutsideScope: !primaryIdentifiers.has(task.identifier),
      isCycle: false,
      isUnresolved: false
    });
  }
  for (const missingIdentifier of missingIdentifiers) {
    nodes.push({
      identifier: missingIdentifier,
      title: "参照先のタスクが見つかりません",
      parentIdentifier: null,
      parentTitle: null,
      status: "参照切れ",
      remainingMinutes: null,
      deadlineAt: null,
      projectIdentifier: null,
      isMissing: true,
      isOutsideScope: true,
      isCycle: false,
      isUnresolved: false
    });
  }

  const includedIdentifiers = new Set(nodes.map(function selectNodeIdentifier(node) {
    // 辺を構築できる対象IDへ変換する。
    return node.identifier;
  }));
  const edges = [];
  for (const task of includedTaskMap.values()) {
    for (const dependencyIdentifier of getDependencyIdentifiers(task)) {
      if (includedIdentifiers.has(dependencyIdentifier)) {
        edges.push({
          sourceIdentifier: dependencyIdentifier,
          targetIdentifier: task.identifier
        });
      }
    }
  }
  if (edges.length === 0) {
    return { nodes: [], edges: [], levels: [], components: [], cycleIdentifiers: new Set(), unresolvedIdentifiers: new Set() };
  }

  const cycleIdentifiers = findDependencyCycleIdentifiers(nodes, edges);
  const layout = calculateDependencyLevels(nodes, edges);
  for (const node of nodes) {
    node.isCycle = cycleIdentifiers.has(node.identifier);
    node.isUnresolved = layout.unresolvedIdentifiers.has(node.identifier);
  }
  return {
    nodes,
    edges,
    levels: layout.levels,
    components: splitDependencyGraphComponents(nodes, edges),
    cycleIdentifiers,
    unresolvedIdentifiers: layout.unresolvedIdentifiers
  };
}

/** 依存関係でつながるタスク群を互いに独立した系列へ分割する。 */
function splitDependencyGraphComponents(nodes, edges) {
  // 辺を双方向にたどり、弱連結成分ごとに独立した表示行を作る。
  const nodeMap = new Map();
  const neighborMap = new Map();
  for (const node of nodes) {
    nodeMap.set(node.identifier, node);
    neighborMap.set(node.identifier, []);
  }
  for (const edge of edges) {
    neighborMap.get(edge.sourceIdentifier)?.push(edge.targetIdentifier);
    neighborMap.get(edge.targetIdentifier)?.push(edge.sourceIdentifier);
  }

  const visitedIdentifiers = new Set();
  const components = [];
  const orderedNodes = [...nodes].sort(compareDependencyNodes);
  for (const startingNode of orderedNodes) {
    if (visitedIdentifiers.has(startingNode.identifier)) continue;
    const pendingIdentifiers = [startingNode.identifier];
    const componentIdentifiers = new Set();
    let pendingIdentifierIndex = 0;
    while (pendingIdentifierIndex < pendingIdentifiers.length) {
      // 配列先頭を削除せず、系列内のタスク数に比例する時間で探索する。
      const taskIdentifier = pendingIdentifiers[pendingIdentifierIndex];
      pendingIdentifierIndex += 1;
      if (visitedIdentifiers.has(taskIdentifier)) continue;
      visitedIdentifiers.add(taskIdentifier);
      componentIdentifiers.add(taskIdentifier);
      for (const neighborIdentifier of neighborMap.get(taskIdentifier) || []) {
        if (!visitedIdentifiers.has(neighborIdentifier)) pendingIdentifiers.push(neighborIdentifier);
      }
    }

    const componentNodes = [];
    for (const componentIdentifier of componentIdentifiers) {
      const componentNode = nodeMap.get(componentIdentifier);
      if (componentNode) componentNodes.push(componentNode);
    }
    const componentEdges = edges.filter(function selectComponentEdge(edge) {
      // 始点と終点が同じ系列に含まれる辺だけを残す。
      return componentIdentifiers.has(edge.sourceIdentifier)
        && componentIdentifiers.has(edge.targetIdentifier);
    });
    const componentLayout = calculateDependencyLevels(componentNodes, componentEdges);
    const incomingIdentifiers = new Set(componentEdges.map(function selectIncomingIdentifier(edge) {
      // 系列の先頭タスクを判定するため終点IDを集約する。
      return edge.targetIdentifier;
    }));
    const rootNodes = componentNodes
      .filter(function selectRootNode(node) {
        // 先行タスクを持たないノードだけを系列名の候補にする。
        return !incomingIdentifiers.has(node.identifier);
      })
      .sort(compareDependencyNodes);
    const representativeNode = rootNodes[0] || componentNodes.sort(compareDependencyNodes)[0];
    components.push({
      nodes: componentNodes,
      edges: componentEdges,
      levels: componentLayout.levels,
      representativeNode
    });
  }
  return components.sort(function sortDependencyComponents(firstComponent, secondComponent) {
    // 系列の表示順を代表タスクの期限と名称で安定させる。
    return compareDependencyNodes(firstComponent.representativeNode, secondComponent.representativeNode);
  });
}

/** 依存グラフ内で実際に循環しているタスクIDを検出する。 */
function findDependencyCycleIdentifiers(nodes, edges) {
  // Tarjan法で強連結成分を求め、複数要素または自己参照だけを循環と判定する。
  const successorMap = new Map();
  for (const node of nodes) successorMap.set(node.identifier, []);
  for (const edge of edges) {
    successorMap.get(edge.sourceIdentifier)?.push(edge.targetIdentifier);
  }
  let traversalIndex = 0;
  const traversalIndexMap = new Map();
  const lowestReachableIndexMap = new Map();
  const activeStack = [];
  const activeIdentifiers = new Set();
  const cycleIdentifiers = new Set();

  /** 1つのノードから深さ優先探索して強連結成分を確定する。 */
  function inspectDependencyNode(taskIdentifier) {
    // 探索順と到達可能な最小順を記録して循環成分を切り出す。
    traversalIndexMap.set(taskIdentifier, traversalIndex);
    lowestReachableIndexMap.set(taskIdentifier, traversalIndex);
    traversalIndex += 1;
    activeStack.push(taskIdentifier);
    activeIdentifiers.add(taskIdentifier);

    for (const successorIdentifier of successorMap.get(taskIdentifier) || []) {
      if (!traversalIndexMap.has(successorIdentifier)) {
        inspectDependencyNode(successorIdentifier);
        lowestReachableIndexMap.set(
          taskIdentifier,
          Math.min(
            lowestReachableIndexMap.get(taskIdentifier),
            lowestReachableIndexMap.get(successorIdentifier)));
      } else if (activeIdentifiers.has(successorIdentifier)) {
        lowestReachableIndexMap.set(
          taskIdentifier,
          Math.min(
            lowestReachableIndexMap.get(taskIdentifier),
            traversalIndexMap.get(successorIdentifier)));
      }
    }

    if (lowestReachableIndexMap.get(taskIdentifier) !== traversalIndexMap.get(taskIdentifier)) return;
    const componentIdentifiers = [];
    let componentIdentifier = "";
    do {
      componentIdentifier = activeStack.pop();
      activeIdentifiers.delete(componentIdentifier);
      componentIdentifiers.push(componentIdentifier);
    } while (componentIdentifier !== taskIdentifier);
    const hasSelfDependency = componentIdentifiers.length === 1
      && (successorMap.get(componentIdentifiers[0]) || []).includes(componentIdentifiers[0]);
    if (componentIdentifiers.length > 1 || hasSelfDependency) {
      for (const cycleIdentifier of componentIdentifiers) cycleIdentifiers.add(cycleIdentifier);
    }
  }

  for (const node of nodes) {
    if (!traversalIndexMap.has(node.identifier)) inspectDependencyNode(node.identifier);
  }
  return cycleIdentifiers;
}

/** 依存関係を左から右へ並べる段階へ変換する。 */
function calculateDependencyLevels(nodes, edges) {
  // Kahn法で入次数ゼロから順に段階を付け、循環の影響範囲は最後へまとめる。
  const incomingCountMap = new Map();
  const successorMap = new Map();
  const levelMap = new Map();
  const nodeMap = new Map();
  for (const node of nodes) {
    incomingCountMap.set(node.identifier, 0);
    successorMap.set(node.identifier, []);
    levelMap.set(node.identifier, 0);
    nodeMap.set(node.identifier, node);
  }
  for (const edge of edges) {
    incomingCountMap.set(edge.targetIdentifier, (incomingCountMap.get(edge.targetIdentifier) || 0) + 1);
    successorMap.get(edge.sourceIdentifier)?.push(edge.targetIdentifier);
  }

  const readyIdentifiers = nodes
    .filter(function selectReadyNode(node) {
      // 先行タスクがないノードを最初の処理段階へ入れる。
      return incomingCountMap.get(node.identifier) === 0;
    })
    .sort(compareDependencyNodes)
    .map(function selectReadyIdentifier(node) {
      // キュー操作用のIDへ変換する。
      return node.identifier;
    });
  const processedIdentifiers = new Set();
  while (readyIdentifiers.length > 0) {
    const taskIdentifier = readyIdentifiers.shift();
    processedIdentifiers.add(taskIdentifier);
    for (const successorIdentifier of successorMap.get(taskIdentifier) || []) {
      // 複数の先行タスクがある場合は最も遅い段階の次へ配置する。
      levelMap.set(
        successorIdentifier,
        Math.max(levelMap.get(successorIdentifier) || 0, (levelMap.get(taskIdentifier) || 0) + 1));
      const remainingIncomingCount = (incomingCountMap.get(successorIdentifier) || 0) - 1;
      incomingCountMap.set(successorIdentifier, remainingIncomingCount);
      if (remainingIncomingCount === 0) {
        readyIdentifiers.push(successorIdentifier);
        readyIdentifiers.sort(function sortReadyIdentifiers(firstIdentifier, secondIdentifier) {
          // 同じ段階では期限と名称が安定する順序で並べる。
          return compareDependencyNodes(nodeMap.get(firstIdentifier), nodeMap.get(secondIdentifier));
        });
      }
    }
  }

  const unresolvedIdentifiers = new Set();
  let maximumResolvedLevel = 0;
  for (const node of nodes) {
    if (processedIdentifiers.has(node.identifier)) {
      maximumResolvedLevel = Math.max(maximumResolvedLevel, levelMap.get(node.identifier) || 0);
    } else {
      unresolvedIdentifiers.add(node.identifier);
    }
  }
  if (unresolvedIdentifiers.size > 0) {
    for (const unresolvedIdentifier of unresolvedIdentifiers) {
      levelMap.set(unresolvedIdentifier, maximumResolvedLevel + 1);
    }
  }

  const groupedLevels = new Map();
  for (const node of nodes) {
    const levelNumber = levelMap.get(node.identifier) || 0;
    const levelNodes = groupedLevels.get(levelNumber) || [];
    levelNodes.push(node);
    groupedLevels.set(levelNumber, levelNodes);
  }
  const levels = Array.from(groupedLevels.entries())
    .sort(function sortLevels(firstEntry, secondEntry) {
      // 処理段階を左から右へ昇順で並べる。
      return firstEntry[0] - secondEntry[0];
    })
    .map(function createLevel(entry) {
      // 各段階のタスクを期限と名称で安定して並べる。
      return {
        levelNumber: entry[0],
        nodes: entry[1].sort(compareDependencyNodes),
        isUnresolved: entry[1].some(function containsUnresolvedNode(node) {
          // 順序未確定ノードを含む段階か判定する。
          return unresolvedIdentifiers.has(node.identifier);
        })
      };
    });
  return { levels, unresolvedIdentifiers };
}

/** 同じ処理段階のタスクを期限と名称で安定して比較する。 */
function compareDependencyNodes(firstNode, secondNode) {
  // 期限ありを先にし、同期限または無期限では名称とIDを使う。
  const firstDeadlineTimestamp = Date.parse(firstNode?.deadlineAt) || Number.MAX_SAFE_INTEGER;
  const secondDeadlineTimestamp = Date.parse(secondNode?.deadlineAt) || Number.MAX_SAFE_INTEGER;
  if (firstDeadlineTimestamp !== secondDeadlineTimestamp) {
    return firstDeadlineTimestamp - secondDeadlineTimestamp;
  }
  const titleComparison = String(firstNode?.title || "").localeCompare(String(secondNode?.title || ""), "ja");
  return titleComparison !== 0
    ? titleComparison
    : String(firstNode?.identifier || "").localeCompare(String(secondNode?.identifier || ""), "ja");
}

/** 系列を代表する親タスク名を返す。 */
function getDependencyComponentParentTitle(component) {
  // 先頭タスクの親を優先し、なければ系列内で最も多く参照される親を選ぶ。
  if (component.representativeNode?.parentTitle) return component.representativeNode.parentTitle;
  const parentTitleCounts = new Map();
  for (const node of component.nodes) {
    if (!node.parentTitle) continue;
    parentTitleCounts.set(node.parentTitle, (parentTitleCounts.get(node.parentTitle) || 0) + 1);
  }
  const orderedParentTitles = Array.from(parentTitleCounts.entries()).sort(function compareParentTitleCounts(firstEntry, secondEntry) {
    // 参照数の多い親を先にし、同数なら名称で表示を安定させる。
    return secondEntry[1] - firstEntry[1] || firstEntry[0].localeCompare(secondEntry[0], "ja");
  });
  return orderedParentTitles[0]?.[0] || component.representativeNode?.title || "親タスク未設定";
}

/** 依存グラフを画面描画用HTMLと件数へ変換する。 */
function createDependencyGraphPresentation(primaryTasks, availableTasks, excludedTaskIdentifiers = new Set(), allowsTaskEditing = false) {
  // タスクノード、段階、矢印を一意なDOM識別子で結び付ける。
  const graph = buildDependencyGraph(primaryTasks, availableTasks, excludedTaskIdentifiers);
  if (graph.edges.length === 0) return null;
  applicationState.dependencyGraphSequence += 1;
  const graphIdentifier = `dependency-graph-${applicationState.dependencyGraphSequence}`;
  const nodeElementIdentifierMap = new Map();
  const graphNodeMap = new Map();
  graph.nodes.forEach(function assignNodeElementIdentifier(node, nodeIndex) {
    // 生のタスクIDをDOM IDへ使わず安全な連番へ対応付ける。
    nodeElementIdentifierMap.set(node.identifier, `${graphIdentifier}-node-${nodeIndex}`);
    graphNodeMap.set(node.identifier, node);
  });

  let componentMarkup = "";
  graph.components.forEach(function renderDependencyComponent(component, componentIndex) {
    // 無関係な系列ごとに矢印と横スクロール領域を独立させる。
    const componentGraphIdentifier = `${graphIdentifier}-component-${componentIndex}`;
    let levelMarkup = "";
    component.levels.forEach(function renderDependencyLevel(level, levelIndex) {
      // 同じ系列内で同時に開始できるタスクだけを同じ縦列へまとめる。
      let nodeMarkup = "";
      for (const node of level.nodes) {
        const nodeClasses = ["dependency-node"];
        if (node.status === "実行中") nodeClasses.push("running");
        if (node.status === "完了") nodeClasses.push("completed");
        if (node.isOutsideScope) nodeClasses.push("outside-scope");
        if (node.isMissing) nodeClasses.push("missing");
        if (node.isCycle) nodeClasses.push("cycle");
        const warningText = node.isMissing
          ? "参照切れ"
          : node.isCycle
            ? "循環依存"
            : node.isUnresolved
              ? "循環の影響で順序未確定"
              : "";
        const durationText = node.status !== "完了"
          && node.remainingMinutes !== null && node.remainingMinutes !== undefined
          && Number.isFinite(Number(node.remainingMinutes))
          ? `${Number(node.remainingMinutes)}分`
          : "";
        const editButtonMarkup = allowsTaskEditing && !node.isMissing
          ? `<button type="button" class="dependency-node-edit-button" data-dependency-task-edit="${escapeAttribute(node.identifier)}" aria-label="${escapeAttribute(`${node.title}を編集`)}">編集</button>`
          : "";
        const identifierMarkup = allowsTaskEditing ? "" : `<small>${escapeHtml(node.identifier)}</small>`;
        nodeMarkup += `<article id="${escapeAttribute(nodeElementIdentifierMap.get(node.identifier))}" class="${nodeClasses.join(" ")}" style="--project-color:${getProjectColor(node.projectIdentifier)}">
          <strong>${escapeHtml(node.title)}</strong>
          ${identifierMarkup}
          <div class="dependency-node-meta"><span>${escapeHtml(node.status)}</span>${durationText ? `<span>${escapeHtml(durationText)}</span>` : ""}${node.status !== "完了" && node.isOutsideScope && !node.isMissing ? "<span>絞り込み外の前提</span>" : ""}${editButtonMarkup}</div>
          ${warningText ? `<div class="dependency-node-warning">${escapeHtml(warningText)}</div>` : ""}
        </article>`;
      }
      levelMarkup += `<section class="dependency-level" data-dependency-level-index="${levelIndex}">${nodeMarkup}</section>`;
    });
    let componentEdgeMarkup = "";
    component.edges.forEach(function renderComponentDependencyEdge(edge) {
      // この系列内の先行タスクから後続タスクへ向く矢印だけを準備する。
      const sourceNode = graphNodeMap.get(edge.sourceIdentifier);
      const targetNode = graphNodeMap.get(edge.targetIdentifier);
      const hasWarning = Boolean(
        sourceNode?.isMissing || sourceNode?.isCycle || targetNode?.isCycle
        || sourceNode?.isUnresolved || targetNode?.isUnresolved);
      componentEdgeMarkup += `<path class="dependency-edge${hasWarning ? " warning" : ""}" data-source-node="${escapeAttribute(nodeElementIdentifierMap.get(edge.sourceIdentifier))}" data-target-node="${escapeAttribute(nodeElementIdentifierMap.get(edge.targetIdentifier))}" marker-end="url(#${componentGraphIdentifier}-${hasWarning ? "warning-arrow" : "arrow"})"></path>`;
    });
    let initialLevelIndex = component.levels.findIndex(function findFirstIncompleteLevel(level) {
      // 1件でも未完了のタスクを含む最初の段を表示開始位置にする。
      return level.nodes.some(function containsIncompleteNode(node) { return node.status !== "完了"; });
    });
    if (initialLevelIndex < 0) initialLevelIndex = Math.max(0, component.levels.length - 1);
    const componentTitle = getDependencyComponentParentTitle(component);
    const componentHeader = graph.components.length > 1
      ? `<div class="dependency-component-header"><strong>${escapeHtml(componentTitle)}</strong><small>${component.nodes.length}件・${component.levels.length}段階</small></div>`
      : "";
    componentMarkup += `<section class="dependency-component${graph.components.length === 1 ? " single" : ""}">
      ${componentHeader}
      <div class="dependency-component-scroll" data-initial-level-index="${initialLevelIndex}">
        <div class="dependency-graph-canvas" data-dependency-graph="${escapeAttribute(componentGraphIdentifier)}">
          <svg class="dependency-edges" aria-hidden="true">
            <defs>
              <marker id="${componentGraphIdentifier}-arrow" markerWidth="8" markerHeight="8" refX="7" refY="4" orient="auto"><path d="M0,0 L8,4 L0,8 Z" fill="#73937f"></path></marker>
              <marker id="${componentGraphIdentifier}-warning-arrow" markerWidth="8" markerHeight="8" refX="7" refY="4" orient="auto"><path d="M0,0 L8,4 L0,8 Z" fill="#a33b35"></path></marker>
            </defs>
            ${componentEdgeMarkup}
          </svg>
          <div class="dependency-graph-levels" style="grid-template-columns: repeat(${component.levels.length}, 240px)">${levelMarkup}</div>
        </div>
      </div>
    </section>`;
  });

  const warningMessages = [];
  const missingCount = graph.nodes.filter(function countMissingNode(node) {
    // 参照切れノードだけを警告件数へ含める。
    return node.isMissing;
  }).length;
  if (missingCount > 0) warningMessages.push(`参照先が見つからないタスクが${missingCount}件あります。`);
  if (graph.cycleIdentifiers.size > 0) warningMessages.push("循環依存があるため、赤いタスクの処理順を確定できません。");
  const affectedCount = Array.from(graph.unresolvedIdentifiers).filter(function countAffectedIdentifier(identifier) {
    // 循環そのものではないが影響を受けるタスクを数える。
    return !graph.cycleIdentifiers.has(identifier);
  }).length;
  if (affectedCount > 0) warningMessages.push(`循環の影響で後続${affectedCount}件も順序未確定です。`);

  const graphMarkup = `<div class="dependency-components">${componentMarkup}</div>
    ${warningMessages.length > 0 ? `<div class="dependency-graph-warning">${escapeHtml(warningMessages.join(" "))}</div>` : ""}`;
  return {
    graphMarkup,
    nodeCount: graph.nodes.length,
    edgeCount: graph.edges.length,
    componentCount: graph.components.length
  };
}

/** 依存関係パネルの共通見出しと説明を組み立てる。 */
function createDependencyPanelContent(title, presentation) {
  // 見れば分かる件数や読み方を省き、短い見出しと図だけを表示する。
  return `<summary><span class="dependency-panel-title"><strong>${escapeHtml(title)}</strong></span></summary>
    ${presentation.graphMarkup}`;
}

/** 系列ごとの横スクロール、初期位置、ドラッグ操作を設定する。 */
function initializeDependencyGraphScrolling(rootElement, resetsScrollPosition) {
  // 新しく描画された系列へ一度だけポインター操作を接続する。
  const scrollContainers = Array.from(rootElement.querySelectorAll(".dependency-component-scroll"));
  for (const scrollContainer of scrollContainers) {
    if (scrollContainer.dataset.dragScrollingInitialized !== "true") {
      attachHorizontalDragScrolling(scrollContainer);
    }
  }
  window.requestAnimationFrame(function positionDependencyComponents() {
    // 非表示中は幅を確定できないため、開いた時または画面移動後まで初期化を保留する。
    const visibleScrollContainers = scrollContainers.filter(function selectVisibleScrollContainer(scrollContainer) {
      return scrollContainer.offsetParent !== null;
    });
    if (visibleScrollContainers.length === 0) return;
    for (const scrollContainer of visibleScrollContainers) {
      if (!resetsScrollPosition) continue;
      const initialLevelIndex = Number(scrollContainer.dataset.initialLevelIndex || 0);
      const targetLevel = scrollContainer.querySelector(`[data-dependency-level-index="${initialLevelIndex}"]`);
      if (!targetLevel) continue;
      scrollContainer.dataset.initializingScroll = "true";
      scrollContainer.scrollLeft = Math.max(0, targetLevel.offsetLeft - 12);
      window.requestAnimationFrame(function finishDependencyScrollInitialization() {
        // 初期移動では操作中スクロールバーを表示しない。
        delete scrollContainer.dataset.initializingScroll;
      });
    }
    if (rootElement.id === "task-dependency-panel" && resetsScrollPosition) {
      applicationState.dependencyGraphScrollShouldReset = false;
    }
    scheduleDependencyGraphDrawing();
  });
}

/** 横スクロール領域を背景ドラッグで移動できるようにする。 */
function attachHorizontalDragScrolling(scrollContainer) {
  // 押下位置と開始スクロール量を保持してポインター移動を横スクロールへ変換する。
  if (scrollContainer.dataset.dragScrollingInitialized === "true") return;
  scrollContainer.dataset.dragScrollingInitialized = "true";
  let activePointerIdentifier = null;
  let startingHorizontalPosition = 0;
  let startingScrollPosition = 0;
  let hasDragged = false;
  let suppressesNextClick = false;
  scrollContainer.addEventListener("pointerdown", function beginHorizontalDrag(event) {
    if (event.button !== 0) return;
    // 編集ボタン・締め切り・作業ログの押下では横スクロールを開始せず、通常のクリックを優先する。
    if (event.target.closest("[data-dependency-task-edit], [data-deadline-task], [data-time-entry]")) return;
    activePointerIdentifier = event.pointerId;
    startingHorizontalPosition = event.clientX;
    startingScrollPosition = scrollContainer.scrollLeft;
    hasDragged = false;
    // 押しただけでは捕捉せず、子ボタンへ通常のクリックを届ける。
    if (scrollContainer.id !== "navigation") showHorizontalScrollbarTemporarily(scrollContainer);
  });
  scrollContainer.addEventListener("pointermove", function moveHorizontalDrag(event) {
    if (activePointerIdentifier !== event.pointerId) return;
    const horizontalDistance = event.clientX - startingHorizontalPosition;
    if (!hasDragged && Math.abs(horizontalDistance) < 3) return;
    if (!hasDragged) scrollContainer.setPointerCapture(event.pointerId);
    hasDragged = true;
    scrollContainer.classList.add("dragging");
    scrollContainer.scrollLeft = startingScrollPosition - horizontalDistance;
    event.preventDefault();
  });
  const finishHorizontalDrag = function finishHorizontalDrag(event) {
    // 捕捉中のポインターだけを終了し、通常カーソルへ戻す。
    if (activePointerIdentifier !== event.pointerId) return;
    if (scrollContainer.hasPointerCapture(event.pointerId)) {
      scrollContainer.releasePointerCapture(event.pointerId);
    }
    activePointerIdentifier = null;
    scrollContainer.classList.remove("dragging");
    suppressesNextClick = hasDragged;
    // ナビゲーションは実際に横位置が変化した操作だけ表示時間を延長する。
    if (scrollContainer.id !== "navigation" || (hasDragged && scrollContainer.classList.contains("scrollbar-active"))) showHorizontalScrollbarTemporarily(scrollContainer);
    window.setTimeout(function clearClickSuppression() {
      // 背景上でドラッグした場合も次の通常クリックへ抑止状態を残さない。
      suppressesNextClick = false;
    }, 0);
  };
  scrollContainer.addEventListener("pointerup", finishHorizontalDrag);
  scrollContainer.addEventListener("pointercancel", finishHorizontalDrag);
  scrollContainer.addEventListener("click", function suppressClickAfterDrag(event) {
    // 予定や作業ログ上からドラッグした場合はクリック操作として扱わない。
    if (!suppressesNextClick) return;
    suppressesNextClick = false;
    event.preventDefault();
    event.stopPropagation();
  }, true);
  scrollContainer.addEventListener("wheel", function showScrollbarForWheel() {
    // ホイールやShift+ホイール操作中も現在位置を確認できるようにする。
    if (scrollContainer.id !== "navigation") showHorizontalScrollbarTemporarily(scrollContainer);
  }, { passive: true });
  scrollContainer.addEventListener("scroll", function showScrollbarForScroll() {
    // 初期位置調整を除く実操作時だけスクロールバーを短時間表示する。
    if (scrollContainer.dataset.initializingScroll !== "true") {
      showHorizontalScrollbarTemporarily(scrollContainer);
    }
  }, { passive: true });
  scrollContainer.addEventListener("dragstart", function preventNativeHorizontalDrag(event) {
    // ブラウザ標準の画像・文字ドラッグを抑えて横移動を優先する。
    event.preventDefault();
  });
}

/** 操作対象領域のスクロールバーを一定時間だけ表示する。 */
function showHorizontalScrollbarTemporarily(scrollContainer) {
  // 連続操作では非表示タイマーを延長する。
  const previousTimerIdentifier = horizontalScrollbarHideTimers.get(scrollContainer);
  if (previousTimerIdentifier) window.clearTimeout(previousTimerIdentifier);
  scrollContainer.classList.add("scrollbar-active");
  const timerIdentifier = window.setTimeout(function hideHorizontalScrollbar() {
    if (!scrollContainer.classList.contains("dragging")) {
      scrollContainer.classList.remove("scrollbar-active");
    }
    horizontalScrollbarHideTimers.delete(scrollContainer);
  }, 900);
  horizontalScrollbarHideTimers.set(scrollContainer, timerIdentifier);
}

/** 表示中の全依存関係図について矢印描画を予約する。 */
function scheduleDependencyGraphDrawing() {
  // 連続した再描画要求を次の1フレームへまとめてレイアウトずれを防ぐ。
  if (applicationState.dependencyGraphDrawingFrameIdentifier !== null) {
    window.cancelAnimationFrame(applicationState.dependencyGraphDrawingFrameIdentifier);
  }
  applicationState.dependencyGraphDrawingFrameIdentifier = window.requestAnimationFrame(drawDependencyGraphs);
}

/** 開かれた依存関係パネルの矢印を再描画する。 */
function handleDependencyPanelToggle(event) {
  // detailsを開いた直後だけ非表示中だった座標を計算し直す。
  if (event.target.matches(".dependency-panel") && event.target.open) {
    initializeDependencyGraphScrolling(
      event.target,
      event.target.id === "task-dependency-panel" && applicationState.dependencyGraphScrollShouldReset);
    scheduleDependencyGraphDrawing();
  }
}

/** 現在表示可能な依存関係図をすべて描画する。 */
function drawDependencyGraphs() {
  // 非表示ビューを除外し、表示中のキャンバスだけを測定する。
  applicationState.dependencyGraphDrawingFrameIdentifier = null;
  for (const graphCanvas of document.querySelectorAll(".dependency-graph-canvas")) {
    if (graphCanvas.offsetParent !== null) drawDependencyGraph(graphCanvas);
  }
}

/** 1つの依存関係図でノード間の矢印座標を設定する。 */
function drawDependencyGraph(graphCanvas) {
  // DOM上の実座標を使い、分岐と合流にも追従するベジェ曲線を生成する。
  const edgeCanvas = graphCanvas.querySelector(".dependency-edges");
  if (!edgeCanvas) return;
  const canvasWidth = Math.max(graphCanvas.scrollWidth, graphCanvas.clientWidth);
  const canvasHeight = Math.max(graphCanvas.scrollHeight, graphCanvas.clientHeight);
  edgeCanvas.setAttribute("viewBox", `0 0 ${canvasWidth} ${canvasHeight}`);
  edgeCanvas.style.width = `${canvasWidth}px`;
  edgeCanvas.style.height = `${canvasHeight}px`;
  const canvasRectangle = graphCanvas.getBoundingClientRect();
  for (const edgePath of edgeCanvas.querySelectorAll(".dependency-edge")) {
    const sourceNode = document.getElementById(edgePath.dataset.sourceNode);
    const targetNode = document.getElementById(edgePath.dataset.targetNode);
    if (!sourceNode || !targetNode) continue;
    const sourceRectangle = sourceNode.getBoundingClientRect();
    const targetRectangle = targetNode.getBoundingClientRect();
    const startHorizontalPosition = sourceRectangle.right - canvasRectangle.left;
    const startVerticalPosition = sourceRectangle.top + sourceRectangle.height / 2 - canvasRectangle.top;
    const endHorizontalPosition = targetRectangle.left - canvasRectangle.left;
    const endVerticalPosition = targetRectangle.top + targetRectangle.height / 2 - canvasRectangle.top;
    let pathData = "";
    if (endHorizontalPosition > startHorizontalPosition) {
      // 段階をまたぐ辺は横方向の距離に比例した滑らかな曲線にする。
      const controlDistance = Math.max(34, (endHorizontalPosition - startHorizontalPosition) * 0.42);
      pathData = `M ${startHorizontalPosition} ${startVerticalPosition} C ${startHorizontalPosition + controlDistance} ${startVerticalPosition}, ${endHorizontalPosition - controlDistance} ${endVerticalPosition}, ${endHorizontalPosition} ${endVerticalPosition}`;
    } else {
      // 循環など同じ段階へ戻る辺はノード右側を回り込ませる。
      const detourHorizontalPosition = Math.max(startHorizontalPosition, endHorizontalPosition) + 44;
      pathData = `M ${startHorizontalPosition} ${startVerticalPosition} C ${detourHorizontalPosition} ${startVerticalPosition}, ${detourHorizontalPosition} ${endVerticalPosition}, ${endHorizontalPosition} ${endVerticalPosition}`;
    }
    edgePath.setAttribute("d", pathData);
  }
}

/** 下書きバッチをユーザー確認後に承認する。 */
async function approveDraftBatch(batchIdentifier) {
  // 誤承認を防ぐため画面上で最終確認する。
  if (!window.confirm("表示中の下書きを優先順位へ反映しますか？")) return;
  try {
    const result = await apiRequest(`/api/v1/draft-batches/${encodeURIComponent(batchIdentifier)}/approve`, { method: "POST", body: "{}" });
    showNotice(`${result.approvedCount}件の下書きを承認しました。`);
    await Promise.all([loadTasks(), loadDashboard()]);
  } catch (error) {
    showNotice(error.message, true);
  }
}

/** タスク追加・編集ダイアログを初期化する。 */
function initializeTaskDialog() {
  // 開閉、追加、保存の操作を接続する。
  document.getElementById("new-task").addEventListener("click", function handleNewTaskClick() { openTaskDialog(); });
  document.getElementById("close-task-dialog").addEventListener("click", closeTaskDialog);
  document.getElementById("cancel-task-dialog").addEventListener("click", closeTaskDialog);
  document.getElementById("task-form").addEventListener("submit", saveTaskFromDialog);
  document.querySelector("#task-form [name='status']").addEventListener("change", updateTaskDialogStartButton);
  document.querySelector("#task-form [name='deadlineAt']").addEventListener("change", function handleDeadlineChange(event) {
    // 日時を入力した場合は個別指定へ明示的に切り替える。
    if (event.target.value) {
      document.querySelector("#task-form [name='deadlineOrigin']").value = "explicit";
    }
  });
}

/** 状態選択肢をフィルターと編集フォームへ設定する。 */
function populateStatusSelectors() {
  // 2つの選択欄で同じ固定値を共有する。
  const filterSelect = document.getElementById("task-status-filter");
  const formSelect = document.querySelector("#task-form [name='status']");
  for (const status of taskStatuses) {
    filterSelect.insertAdjacentHTML("beforeend", `<option value="${escapeAttribute(status)}">${escapeHtml(status)}</option>`);
    formSelect.insertAdjacentHTML("beforeend", `<option value="${escapeAttribute(status)}">${escapeHtml(status)}</option>`);
  }
  renderCustomFilterOptions(
    "task-status-filter",
    [{ value: "", label: "すべての状態", color: null }].concat(
      taskStatuses.map(function createStatusFilterOption(status) {
        return { value: status, label: status, color: null };
      })),
    false);
}

/** 新規または既存タスクを編集ダイアログへ設定する。 */
function openTaskDialog(taskIdentifier = null) {
  // 既存タスクは全項目を復元し、新規時は既定値へ戻す。
  const dialog = document.getElementById("task-dialog");
  const form = document.getElementById("task-form");
  form.reset();
  document.getElementById("task-dialog-title").textContent = taskIdentifier ? "タスクを編集" : "タスクを追加";
  if (taskIdentifier) {
    const task = applicationState.tasks.find(function findTask(item) { return item.identifier === taskIdentifier; });
    if (!task) return;
    for (const [fieldName, fieldValue] of Object.entries(task)) {
      const field = form.elements.namedItem(fieldName);
      if (!field) continue;
      if (field.type === "checkbox") field.checked = Boolean(fieldValue);
      else if (field.type === "datetime-local") field.value = toLocalInput(fieldValue);
      else field.value = Array.isArray(fieldValue) ? fieldValue.join(",") : (fieldValue ?? "");
    }
  } else {
    form.elements.namedItem("status").value = "実行可能";
    form.elements.namedItem("deadlineType").value = "目安";
    form.elements.namedItem("deadlineOrigin").value = "auto";
    form.elements.namedItem("requiredContext").value = "PC";
    form.elements.namedItem("splittable").checked = true;
  }
  updateTaskDialogStartButton();
  dialog.showModal();
}

/** 編集対象の状態に合わせて開始ボタンを更新する。 */
function updateTaskDialogStartButton() {
  // 保存後に開始できる既存タスクだけで操作を有効にする。
  const form = document.getElementById("task-form");
  const startButton = document.getElementById("start-task-dialog");
  startButton.hidden = !form.elements.namedItem("identifier").value;
  startButton.disabled = form.elements.namedItem("status").value !== "実行可能";
}

/** タスク編集ダイアログを閉じる。 */
function closeTaskDialog() {
  // 保存せず現在のモーダルだけを閉じる。
  document.getElementById("task-dialog").close();
}

/** 編集フォームからタスクJSONを作成して保存する。 */
async function saveTaskFromDialog(event) {
  // 画面入力をAPIの型へ変換する。
  event.preventDefault();
  const form = event.currentTarget;
  if (form.dataset.saving === "true") return;
  const startsTask = event.submitter?.id === "start-task-dialog";
  const existingIdentifier = form.elements.namedItem("identifier").value;
  if (startsTask && (!existingIdentifier || form.elements.namedItem("status").value !== "実行可能")) return;
  const selectedDeadlineOrigin = form.elements.namedItem("deadlineOrigin").value;
  const enteredDeadlineAt = fromLocalInput(form.elements.namedItem("deadlineAt").value);
  const deadlineAt = selectedDeadlineOrigin === "none" ? null : enteredDeadlineAt;
  if (selectedDeadlineOrigin === "explicit" && !deadlineAt) {
    showNotice("個別指定の期限日時を入力してください。", true);
    return;
  }
  const task = {
    identifier: existingIdentifier,
    projectIdentifier: form.elements.namedItem("projectIdentifier").value || null,
    title: form.elements.namedItem("title").value,
    category: form.elements.namedItem("category").value,
    status: form.elements.namedItem("status").value,
    details: form.elements.namedItem("details").value,
    parentIdentifier: form.elements.namedItem("parentIdentifier").value || null,
    deadlineAt,
    deadlineType: selectedDeadlineOrigin === "none"
      ? "なし"
      : form.elements.namedItem("deadlineType").value,
    deadlineOrigin: deadlineAt && selectedDeadlineOrigin !== "project-default"
      ? "explicit"
      : selectedDeadlineOrigin,
    estimatedMinutes: Number(form.elements.namedItem("estimatedMinutes").value),
    remainingMinutes: Number(form.elements.namedItem("remainingMinutes").value),
    importance: Number(form.elements.namedItem("importance").value),
    earliestStartAt: fromLocalInput(form.elements.namedItem("earliestStartAt").value),
    dependencyIdentifiers: form.elements.namedItem("dependencyIdentifiers").value.split(/[,、\n]/).map(function trimIdentifier(value) { return value.trim(); }).filter(Boolean),
    requiredContext: form.elements.namedItem("requiredContext").value,
    completionCondition: form.elements.namedItem("completionCondition").value,
    splittable: form.elements.namedItem("splittable").checked,
    source: "画面"
  };
  try {
    form.dataset.saving = "true";
    await apiRequest(existingIdentifier ? `/api/v1/tasks/${encodeURIComponent(existingIdentifier)}` : "/api/v1/tasks", {
      method: existingIdentifier ? "PUT" : "POST",
      body: JSON.stringify(task)
    });
    closeTaskDialog();
    showNotice(existingIdentifier ? "タスクを更新しました。" : "タスクを追加しました。");
    await Promise.all([loadTasks(), loadDashboard()]);
    // 保存成功後だけ共通の活動切替確認を経由して開始する。
    if (startsTask) await executeTaskAction(existingIdentifier, "start");
  } catch (error) {
    showNotice(error.message, true);
  } finally {
    form.dataset.saving = "false";
  }
}

/** プロジェクト管理ダイアログと関連操作を初期化する。 */
function initializeProjectDialogs() {
  // 一覧、基本情報、表示色、別名、背景情報、アーカイブの操作を接続する。
  document.getElementById("new-project").addEventListener("click", function handleNewProjectClick() {
    openProjectDialog();
  });
  document.getElementById("close-project-dialog").addEventListener("click", closeProjectDialog);
  document.getElementById("cancel-project-dialog").addEventListener("click", closeProjectDialog);
  document.getElementById("project-form").addEventListener("submit", saveProjectFromDialog);
  document.getElementById("add-project-alias").addEventListener("click", addProjectAlias);
  document.getElementById("new-project-context").addEventListener("click", function handleNewContextClick() {
    const projectIdentifier = document.querySelector("#project-form [name='identifier']").value;
    openProjectContextDialog(projectIdentifier);
  });
  document.getElementById("preview-project-context").addEventListener("click", previewProjectContext);
  document.getElementById("archive-project").addEventListener("click", archiveCurrentProject);
  document.getElementById("close-project-context-dialog").addEventListener("click", closeProjectContextDialog);
  document.getElementById("cancel-project-context-dialog").addEventListener("click", closeProjectContextDialog);
  document.getElementById("project-context-form").addEventListener("submit", saveProjectContextFromDialog);
  document.getElementById("randomize-project-color").addEventListener("click", randomizeProjectColor);
  for (const colorInput of document.querySelectorAll("#project-form input[name^='color']")) {
    colorInput.addEventListener("input", updateProjectColorPreview);
  }
}

/** 全プロジェクトを取得して一覧とタスク選択肢を更新する。 */
async function loadProjects() {
  // アーカイブ済みも取得し、表示時にチェック状態で絞り込む。
  applicationState.projects = await apiRequest("/api/v1/projects?includeArchived=true");
  populateProjectSelectors();
  renderProjectList();
  if (applicationState.tasks.length > 0) {
    renderTaskTable();
  }
}

/** タスク画面と編集フォームのプロジェクト選択肢を更新する。 */
function populateProjectSelectors() {
  // 現在選択値を維持しつつ、フォームは有効プロジェクトだけを新規候補にする。
  const filterSelect = document.getElementById("task-project-filter");
  const taskProjectSelect = document.querySelector("#task-form [name='projectIdentifier']");
  const currentFilter = filterSelect.value;
  const currentTaskProject = taskProjectSelect.value;
  filterSelect.innerHTML = `<option value="">すべてのプロジェクト</option>
    <option value="${taskCategoryFilterPrefix}仕事">仕事</option>
    <option value="${taskCategoryFilterPrefix}私用">私用</option>
    <option value="${unassignedProjectFilterValue}">プロジェクト未設定</option>`;
  taskProjectSelect.innerHTML = `<option value="">なし</option>`;
  for (const project of applicationState.projects) {
    const optionLabel = `${project.canonicalName}${project.status === "アーカイブ" ? "（アーカイブ）" : ""}`;
    const projectColor = getProjectColor(project.identifier);
    filterSelect.insertAdjacentHTML(
      "beforeend",
      `<option value="${escapeAttribute(project.identifier)}">${escapeHtml(optionLabel)}</option>`);
    taskProjectSelect.insertAdjacentHTML(
      "beforeend",
      `<option value="${escapeAttribute(project.identifier)}" style="color:${projectColor}" ${project.status === "アーカイブ" ? "disabled" : ""}>● ${escapeHtml(optionLabel)}</option>`);
  }
  filterSelect.value = currentFilter;
  renderTaskProjectFilterOptions();
  taskProjectSelect.value = currentTaskProject;
  // 作業時間フィルターへ文字色とドット色を分離したプロジェクト候補を反映する。
  const timeProjectFilter = document.getElementById("time-project-filter");
  const currentTimeProject = timeProjectFilter.value;
  timeProjectFilter.innerHTML = `<option value="">すべてのプロジェクト</option>
    <option value="${unassignedTimeProjectFilterValue}">未割当</option>`;
  for (const project of applicationState.projects) {
    const optionLabel = `${project.canonicalName}${project.status === "アーカイブ" ? "（アーカイブ）" : ""}`;
    timeProjectFilter.insertAdjacentHTML(
      "beforeend",
      `<option value="${escapeAttribute(project.identifier)}">${escapeHtml(optionLabel)}</option>`);
  }
  timeProjectFilter.value = currentTimeProject;
  renderTimeProjectFilterOptions();

  // ダッシュボードへ区分とプロジェクトをまとめた推薦範囲を反映する。
  const dashboardRecommendationFilter = document.getElementById("dashboard-recommendation-filter");
  const currentDashboardFilter = dashboardRecommendationFilter.value;
  const dashboardFilterOptions = [
    { value: "", label: "すべて", color: null },
    { value: "category:仕事", label: "仕事", color: null },
    { value: "category:私用", label: "私用", color: null },
    ...applicationState.projects.map(function createDashboardProjectOption(project) {
      return {
        value: `project:${project.identifier}`,
        label: `${project.canonicalName}${project.status === "アーカイブ" ? "（アーカイブ）" : ""}`,
        color: getProjectColor(project.identifier)
      };
    })
  ];
  dashboardRecommendationFilter.innerHTML = dashboardFilterOptions.map(function renderNativeDashboardFilterOption(option) {
    return `<option value="${escapeAttribute(option.value)}">${escapeHtml(option.label)}</option>`;
  }).join("");
  dashboardRecommendationFilter.value = currentDashboardFilter;
  renderCustomFilterOptions("dashboard-recommendation-filter", dashboardFilterOptions, true);

  // 作業ログ編集フォームへネイティブのプロジェクト候補を反映する。
  const timeProjectSelectors = [document.querySelector("#time-entry-form [name='projectIdentifier']")].filter(Boolean);
  for (const selector of timeProjectSelectors) {
    const currentValue = selector.value;
    selector.innerHTML = `<option value="">未割当</option>`;
    for (const project of applicationState.projects) {
      const optionLabel = `${project.canonicalName}${project.status === "アーカイブ" ? "（アーカイブ）" : ""}`;
      const projectColor = getProjectColor(project.identifier);
      selector.insertAdjacentHTML(
        "beforeend",
        `<option value="${escapeAttribute(project.identifier)}" style="color:${projectColor}">● ${escapeHtml(optionLabel)}</option>`);
    }
    selector.value = currentValue;
  }

  // 自由活動フォームへ他画面と同じ色付きカスタム候補を反映する。
  const freeActivityProjectSelect = document.getElementById("free-activity-project");
  const currentFreeActivityProject = freeActivityProjectSelect.value;
  const freeActivityProjectOptions = [
    { value: "", label: "未割当", color: getProjectColor(null) },
    ...applicationState.projects.map(function createFreeActivityProjectOption(project) {
      return {
        value: project.identifier,
        label: `${project.canonicalName}${project.status === "アーカイブ" ? "（アーカイブ）" : ""}`,
        color: getProjectColor(project.identifier)
      };
    })
  ];
  freeActivityProjectSelect.innerHTML = freeActivityProjectOptions.map(function renderNativeFreeActivityProjectOption(option) {
    return `<option value="${escapeAttribute(option.value)}">${escapeHtml(option.label)}</option>`;
  }).join("");
  freeActivityProjectSelect.value = currentFreeActivityProject;
  renderCustomFilterOptions("free-activity-project", freeActivityProjectOptions, true);
}

/** タスク画面のプロジェクト絞り込み候補を描画する。 */
function renderTaskProjectFilterOptions() {
  // 区分候補は色なし、プロジェクト候補はドットだけへ保存色を適用する。
  const options = [
    { value: "", label: "すべてのプロジェクト", color: null },
    { value: `${taskCategoryFilterPrefix}仕事`, label: "仕事", color: null },
    { value: `${taskCategoryFilterPrefix}私用`, label: "私用", color: null },
    { value: unassignedProjectFilterValue, label: "プロジェクト未設定", color: getProjectColor(null) },
    ...applicationState.projects.map(function createProjectFilterOption(project) {
      return {
        value: project.identifier,
        label: `${project.canonicalName}${project.status === "アーカイブ" ? "（アーカイブ）" : ""}`,
        color: getProjectColor(project.identifier)
      };
    })
  ];
  renderCustomFilterOptions("task-project-filter", options, true);
}

/** 作業時間画面のプロジェクト絞り込み候補を描画する。 */
function renderTimeProjectFilterOptions() {
  // プロジェクト色はドットだけへ適用して名称を通常の文字色で表示する。
  const options = [
    { value: "", label: "すべてのプロジェクト", color: null },
    { value: unassignedTimeProjectFilterValue, label: "未割当", color: getProjectColor(null) },
    ...applicationState.projects.map(function createTimeProjectFilterOption(project) {
      return {
        value: project.identifier,
        label: `${project.canonicalName}${project.status === "アーカイブ" ? "（アーカイブ）" : ""}`,
        color: getProjectColor(project.identifier)
      };
    })
  ];
  renderCustomFilterOptions("time-project-filter", options, true);
}

/** 候補一覧を共通のカスタム絞り込みメニューへ描画する。 */
function renderCustomFilterOptions(selectIdentifier, options, showsColorDots) {
  // 保存済み選択値を維持し、状態欄ではドットを省略する。
  const filterSelect = document.getElementById(selectIdentifier);
  const menu = document.getElementById(`${selectIdentifier}-menu`);
  for (const nativeOption of filterSelect.options) {
    const option = options.find(function findOption(item) { return item.value === nativeOption.value; });
    nativeOption.dataset.filterColor = option?.color || "";
  }
  menu.innerHTML = options.map(function renderFilterOption(option) {
    const dotMarkup = showsColorDots
      ? `<span class="project-filter-option-dot${option.color ? "" : " empty"}"${option.color ? ` style="--project-color:${escapeAttribute(option.color)}"` : ""}></span>`
      : "";
    const selected = option.value === filterSelect.value;
    return `<button type="button" class="project-filter-option" role="option" aria-selected="${selected}" data-filter-value="${escapeAttribute(option.value)}">
      ${dotMarkup}<span>${escapeHtml(option.label)}</span>
    </button>`;
  }).join("");
  updateCustomFilterDisplay(selectIdentifier);
}

/** 選択中候補の名称と色をカスタム絞り込みボタンへ表示する。 */
function updateCustomFilterDisplay(selectIdentifier) {
  // 色付き選択欄ではドットだけを着色し、名称は通常の文字色に保つ。
  const filterSelect = document.getElementById(selectIdentifier);
  const selectedOption = filterSelect.options[filterSelect.selectedIndex];
  const selectedValue = filterSelect.value;
  const selectedColor = selectedOption?.dataset.filterColor || null;
  const dot = document.getElementById(`${selectIdentifier}-dot`);
  document.getElementById(`${selectIdentifier}-label`).textContent = selectedOption?.textContent || "";
  if (dot) {
    dot.classList.toggle("hidden", !selectedColor);
    if (selectedColor) dot.style.setProperty("--project-color", selectedColor);
  }
  for (const optionButton of document.querySelectorAll(`#${selectIdentifier}-menu [data-filter-value]`)) {
    optionButton.setAttribute("aria-selected", String(optionButton.dataset.filterValue === selectedValue));
  }
}

/** IDに一致する取得済みプロジェクトを返す。 */
function findProject(projectIdentifier) {
  // 未指定または不一致の場合はnullを返す。
  return applicationState.projects.find(function findProjectByIdentifier(project) {
    return project.identifier === projectIdentifier;
  }) || null;
}

/** 検索条件に一致するプロジェクトカードを表示する。 */
function renderProjectList() {
  // 正式名称、ID、別名とアーカイブ表示条件を組み合わせる。
  const projectContainer = document.getElementById("project-list");
  const searchText = document.getElementById("project-search").value.trim().toLowerCase();
  const showArchivedProjects = document.getElementById("show-archived-projects").checked;
  const visibleProjects = applicationState.projects.filter(function filterProject(project) {
    const matchesArchive = showArchivedProjects || project.status !== "アーカイブ";
    const searchableText = `${project.identifier} ${project.canonicalName} ${project.aliases.map(function getAliasText(alias) { return alias.aliasText; }).join(" ")}`.toLowerCase();
    return matchesArchive && (!searchText || searchableText.includes(searchText));
  });
  if (visibleProjects.length === 0) {
    projectContainer.innerHTML = `<div class="empty-state">該当するプロジェクトはありません。</div>`;
    return;
  }
  projectContainer.innerHTML = visibleProjects.map(function renderProjectCard(project) {
    const deadlineText = project.deadlineRule?.enabled
      ? `ISO曜日${project.deadlineRule.weekday} ${project.deadlineRule.localTime} · ${project.deadlineRule.deadlineType}`
      : "既定期限なし";
    return `<article class="project-card${project.status === "アーカイブ" ? " archived" : ""}" style="--project-color:${getProjectColor(project.identifier)}">
      <div><span class="task-category">${escapeHtml(project.status)}</span><h2>${escapeHtml(project.canonicalName)}</h2></div>
      <div class="project-card-meta"><span>${escapeHtml(project.identifier)}</span><span>別名 ${project.aliases.length}件</span><span>背景 ${project.contextDocuments.length}件</span></div>
      <div class="muted">${escapeHtml(deadlineText)}</div>
      <button class="secondary-button edit-project-button" data-project="${escapeAttribute(project.identifier)}">編集</button>
    </article>`;
  }).join("");
  for (const editButton of projectContainer.querySelectorAll(".edit-project-button")) {
    editButton.addEventListener("click", function handleEditProjectClick() {
      openProjectDialog(editButton.dataset.project);
    });
  }
}

/** 新規または既存プロジェクトを編集ダイアログへ設定する。 */
function openProjectDialog(projectIdentifier = null) {
  // 既存時は期限、別名、背景情報を復元し、新規時は基本欄だけを表示する。
  const dialog = document.getElementById("project-dialog");
  const form = document.getElementById("project-form");
  const relatedEditor = document.getElementById("project-related-editor");
  const archiveButton = document.getElementById("archive-project");
  form.reset();
  // hidden入力は値属性がreset後も残るため、新規作成へ編集対象IDを引き継がないよう明示的に消す。
  form.dataset.projectIdentifier = "";
  form.elements.namedItem("identifier").value = "";
  form.elements.namedItem("deadlineWeekday").value = "5";
  form.elements.namedItem("deadlineLocalTime").value = "17:00";
  form.elements.namedItem("deadlineType").value = "目安";
  document.getElementById("project-context-preview").textContent = "プレビュー更新を押してください。";
  document.getElementById("project-dialog-title").textContent = projectIdentifier
    ? "プロジェクトを編集"
    : "プロジェクトを追加";
  relatedEditor.classList.toggle("hidden", !projectIdentifier);
  archiveButton.classList.toggle("hidden", !projectIdentifier);
  if (projectIdentifier) {
    const project = findProject(projectIdentifier);
    if (!project) return;
    form.dataset.projectIdentifier = project.identifier;
    form.elements.namedItem("identifier").value = project.identifier;
    form.elements.namedItem("canonicalName").value = project.canonicalName;
    form.elements.namedItem("status").value = project.status;
    form.elements.namedItem("colorHue").value = String(project.colorHue);
    form.elements.namedItem("colorSaturation").value = String(project.colorSaturation);
    form.elements.namedItem("colorValue").value = String(project.colorValue);
    if (project.deadlineRule) {
      form.elements.namedItem("deadlineEnabled").checked = project.deadlineRule.enabled;
      form.elements.namedItem("deadlineWeekday").value = String(project.deadlineRule.weekday);
      form.elements.namedItem("deadlineLocalTime").value = project.deadlineRule.localTime;
      form.elements.namedItem("deadlineType").value = project.deadlineRule.deadlineType;
    }
    archiveButton.classList.toggle("hidden", project.status === "アーカイブ");
    renderProjectAliases(project);
    renderProjectContexts(project);
  } else {
    form.elements.namedItem("status").value = "有効";
    form.elements.namedItem("colorHue").value = String(Math.floor(Math.random() * 360));
    form.elements.namedItem("colorSaturation").value = "50";
    form.elements.namedItem("colorValue").value = "78";
    document.getElementById("project-alias-list").innerHTML = "";
    document.getElementById("project-context-list").innerHTML = "";
  }
  updateProjectColorPreview();
  dialog.showModal();
}

/** プロジェクト色のHSV値とプレビューを同期する。 */
function updateProjectColorPreview() {
  // スライダー値を数値表示とCSSプレビューへ同時に反映する。
  const form = document.getElementById("project-form");
  const hueValue = Number(form.elements.namedItem("colorHue").value);
  const saturationValue = Number(form.elements.namedItem("colorSaturation").value);
  const brightnessValue = Number(form.elements.namedItem("colorValue").value);
  document.getElementById("project-color-hue-output").textContent = String(hueValue);
  document.getElementById("project-color-saturation-output").textContent = String(saturationValue);
  document.getElementById("project-color-value-output").textContent = String(brightnessValue);
  document.getElementById("project-color-preview").style.setProperty(
    "--project-color",
    convertHsvToCssColor(hueValue, saturationValue, brightnessValue));
}

/** プロジェクト色の色相だけをランダムに変更する。 */
function randomizeProjectColor() {
  // 彩度と明度を維持し、0から359の色相を再選択する。
  const form = document.getElementById("project-form");
  form.elements.namedItem("colorHue").value = String(Math.floor(Math.random() * 360));
  updateProjectColorPreview();
}

/** プロジェクト編集ダイアログを閉じる。 */
function closeProjectDialog() {
  // 保存せず現在のプロジェクトモーダルだけを閉じる。
  document.getElementById("project-dialog").close();
}

/** プロジェクト基本情報と期限規則を保存する。 */
async function saveProjectFromDialog(event) {
  // 新規・編集の判定にはhidden入力へ残り得る値ではなく、ダイアログを開いた時の一時状態を使う。
  event.preventDefault();
  const form = event.currentTarget;
  const existingIdentifier = form.dataset.projectIdentifier || "";
  const project = {
    identifier: existingIdentifier,
    canonicalName: form.elements.namedItem("canonicalName").value,
    status: form.elements.namedItem("status").value,
    colorHue: Number(form.elements.namedItem("colorHue").value),
    colorSaturation: Number(form.elements.namedItem("colorSaturation").value),
    colorValue: Number(form.elements.namedItem("colorValue").value)
  };
  const deadlineRule = {
    weekday: Number(form.elements.namedItem("deadlineWeekday").value),
    localTime: form.elements.namedItem("deadlineLocalTime").value,
    deadlineType: form.elements.namedItem("deadlineType").value,
    enabled: form.elements.namedItem("deadlineEnabled").checked
  };
  try {
    const savedProject = await apiRequest(
      existingIdentifier
        ? `/api/v1/projects/${encodeURIComponent(existingIdentifier)}`
        : "/api/v1/projects",
      {
        method: existingIdentifier ? "PUT" : "POST",
        body: JSON.stringify(project)
      });
    await apiRequest(`/api/v1/projects/${encodeURIComponent(savedProject.identifier)}/deadline-rule`, {
      method: "PUT",
      body: JSON.stringify(deadlineRule)
    });
    closeProjectDialog();
    showNotice(existingIdentifier ? "プロジェクトを更新しました。" : "プロジェクトを追加しました。");
    await Promise.all([loadProjects(), loadTasks(), loadDashboard()]);
  } catch (error) {
    showNotice(error.message, true);
  }
}

/** 現在のプロジェクト別名をチップとして表示する。 */
function renderProjectAliases(project) {
  // 各別名へ解除ボタンを付けて正式名称とは区別する。
  const aliasContainer = document.getElementById("project-alias-list");
  aliasContainer.innerHTML = project.aliases.length === 0
    ? `<span class="muted">別名はありません。</span>`
    : project.aliases.map(function renderAlias(projectAlias) {
      return `<span class="alias-chip">${escapeHtml(projectAlias.aliasText)}<button type="button" class="remove-project-alias" data-alias="${projectAlias.identifier}" aria-label="別名を解除">×</button></span>`;
    }).join("");
  for (const removeButton of aliasContainer.querySelectorAll(".remove-project-alias")) {
    removeButton.addEventListener("click", async function handleRemoveAliasClick() {
      await removeProjectAlias(project.identifier, Number(removeButton.dataset.alias));
    });
  }
}

/** 入力された確認済み別名を現在のプロジェクトへ追加する。 */
async function addProjectAlias() {
  // あいまい候補を自動学習せず、画面で入力された文字だけを保存する。
  const projectIdentifier = document.querySelector("#project-form [name='identifier']").value;
  const aliasInput = document.getElementById("project-alias-input");
  const aliasText = aliasInput.value.trim();
  if (!projectIdentifier || !aliasText) return;
  try {
    await apiRequest(`/api/v1/projects/${encodeURIComponent(projectIdentifier)}/aliases`, {
      method: "POST",
      body: JSON.stringify({ aliasText })
    });
    aliasInput.value = "";
    await loadProjects();
    const project = findProject(projectIdentifier);
    if (project) renderProjectAliases(project);
    showNotice("プロジェクト別名を追加しました。");
  } catch (error) {
    showNotice(error.message, true);
  }
}

/** 指定されたプロジェクト別名を解除する。 */
async function removeProjectAlias(projectIdentifier, aliasIdentifier) {
  // ユーザー確認後に別名だけを削除し、プロジェクト本体は残す。
  if (!window.confirm("この別名を解除しますか？")) return;
  try {
    await apiRequest(`/api/v1/projects/${encodeURIComponent(projectIdentifier)}/aliases/${aliasIdentifier}`, {
      method: "DELETE"
    });
    await loadProjects();
    const project = findProject(projectIdentifier);
    if (project) renderProjectAliases(project);
    showNotice("プロジェクト別名を解除しました。");
  } catch (error) {
    showNotice(error.message, true);
  }
}

/** 現在のプロジェクト背景情報を一覧表示する。 */
function renderProjectContexts(project) {
  // 有効状態、優先度、有効期間を確認できる編集一覧を作る。
  const contextContainer = document.getElementById("project-context-list");
  if (project.contextDocuments.length === 0) {
    contextContainer.innerHTML = `<div class="muted">背景情報はありません。</div>`;
    return;
  }
  contextContainer.innerHTML = project.contextDocuments.map(function renderContextDocument(contextDocument) {
    const validityText = contextDocument.validFrom || contextDocument.validUntil
      ? `${contextDocument.validFrom ? formatDateTime(contextDocument.validFrom) : "指定なし"} ～ ${contextDocument.validUntil ? formatDateTime(contextDocument.validUntil) : "指定なし"}`
      : "期間指定なし";
    return `<div class="context-document${contextDocument.enabled ? "" : " disabled"}">
      <div><strong>${escapeHtml(contextDocument.title)}</strong><div class="muted">優先度${contextDocument.priority} · ${escapeHtml(validityText)}${contextDocument.enabled ? "" : " · 無効"}</div></div>
      <button type="button" class="text-button edit-project-context" data-context="${escapeAttribute(contextDocument.identifier)}">編集</button>
    </div>`;
  }).join("");
  for (const editButton of contextContainer.querySelectorAll(".edit-project-context")) {
    editButton.addEventListener("click", function handleEditContextClick() {
      openProjectContextDialog(project.identifier, editButton.dataset.context);
    });
  }
}

/** プロジェクト背景情報の追加・編集ダイアログを開く。 */
function openProjectContextDialog(projectIdentifier, contextIdentifier = null) {
  // 既存背景情報は全項目を復元し、新規時は安全な既定値を設定する。
  if (!projectIdentifier) return;
  const dialog = document.getElementById("project-context-dialog");
  const form = document.getElementById("project-context-form");
  form.reset();
  form.elements.namedItem("projectIdentifier").value = projectIdentifier;
  form.elements.namedItem("priority").value = "3";
  form.elements.namedItem("enabled").checked = true;
  document.getElementById("project-context-dialog-title").textContent = contextIdentifier
    ? "背景情報を編集"
    : "背景情報を追加";
  if (contextIdentifier) {
    const project = findProject(projectIdentifier);
    const contextDocument = project?.contextDocuments.find(function findContext(document) {
      return document.identifier === contextIdentifier;
    });
    if (!contextDocument) return;
    form.elements.namedItem("identifier").value = contextDocument.identifier;
    form.elements.namedItem("title").value = contextDocument.title;
    form.elements.namedItem("priority").value = String(contextDocument.priority);
    form.elements.namedItem("enabled").checked = contextDocument.enabled;
    form.elements.namedItem("validFrom").value = toLocalInput(contextDocument.validFrom);
    form.elements.namedItem("validUntil").value = toLocalInput(contextDocument.validUntil);
    form.elements.namedItem("contentMarkdown").value = contextDocument.contentMarkdown;
  }
  dialog.showModal();
}

/** プロジェクト背景情報ダイアログを閉じる。 */
function closeProjectContextDialog() {
  // 保存せず背景情報モーダルだけを閉じる。
  document.getElementById("project-context-dialog").close();
}

/** 背景情報フォームを追加または更新APIへ保存する。 */
async function saveProjectContextFromDialog(event) {
  // 日時、優先度、有効状態、Markdown本文を型付きJSONへ変換する。
  event.preventDefault();
  const form = event.currentTarget;
  const projectIdentifier = form.elements.namedItem("projectIdentifier").value;
  const contextIdentifier = form.elements.namedItem("identifier").value;
  const contextDocument = {
    identifier: contextIdentifier,
    title: form.elements.namedItem("title").value,
    contentMarkdown: form.elements.namedItem("contentMarkdown").value,
    priority: Number(form.elements.namedItem("priority").value),
    enabled: form.elements.namedItem("enabled").checked,
    validFrom: fromLocalInput(form.elements.namedItem("validFrom").value),
    validUntil: fromLocalInput(form.elements.namedItem("validUntil").value)
  };
  try {
    await apiRequest(
      contextIdentifier
        ? `/api/v1/projects/${encodeURIComponent(projectIdentifier)}/contexts/${encodeURIComponent(contextIdentifier)}`
        : `/api/v1/projects/${encodeURIComponent(projectIdentifier)}/contexts`,
      {
        method: contextIdentifier ? "PUT" : "POST",
        body: JSON.stringify(contextDocument)
      });
    closeProjectContextDialog();
    await loadProjects();
    const project = findProject(projectIdentifier);
    if (project) renderProjectContexts(project);
    showNotice(contextIdentifier ? "背景情報を更新しました。" : "背景情報を追加しました。");
  } catch (error) {
    showNotice(error.message, true);
  }
}

/** Codexへ渡す統合背景情報をプレビューする。 */
async function previewProjectContext() {
  // 名前解決を介さず不変IDでprepare APIを呼び出す。
  const projectIdentifier = document.querySelector("#project-form [name='identifier']").value;
  if (!projectIdentifier) return;
  try {
    const preparation = await apiRequest(`/api/v1/projects/prepare?reference=${encodeURIComponent(projectIdentifier)}`);
    document.getElementById("project-context-preview").textContent = preparation.contextMarkdown
      || "背景情報を生成できませんでした。";
  } catch (error) {
    showNotice(error.message, true);
  }
}

/** 現在編集中のプロジェクトをアーカイブする。 */
async function archiveCurrentProject() {
  // 関連タスクを残すことを説明して確認後に状態を変更する。
  const projectIdentifier = document.querySelector("#project-form [name='identifier']").value;
  if (!projectIdentifier || !window.confirm("このプロジェクトをアーカイブしますか？既存タスクとの関連は維持されます。")) return;
  try {
    await apiRequest(`/api/v1/projects/${encodeURIComponent(projectIdentifier)}/archive`, {
      method: "POST",
      body: "{}"
    });
    closeProjectDialog();
    await Promise.all([loadProjects(), loadTasks()]);
    showNotice("プロジェクトをアーカイブしました。");
  } catch (error) {
    showNotice(error.message, true);
  }
}

/** 作業タイマー、集計期間、作業ログ編集の画面操作を初期化する。 */
function initializeTimeTrackingActions() {
  // 自由活動ダイアログの開閉と開始を接続する。
  document.getElementById("close-free-activity-dialog").addEventListener("click", closeFreeActivityDialog);
  document.getElementById("cancel-free-activity-dialog").addEventListener("click", closeFreeActivityDialog);
  document.getElementById("free-activity-form").addEventListener("submit", startFreeActivity);

  // 作業ログ追加・編集ダイアログの操作を接続する。
  document.getElementById("new-time-entry").addEventListener("click", function handleNewTimeEntry() {
    openTimeEntryDialog();
  });
  document.getElementById("close-time-entry-dialog").addEventListener("click", closeTimeEntryDialog);
  document.getElementById("cancel-time-entry-dialog").addEventListener("click", closeTimeEntryDialog);
  document.getElementById("time-entry-form").addEventListener("submit", saveTimeEntry);
  document.getElementById("void-time-entry").addEventListener("click", voidCurrentTimeEntry);
  document.querySelector("#time-entry-form [name='taskIdentifier']").addEventListener("change", applySelectedTaskToTimeEntry);

  // 日・週・月と前後期間の切替を集計再読込へ接続する。
  for (const periodButton of document.querySelectorAll("#time-period-selector [data-period]")) {
    periodButton.addEventListener("click", async function handlePeriodChange() {
      applicationState.timeReportPeriod = periodButton.dataset.period;
      await loadTimeReport();
    });
  }
  document.getElementById("previous-time-period").addEventListener("click", async function handlePreviousPeriod() {
    moveTimeReportAnchor(-1);
    await loadTimeReport();
  });
  document.getElementById("today-time-period").addEventListener("click", async function handleTodayPeriod() {
    applicationState.timeReportAnchor = new Date();
    await loadTimeReport();
  });
  document.getElementById("next-time-period").addEventListener("click", async function handleNextPeriod() {
    // 現在期間より先へは移動しない。
    if (document.getElementById("next-time-period").disabled) return;
    moveTimeReportAnchor(1);
    await loadTimeReport();
  });
  document.getElementById("time-project-filter").addEventListener("change", loadTimeReport);
}

/** 自由活動開始ダイアログを表示する。 */
function openFreeActivityDialog() {
  // 前回入力を消し、現在の有効プロジェクト候補を利用する。
  const form = document.getElementById("free-activity-form");
  form.reset();
  updateCustomFilterDisplay("free-activity-project");
  closeCustomFilterMenus();
  document.getElementById("free-activity-dialog").showModal();
}

/** 自由活動開始ダイアログを閉じる。 */
function closeFreeActivityDialog() {
  // 保存せずモーダルだけを閉じる。
  document.getElementById("free-activity-dialog").close();
}

/** 自由活動タイマーをAPIから開始する。 */
async function startFreeActivity(event) {
  // 名称と任意プロジェクトを送り、現在のタイマー切替はサーバーへ任せる。
  event.preventDefault();
  const form = event.currentTarget;
  const request = {
    title: form.elements.namedItem("title").value,
    projectIdentifier: form.elements.namedItem("projectIdentifier").value || null
  };
  try {
    await apiRequest("/api/v1/time-entries/start", {
      method: "POST",
      body: JSON.stringify(request)
    });
    closeFreeActivityDialog();
    showNotice("自由活動の計測を開始しました。");
    await Promise.all([loadDashboard(), loadTasks()]);
    if (applicationState.activeView === "time") await loadTimeReport();
  } catch (error) {
    showNotice(error.message, true);
  }
}

/** 作業時間レポートと対象期間のログを取得する。 */
async function loadTimeReport() {
  // 選択期間、基準日、プロジェクト条件を同じクエリへ反映する。
  clampTimeReportAnchorToToday();
  const period = applicationState.timeReportPeriod;
  const anchor = formatLocalDate(applicationState.timeReportAnchor);
  const projectIdentifier = document.getElementById("time-project-filter").value;
  const projectQuery = projectIdentifier
    ? `&projectIdentifier=${encodeURIComponent(projectIdentifier)}`
    : "";
  const report = await apiRequest(
    `/api/v1/time-reports?period=${encodeURIComponent(period)}&anchor=${encodeURIComponent(anchor)}${projectQuery}`);
  const entries = await apiRequest(
    `/api/v1/time-entries?rangeStart=${encodeURIComponent(report.rangeStart)}&rangeEnd=${encodeURIComponent(report.rangeEnd)}${projectQuery}`);
  applicationState.timeReport = report;
  applicationState.timeEntries = entries;
  renderTimeReport(report, entries);
}

/** 取得した作業時間レポートをグラフ、表、ログ一覧へ描画する。 */
function renderTimeReport(report, entries) {
  // 期間選択、要約値、表示範囲を最新結果へ同期する。
  for (const periodButton of document.querySelectorAll("#time-period-selector [data-period]")) {
    periodButton.classList.toggle("active", periodButton.dataset.period === report.period);
  }
  document.getElementById("time-report-range").textContent = formatTimeReportRange(report);
  document.getElementById("next-time-period").disabled = isCurrentOrFutureTimeReportPeriod(report);
  document.getElementById("time-total-duration").textContent = formatDuration(report.totalSeconds);
  document.getElementById("time-review-count").textContent = `${report.reviewCount}件`;
  document.getElementById("time-overlap-count").textContent = `${report.overlapCount}件`;
  // 要確認と重複ログは、それぞれ件数がある場合だけカードを表示する。
  document.getElementById("time-review-count").closest(".time-summary-card").classList.toggle("hidden", report.reviewCount === 0);
  document.getElementById("time-overlap-count").closest(".time-summary-card").classList.toggle("hidden", report.overlapCount === 0);
  renderTimeReportChart(report);

  const projectSummaryBody = document.getElementById("time-project-summary");
  projectSummaryBody.innerHTML = report.projects.length === 0
    ? `<tr><td colspan="3" class="muted">集計対象はありません。</td></tr>`
    : report.projects.map(project => `<tr style="--project-color:${getProjectColor(project.projectIdentifier)}">
      <td><span class="project-color-dot"></span>${escapeHtml(project.projectName)}</td>
      <td>${formatDuration(project.totalSeconds)}</td>
      <td>${project.entryCount}件</td>
    </tr>`).join("");
  const taskSummaryBody = document.getElementById("time-task-summary");
  taskSummaryBody.innerHTML = report.tasks.length === 0
    ? `<tr><td colspan="3" class="muted">集計対象はありません。</td></tr>`
    : report.tasks.map(task => `<tr class="time-task-summary-row" style="--project-color:${getProjectColor(task.projectIdentifier)}">
      <td>${escapeHtml(task.taskTitle)}</td>
      <td>${escapeHtml(task.projectName)}</td>
      <td>${formatDuration(task.totalSeconds)}</td>
    </tr>`).join("");
  renderTimeEntryList(entries);
}

/** 作業時間レポートの期間を時刻なしの見出しへ整形する。 */
function formatTimeReportRange(report) {
  // APIの期間終了は排他的なので、表示用の最終日は1ミリ秒戻して求める。
  const rangeStart = new Date(report.rangeStart);
  const rangeEnd = new Date(new Date(report.rangeEnd).getTime() - 1);
  if (report.period === "day") {
    const dateText = new Intl.DateTimeFormat("ja-JP", { month: "long", day: "numeric" }).format(rangeStart);
    const weekdayText = new Intl.DateTimeFormat("ja-JP", { weekday: "short" }).format(rangeStart);
    const todayPrefix = formatLocalDate(rangeStart) === formatLocalDate(new Date()) ? "今日・" : "";
    return `${todayPrefix}${dateText}（${weekdayText}）`;
  }
  if (report.period === "month") {
    return new Intl.DateTimeFormat("ja-JP", { year: "numeric", month: "long" }).format(rangeStart);
  }

  // 週表示は同じ年を重複表示せず、年をまたぐ場合だけ両側へ年を付ける。
  const startFormatter = new Intl.DateTimeFormat("ja-JP", { year: "numeric", month: "long", day: "numeric" });
  const endFormatter = new Intl.DateTimeFormat("ja-JP", {
    year: rangeStart.getFullYear() === rangeEnd.getFullYear() ? undefined : "numeric",
    month: "long",
    day: "numeric"
  });
  return `${startFormatter.format(rangeStart)} ～ ${endFormatter.format(rangeEnd)}`;
}

/** 現在日を含む期間または未来期間かを判定する。 */
function isCurrentOrFutureTimeReportPeriod(report) {
  // 今日の開始より期間終了が後なら、次の期間は必ず未来になる。
  const currentDate = new Date();
  const todayStart = new Date(currentDate.getFullYear(), currentDate.getMonth(), currentDate.getDate());
  return new Date(report.rangeEnd).getTime() > todayStart.getTime();
}

/** 作業時間レポートの基準日が未来へ進まないよう今日へ補正する。 */
function clampTimeReportAnchorToToday() {
  // YYYY-MM-DD形式の比較で時刻を無視し、未来日の基準だけを今日へ戻す。
  const today = new Date();
  if (formatLocalDate(applicationState.timeReportAnchor) > formatLocalDate(today)) {
    applicationState.timeReportAnchor = today;
  }
}

/** プロジェクト積み上げ作業時間グラフを描画する。 */
function renderTimeReportChart(report) {
  // 最大時間枠をグラフ高100%として各プロジェクト秒数を積み上げる。
  const chartContainer = document.getElementById("time-report-chart");
  const maximumSeconds = Math.max(1, ...report.buckets.map(bucket => bucket.totalSeconds));
  chartContainer.innerHTML = report.buckets.map(function renderBucket(bucket, bucketIndex) {
    const segments = bucket.projects.map(project => {
      const heightPercentage = project.totalSeconds / maximumSeconds * 100;
      return `<div class="time-chart-segment" style="--entry-color:${getProjectColor(project.projectIdentifier)};height:${heightPercentage}%"
        title="${escapeAttribute(`${project.projectName} ${formatDuration(project.totalSeconds)}`)}"></div>`;
    }).join("");
    return `<div class="time-chart-column" title="${escapeAttribute(formatDuration(bucket.totalSeconds))}">
      ${segments}<span class="time-chart-label">${escapeHtml(formatTimeBucketLabel(bucket, report.period, bucketIndex))}</span>
    </div>`;
  }).join("");
  document.getElementById("time-report-legend").innerHTML = report.projects.map(project =>
    `<span><i style="--entry-color:${getProjectColor(project.projectIdentifier)}"></i>${escapeHtml(project.projectName)}</span>`
  ).join("");
}

/** グラフ時間枠の短いラベルを返す。 */
function formatTimeBucketLabel(bucket, period, bucketIndex) {
  // 日表示は間引いた時刻、週・月表示は日付を表示する。
  const startAt = new Date(bucket.startAt);
  if (period === "day") {
    return bucketIndex % 3 === 0 ? `${String(startAt.getHours()).padStart(2, "0")}時` : "";
  }
  if (period === "month" && bucketIndex % 2 !== 0) return "";
  return `${startAt.getMonth() + 1}/${startAt.getDate()}`;
}

/** 選択期間内の作業ログ一覧を表示する。 */
function renderTimeEntryList(entries) {
  // 新しい順のログへ状態、時間、プロジェクト、編集操作を付ける。
  const container = document.getElementById("time-entry-list");
  if (entries.length === 0) {
    container.innerHTML = `<div class="empty-state">この期間の作業ログはありません。</div>`;
    return;
  }
  container.innerHTML = entries.map(function renderTimeEntryRow(timeEntry) {
    const state = timeEntry.endAt ? (timeEntry.needsReview ? "要確認" : "終了") : "実行中";
    const endText = timeEntry.endAt ? formatDateTime(timeEntry.endAt) : "実行中";
    const durationSeconds = Math.max(
      0,
      Math.floor(((timeEntry.endAt ? Date.parse(timeEntry.endAt) : Date.now()) - Date.parse(timeEntry.startAt)) / 1000));
    return `<div class="time-entry-row" style="--project-color:${getProjectColor(timeEntry.projectIdentifier)}">
      <div><strong>${escapeHtml(timeEntry.title)}</strong><span>${escapeHtml(timeEntry.projectNameSnapshot || "未割当")}・${escapeHtml(timeEntry.identifier)}</span></div>
      <div>${formatDateTime(timeEntry.startAt)}<span>～ ${endText}</span></div>
      <div>${formatDuration(durationSeconds)}</div>
      <div>
        <span class="time-entry-status${timeEntry.needsReview ? " review" : ""}">${state}</span>
        <button type="button" class="text-button" data-time-edit="${escapeAttribute(timeEntry.identifier)}">編集</button>
      </div>
    </div>`;
  }).join("");
  for (const editButton of container.querySelectorAll("[data-time-edit]")) {
    editButton.addEventListener("click", function handleTimeEditClick() {
      openTimeEntryDialog(editButton.dataset.timeEdit);
    });
  }
}

/** 選択中の集計期間に応じて基準日を前後へ移動する。 */
function moveTimeReportAnchor(direction) {
  // 日と週は日数、月は暦月単位で移動する。
  const anchor = new Date(applicationState.timeReportAnchor);
  if (applicationState.timeReportPeriod === "month") {
    anchor.setMonth(anchor.getMonth() + direction);
  } else {
    anchor.setDate(anchor.getDate() + direction * (applicationState.timeReportPeriod === "week" ? 7 : 1));
  }
  applicationState.timeReportAnchor = anchor;
}

/** 作業ログの新規追加または編集ダイアログを表示する。 */
function openTimeEntryDialog(timeEntryIdentifier = null, suppliedEntry = null) {
  // 既存ログはスナップショットを復元し、新規時は直前1時間を既定にする。
  const form = document.getElementById("time-entry-form");
  const dialog = document.getElementById("time-entry-dialog");
  const timeEntry = suppliedEntry
    || applicationState.timeEntries.find(entry => entry.identifier === timeEntryIdentifier)
    || applicationState.dashboardResult?.reviewTimeEntries?.find(entry => entry.identifier === timeEntryIdentifier);
  const isActiveEntry = Boolean(timeEntry && !timeEntry.endAt);
  form.reset();
  form.dataset.activeEntry = String(isActiveEntry);
  document.getElementById("time-entry-dialog-title").textContent = timeEntry ? "作業ログを編集" : "作業ログを追加";
  document.getElementById("void-time-entry").classList.toggle("hidden", !timeEntry || isActiveEntry);
  const reviewNote = document.getElementById("time-entry-review-note");
  reviewNote.classList.toggle("hidden", !timeEntry?.needsReview && !isActiveEntry);
  reviewNote.textContent = isActiveEntry
    ? "実行中のログは開始日時だけ変更できます。"
    : timeEntry?.reviewReason || "";
  for (const fixedFieldName of ["title", "taskIdentifier", "projectIdentifier", "endAt"]) {
    // 実行中ログでは開始時刻以外の入力を固定する。
    form.elements.namedItem(fixedFieldName).disabled = isActiveEntry;
  }
  if (timeEntry) {
    form.elements.namedItem("identifier").value = timeEntry.identifier;
    form.elements.namedItem("title").value = timeEntry.title;
    form.elements.namedItem("taskIdentifier").value = timeEntry.taskIdentifier || "";
    form.elements.namedItem("projectIdentifier").value = timeEntry.projectIdentifier || "";
    form.elements.namedItem("startAt").value = toLocalInput(timeEntry.startAt);
    form.elements.namedItem("endAt").value = toLocalInput(timeEntry.endAt);
  } else {
    const endAt = new Date();
    const startAt = new Date(endAt.getTime() - 60 * 60000);
    form.elements.namedItem("startAt").value = toLocalInput(startAt.toISOString());
    form.elements.namedItem("endAt").value = toLocalInput(endAt.toISOString());
  }
  form.querySelector("button[type='submit']").textContent = isActiveEntry ? "開始時刻を保存" : "保存";
  dialog.showModal();
}

/** 作業ログ編集ダイアログを閉じる。 */
function closeTimeEntryDialog() {
  // 保存せずモーダルだけを閉じる。
  document.getElementById("time-entry-dialog").close();
}

/** 選択タスクの名称とプロジェクトを新規作業ログへ反映する。 */
function applySelectedTaskToTimeEntry(event) {
  // タスク選択時だけスナップショット候補を補完し、ユーザーの再編集は許可する。
  const task = applicationState.tasks.find(item => item.identifier === event.target.value);
  if (!task) return;
  const form = document.getElementById("time-entry-form");
  form.elements.namedItem("title").value = task.title;
  form.elements.namedItem("projectIdentifier").value = task.projectIdentifier || "";
}

/** 作業ログ編集フォームを重複確認付きで保存する。 */
async function saveTimeEntry(event) {
  // 409応答時だけ競合内容を示し、ユーザー承認後にallowOverlapを付けて再送する。
  event.preventDefault();
  const form = event.currentTarget;
  const identifier = form.elements.namedItem("identifier").value;
  const isActiveEntry = form.dataset.activeEntry === "true";
  const request = isActiveEntry
    ? {
        startAt: fromLocalInput(form.elements.namedItem("startAt").value),
        allowOverlap: false
      }
    : {
        taskIdentifier: form.elements.namedItem("taskIdentifier").value || null,
        projectIdentifier: form.elements.namedItem("projectIdentifier").value || null,
        title: form.elements.namedItem("title").value,
        startAt: fromLocalInput(form.elements.namedItem("startAt").value),
        endAt: fromLocalInput(form.elements.namedItem("endAt").value),
        allowOverlap: false
      };
  try {
    await submitTimeEntryMutation(identifier, request, isActiveEntry);
  } catch (error) {
    if (error.status !== 409 || !error.details?.conflicts) {
      showNotice(error.message, true);
      return;
    }
    const conflictText = error.details.conflicts
      .map(conflict => `${conflict.title}（${formatDateTime(conflict.startAt)}～${conflict.endAt ? formatDateTime(conflict.endAt) : "実行中"}）`)
      .join("\n");
    if (!window.confirm(`次のログと時間が重複します。\n${conflictText}\n\n両方を集計対象として保存しますか？`)) return;
    request.allowOverlap = true;
    try {
      await submitTimeEntryMutation(identifier, request, isActiveEntry);
    } catch (confirmedError) {
      showNotice(confirmedError.message, true);
      return;
    }
  }
  closeTimeEntryDialog();
  showNotice(isActiveEntry ? "実行中ログの開始時刻を修正しました。" : identifier ? "作業ログを修正しました。" : "作業ログを追加しました。");
  await Promise.all([loadDashboard(), loadTimeReport()]);
}

/** 新規または既存作業ログの保存APIを呼び分ける。 */
async function submitTimeEntryMutation(identifier, request, isActiveEntry = false) {
  // 実行中は開始時刻専用API、それ以外はIDの有無から通常のPOSTとPUTを選ぶ。
  const requestPath = isActiveEntry
    ? `/api/v1/time-entries/${encodeURIComponent(identifier)}/start`
    : identifier
      ? `/api/v1/time-entries/${encodeURIComponent(identifier)}`
      : "/api/v1/time-entries";
  return await apiRequest(
    requestPath,
    { method: identifier ? "PUT" : "POST", body: JSON.stringify(request) });
}

/** 編集中の作業ログを集計対象外へ変更する。 */
async function voidCurrentTimeEntry() {
  // 取消不能な削除ではなく論理無効化であることを確認して実行する。
  const identifier = document.querySelector("#time-entry-form [name='identifier']").value;
  if (!identifier || !window.confirm("この作業ログを集計対象外にしますか？記録自体は履歴として残ります。")) return;
  try {
    await apiRequest(`/api/v1/time-entries/${encodeURIComponent(identifier)}/void`, {
      method: "POST",
      body: "{}"
    });
    closeTimeEntryDialog();
    showNotice("作業ログを集計対象外にしました。");
    await Promise.all([loadDashboard(), loadTimeReport(), loadTasks()]);
  } catch (error) {
    showNotice(error.message, true);
  }
}

/** 設定画面の保存・Google操作を初期化する。 */
function initializeSettingsActions() {
  // 設定保存、認証情報登録、接続、同期を接続する。
  document.getElementById("settings-form").addEventListener("submit", saveSettings);
  document.getElementById("calendar-credentials").addEventListener("change", uploadCalendarCredentials);
  document.getElementById("connect-calendar").addEventListener("click", connectCalendar);
  document.getElementById("sync-calendar").addEventListener("click", synchronizeCalendar);
  document.getElementById("run-external-backup").addEventListener("click", runExternalBackup);
  document.getElementById("refresh-external-backup").addEventListener("click", loadExternalBackupStatus);
}

/** 追加バックアップの保存済み設定と直近結果を表示する。 */
async function loadExternalBackupStatus() {
  // 保存先のエラーをHTMLとして解釈せず、未成功と停止状態を区別して表示する。
  const statusElement = document.getElementById("external-backup-status");
  try {
    const status = await apiRequest("/api/v1/backup/status");
    const hourly = status.lastHourlyAt ? new Date(status.lastHourlyAt).toLocaleString("ja-JP") : "未保存";
    const daily = status.lastDailyAt ? new Date(status.lastDailyAt).toLocaleString("ja-JP") : "未保存";
    statusElement.textContent = !status.enabled ? "OFF：追加バックアップは停止しています。"
      : `時間別の最終保存：${hourly} ／ 日別の最終保存：${daily}。${status.error ? `失敗：${status.error} 次の周期で再試行します。` : status.lastCheckedAt ? "保存先を確認済みです。" : "次の自動実行を待っています（通常1分以内）。"}`;
    document.getElementById("run-external-backup").disabled = !status.enabled;
  } catch (error) {
    statusElement.textContent = `状態の取得に失敗しました：${error.message}`;
  }
}

/** 保存済み設定で追加バックアップを作成して結果を表示する。 */
async function runExternalBackup() {
  // 未保存のフォーム内容と保存済みの実行先が食い違う操作を防ぐ。
  if (applicationState.settingsFormDirty) {
    showNotice("先に設定を保存してください。", true);
    return;
  }
  const button = document.getElementById("run-external-backup");
  button.disabled = true;
  try {
    const status = await apiRequest("/api/v1/backup/external", { method: "POST" });
    showNotice(status.error || (status.enabled ? "バックアップを保存しました。" : "追加バックアップはOFFです。"), Boolean(status.error));
  } catch (error) {
    showNotice(error.message, true);
  } finally {
    await loadExternalBackupStatus();
  }
}

/** 現在設定を読み込んでフォームへ表示する。 */
async function loadSettings() {
  // API設定をキャッシュし存在するフォーム欄へ設定する。
  applicationState.settings = await apiRequest("/api/v1/settings");
  const form = document.getElementById("settings-form");
  for (const [fieldName, fieldValue] of Object.entries(applicationState.settings)) {
    const field = form.elements.namedItem(fieldName);
    if (!field) continue;
    if (field.type === "checkbox") field.checked = Boolean(fieldValue);
    else field.value = fieldValue;
  }
  applicationState.settingsFormDirty = false;
  await loadExternalBackupStatus();
  if (applicationState.uiChangeRefreshPending) {
    window.setTimeout(applyPendingUiChange, 0);
  }
}

/** 設定フォームを型付きJSONへ変換して保存する。 */
async function saveSettings(event) {
  // 画面にない詳細設定は現在値を維持する。
  event.preventDefault();
  const form = event.currentTarget;
  const settings = { ...applicationState.settings };
  for (const field of form.elements) {
    if (!field.name) continue;
    if (field.type === "checkbox") settings[field.name] = field.checked;
    else if (field.type === "number") settings[field.name] = Number(field.value);
    else settings[field.name] = field.value;
  }
  try {
    await apiRequest("/api/v1/settings", { method: "PUT", body: JSON.stringify(settings) });
    // サーバーで正規化されたCodexタスクIDなどをフォームへ読み戻す。
    await loadSettings();
    showNotice("設定を保存しました。");
    await loadDashboard();
  } catch (error) {
    showNotice(error.message, true);
  }
}

/** Googleのcredentials.jsonをローカルへ登録する。 */
async function uploadCalendarCredentials(event) {
  // 選択したファイルをmultipartで安全に送信する。
  const file = event.target.files[0];
  if (!file) return;
  const formData = new FormData();
  formData.append("credentialFile", file);
  try {
    await apiRequest("/api/v1/calendar/credentials", { method: "POST", body: formData });
    showNotice("credentials.jsonを保存しました。続けてGoogleへ接続してください。");
  } catch (error) {
    showNotice(error.message, true);
  }
}

/** システムブラウザでGoogle Calendar認証を開始する。 */
async function connectCalendar() {
  // 認証完了まで待ち、初回同期結果を表示する。
  try {
    showNotice("Google認証を開始します。ブラウザの案内に従ってください。");
    const result = await apiRequest("/api/v1/calendar/connect", { method: "POST", body: "{}" });
    showNotice(`Google Calendarへ接続し、${result.eventCount}件を同期しました。`);
    await loadDashboard();
  } catch (error) {
    showNotice(error.message, true);
  }
}

/** Google Calendarを手動同期する。 */
async function synchronizeCalendar() {
  // 同期後に空き時間と推薦を再表示する。
  try {
    const result = await apiRequest("/api/v1/calendar/synchronize", { method: "POST", body: "{}" });
    showNotice(`${result.eventCount}件の予定を同期しました。`);
    await loadDashboard();
  } catch (error) {
    showNotice(error.message, true);
    if (applicationState.activeView === "dashboard") {
      // 保存された同期エラーを警告カードへ直ちに反映する。
      try {
        await loadDashboard();
      } catch {
        // 元の同期エラー通知を維持する。
      }
    }
  }
}

/** 操作履歴を新しい順に表示する。 */
async function loadHistory() {
  // 最新200件を取得して表示切替時にも再利用する。
  applicationState.history = await apiRequest("/api/v1/history?maximumCount=200");
  renderHistory();
}

/** 取得済み履歴をカレンダーログの表示設定に合わせて描画する。 */
function renderHistory() {
  // 通常操作を優先し、チェック時だけカレンダー同期と作業ログ詳細を含める。
  const showCalendarHistory = document.getElementById("show-calendar-history").checked;
  const showTimeHistory = document.getElementById("show-time-history").checked;
  const visibleHistory = applicationState.history.filter(function filterHistoryRecord(record) {
    // 高頻度の同期と時間記録イベントをそれぞれ任意表示の対象にする。
    const isCalendarHistory = record.eventType === "カレンダー同期" || record.eventType === "カレンダーエラー";
    const isTimeHistory = record.eventType.includes("作業ログ")
      || record.eventType.includes("自由活動")
      || record.eventType.includes("タイマー");
    return (showCalendarHistory || !isCalendarHistory) && (showTimeHistory || !isTimeHistory);
  });
  const historyContainer = document.getElementById("history-list");
  historyContainer.innerHTML = visibleHistory.length === 0
    ? `<div class="empty-state">表示対象の履歴はありません。</div>`
    : visibleHistory.map(function renderHistoryItem(record) {
      // 日時、種類、入力元、対象タスクを1件の履歴カードへ整形する。
      return `<article class="timeline-item"><time>${formatDateTime(record.occurredAt)}</time><div><strong>${escapeHtml(record.eventType)}</strong><div class="source">${escapeHtml(record.source)}</div></div><div>${escapeHtml(record.summary)}${record.taskIdentifier ? `<div class="muted">タスク: ${escapeHtml(record.taskIdentifier)}</div>` : ""}${record.projectIdentifier ? `<div class="muted">プロジェクト: ${escapeHtml(record.projectIdentifier)}</div>` : ""}${record.details ? `<div class="muted">${escapeHtml(record.details)}</div>` : ""}</div></article>`;
    }).join("");
}

/** 画面位置へ影響しない固定トーストで成功またはエラー通知を表示する。 */
function showNotice(message, isError = false) {
  // 画面右上へ重ね、数秒後に自動で隠して操作を妨げないようにする。
  const notice = document.getElementById("notice");
  notice.textContent = message;
  notice.classList.toggle("error", isError);
  notice.classList.remove("hidden");
  window.clearTimeout(showNotice.timeoutIdentifier);
  showNotice.timeoutIdentifier = window.setTimeout(function hideNotice() {
    // 通知領域をレイアウトへ残さず完全に非表示に戻す。
    notice.classList.add("hidden");
  }, 6000);
}

/** ISO日時を日本語の短い表示へ変換する。 */
function formatDateTime(value) {
  // 未指定日時はダッシュで表示する。
  if (!value) return "—";
  return new Intl.DateTimeFormat("ja-JP", { month: "numeric", day: "numeric", hour: "2-digit", minute: "2-digit" }).format(new Date(value));
}

/** 秒数を時間と分の読みやすい表記へ変換する。 */
function formatDuration(totalSeconds) {
  // 秒を分へ切り捨て、60分以上では時間と余り分に分ける。
  const totalMinutes = Math.max(0, Math.floor(Number(totalSeconds || 0) / 60));
  const hours = Math.floor(totalMinutes / 60);
  const minutes = totalMinutes % 60;
  return hours > 0 ? `${hours}時間${minutes}分` : `${minutes}分`;
}

/** DateをローカルのYYYY-MM-DDへ変換する。 */
function formatLocalDate(value) {
  // UTC変換による日付ずれを避けてブラウザの年月日を直接連結する。
  const date = new Date(value);
  return `${date.getFullYear()}-${String(date.getMonth() + 1).padStart(2, "0")}-${String(date.getDate()).padStart(2, "0")}`;
}

/** タスク一覧の期限を日付と時刻へ分けて整形する。 */
function formatTaskDeadline(value) {
  // 狭い期限列で読みやすいように日付と時刻を個別に生成する。
  const deadline = new Date(value);
  return {
    date: new Intl.DateTimeFormat("ja-JP", { month: "numeric", day: "numeric" }).format(deadline),
    time: new Intl.DateTimeFormat("ja-JP", { hour: "2-digit", minute: "2-digit" }).format(deadline)
  };
}

/** ISO日時をdatetime-local入力値へ変換する。 */
function toLocalInput(value) {
  // ブラウザのローカル時刻へ補正して秒を除く。
  if (!value) return "";
  const date = new Date(value);
  const localDate = new Date(date.getTime() - date.getTimezoneOffset() * 60000);
  return localDate.toISOString().slice(0, 16);
}

/** datetime-local入力をISO日時へ変換する。 */
function fromLocalInput(value) {
  // 空欄はnullとしてAPIへ渡す。
  return value ? new Date(value).toISOString() : null;
}

/** HTML本文へ表示する文字列をエスケープする。 */
function escapeHtml(value) {
  // DOMへの文字列挿入でスクリプトが実行されないようにする。
  return String(value ?? "").replace(/[&<>"']/g, function replaceCharacter(character) {
    return { "&": "&amp;", "<": "&lt;", ">": "&gt;", "\"": "&quot;", "'": "&#039;" }[character];
  });
}

/** HTML属性へ表示する文字列をエスケープする。 */
function escapeAttribute(value) {
  // 属性値にも本文と同じ安全化を適用する。
  return escapeHtml(value);
}

// DOM準備後にローカルアプリを起動する。
document.addEventListener("DOMContentLoaded", initializeApplication);
