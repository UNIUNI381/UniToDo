"use strict";

/** 距離と方向から隣接タブへの移動方向を判定する。 */
function mobileSwipeDirection(horizontalDistance, verticalDistance) {
  // 64px以上かつ横方向が十分優勢なジェスチャーだけを採用する。
  if (Math.abs(horizontalDistance) < 64 || Math.abs(horizontalDistance) < Math.abs(verticalDistance) * 1.5) return 0;
  return horizontalDistance < 0 ? 1 : -1;
}

/** スマホの本文領域で左右スワイプによるタブ切替を接続する。 */
function initializeMobileNavigation(getActiveView, navigate) {
  // ジェスチャー開始位置と方向固定の状態を保持する。
  const content = document.querySelector(".main-content");
  let gesture = null;

  function blocked(target) {
    // 編集・会話・モーダル・既存の横スクロール操作を優先する。
    if (!(target instanceof Element) || document.querySelector('dialog[open], #assistant-panel:not(.hidden), .project-filter-menu:not(.hidden)')) return true;
    if (target.closest('input, textarea, select, button, a, label, [contenteditable]:not([contenteditable="false"]), .calendar-timeline-scroll, .dependency-component-scroll')) return true;
    if (document.activeElement?.matches('input, textarea, select, [contenteditable]:not([contenteditable="false"])')) return true;
    if (window.getSelection()?.type === "Range") return true;
    for (let ancestor = target; ancestor && ancestor !== content; ancestor = ancestor.parentElement) {
      if (ancestor.scrollWidth > ancestor.clientWidth + 1 && /auto|scroll/.test(getComputedStyle(ancestor).overflowX)) return true;
    }
    return false;
  }

  content.addEventListener("touchstart", (event) => {
    // 画面端のAndroid戻る操作と複数指の拡大操作は奪わない。
    gesture = null;
    if (!window.matchMedia("(max-width: 680px)").matches || event.touches.length !== 1 || blocked(event.target)) return;
    const touch = event.touches[0];
    if (touch.clientX < 24 || touch.clientX > document.documentElement.clientWidth - 24) return;
    gesture = { identifier: touch.identifier, horizontal: touch.clientX, vertical: touch.clientY, view: getActiveView(), direction: null };
  }, { passive: true });

  content.addEventListener("touchmove", (event) => {
    // 縦スクロールと判定した後は横移動が増えてもタブ切替へ変更しない。
    if (!gesture) return;
    if (event.touches.length !== 1) { gesture = null; return; }
    const touch = event.touches[0];
    const horizontalDistance = touch.clientX - gesture.horizontal;
    const verticalDistance = touch.clientY - gesture.vertical;
    if (gesture.direction === null && Math.max(Math.abs(horizontalDistance), Math.abs(verticalDistance)) > 10) {
      if (Math.abs(horizontalDistance) < Math.abs(verticalDistance) * 1.5) { gesture = null; return; }
      gesture.direction = "horizontal";
    }
    // 横操作だけブラウザのスクロールから切り離し、通常の縦スクロールは維持する。
    if (gesture.direction === "horizontal" && event.cancelable) event.preventDefault();
  }, { passive: false });

  content.addEventListener("touchend", (event) => {
    // ナビゲーションに表示されているタブだけを順に移動し、端では折り返さない。
    const completed = gesture;
    gesture = null;
    if (!completed || event.touches.length || completed.view !== getActiveView() || blocked(event.target)) return;
    const touch = [...event.changedTouches].find(candidate => candidate.identifier === completed.identifier);
    if (!touch) return;
    const direction = mobileSwipeDirection(touch.clientX - completed.horizontal, touch.clientY - completed.vertical);
    if (!direction) return;
    const buttons = [...document.querySelectorAll(".nav-button")].filter(button => !button.disabled && button.getClientRects().length);
    const position = buttons.findIndex(button => button.dataset.view === completed.view);
    const destination = buttons[position + direction];
    if (position >= 0 && destination) navigate(destination.dataset.view);
  }, { passive: true });

  content.addEventListener("touchcancel", () => {
    // OSやブラウザに中断された操作は実行しない。
    gesture = null;
  }, { passive: true });
}

// ブラウザ起動を伴わず距離・方向判定を検証できるよう公開する。
if (typeof module !== "undefined") module.exports = { mobileSwipeDirection, initializeMobileNavigation };
