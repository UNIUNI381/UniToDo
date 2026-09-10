(() => {
  // 会話表示と編集中の回答を保持し、タブを閉じてもサーバーの処理は停止しない。
  const panel = document.getElementById("assistant-panel");
  const messageList = document.getElementById("assistant-messages");
  const promptList = document.getElementById("assistant-prompts");
  const textInput = document.getElementById("assistant-text");
  let snapshot = null;
  let polling = false;
  let busy = false;
  let promptSignature = "";
  let messageSignature = "";
  let pendingSubmission = null;
  let pollTimer = null;
  // キーボード開閉前の末尾追従状態とViewport更新の予約を保持する。
  let followLatest = true;
  let viewportFrame = null;

  function updateViewport() {
    // Androidの表示可能領域へ高さと位置を合わせ、キーボード背後へのはみ出しを防ぐ。
    viewportFrame = null;
    if (panel.classList.contains("hidden")) return;
    const mobile = window.matchMedia("(max-width: 680px)").matches;
    const viewport = window.visualViewport;
    const height = viewport?.height ?? window.innerHeight;
    const keepLatest = followLatest || document.activeElement === textInput;
    if (mobile) {
      panel.style.setProperty("--assistant-viewport-height", `${height}px`);
      panel.style.setProperty("--assistant-viewport-top", `${viewport?.offsetTop ?? 0}px`);
    } else {
      panel.style.removeProperty("--assistant-viewport-height");
      panel.style.removeProperty("--assistant-viewport-top");
    }
    panel.classList.toggle("assistant-compact", mobile && height < 480);
    // レイアウト更新後の末尾へ合わせ、過去履歴を読んでいる場合は位置を維持する。
    if (keepLatest) messageList.scrollTop = messageList.scrollHeight;
  }

  function scheduleViewport() {
    // キーボードのアニメーション中に重なる通知を描画単位にまとめる。
    if (viewportFrame === null) viewportFrame = window.requestAnimationFrame(updateViewport);
  }

  messageList.addEventListener("scroll", () => {
    // 利用者が過去の発言を読んでいるかをリサイズ前に保持する。
    followLatest = messageList.scrollHeight - messageList.scrollTop - messageList.clientHeight < 100;
  }, { passive: true });
  window.addEventListener("resize", scheduleViewport, { passive: true });
  window.visualViewport?.addEventListener("resize", scheduleViewport, { passive: true });
  window.visualViewport?.addEventListener("scroll", scheduleViewport, { passive: true });
  textInput.addEventListener("focus", scheduleViewport);

  function showError(message) {
    // エラーはHTMLとして解釈せず、パネル内へ表示する。
    const element = document.getElementById("assistant-error");
    element.textContent = message || "";
    element.classList.toggle("hidden", !message);
  }

  function element(tag, text, className = "") {
    // Codexやユーザー由来の文字列を安全なテキストノードにする。
    const result = document.createElement(tag);
    result.textContent = text;
    result.className = className;
    return result;
  }

  function render(state) {
    // 差分がある部分だけ更新し、入力中の質問回答を維持する。
    snapshot = state;
    const labels = { idle: "送信できます", running: "処理中…", uncertain: "接続・実行結果を確認してください", archived: "アーカイブ済み・新しい会話を開始できます" };
    document.getElementById("assistant-status").textContent = state.prompts.length ? "確認待ち" : labels[state.status] || state.status;
    document.getElementById("assistant-send").disabled = busy || state.status !== "idle";
    document.getElementById("assistant-new").disabled = busy || !["idle", "archived"].includes(state.status);
    document.getElementById("assistant-stop").disabled = busy || ["idle", "archived"].includes(state.status);
    const messages = JSON.stringify(state.messages);
    if (messages !== messageSignature) {
      const followBottom = messageList.scrollHeight - messageList.scrollTop - messageList.clientHeight < 100;
      messageList.replaceChildren();
      for (const message of state.messages) {
        const article = element("article", "", `assistant-message assistant-${message.role}`);
        article.append(element("strong", message.role === "user" ? "あなた" : message.role === "assistant" ? "Codex" : "実行状況"));
        article.append(element("div", message.text));
        messageList.append(article);
      }
      if (!state.messages.length) messageList.append(element("p", "タスクの確認、登録、整理を依頼できます。", "assistant-empty"));
      if (followBottom || !messageSignature) messageList.scrollTop = messageList.scrollHeight;
      messageSignature = messages;
    }
    const signature = JSON.stringify(state.prompts);
    if (signature !== promptSignature) {
      promptList.replaceChildren();
      for (const prompt of state.prompts) renderPrompt(prompt);
      promptSignature = signature;
    }
  }

  function renderPrompt(prompt) {
    // 確認要求ごとに独立したフォームを作り、自動承認はしない。
    const form = element("form", "", "assistant-prompt");
    form.append(element("strong", prompt.kind === "question" ? "確認したいこと" : "実行の確認"));
    if (prompt.description) form.append(element("pre", prompt.description));
    const answers = new Map();
    for (const question of prompt.kind === "mcp-form" ? [] : prompt.questions || []) {
      const label = element("label", question.question);
      const input = document.createElement("input");
      input.type = question.isSecret ? "password" : "text";
      input.autocomplete = "off";
      input.required = true;
      input.maxLength = 4000;
      label.append(input);
      form.append(label);
      answers.set(question.id, input);
      for (const option of question.options || []) {
        const button = element("button", option.label, "secondary-button");
        button.type = "button";
        button.title = option.description || "";
        button.addEventListener("click", () => {
          // 選択肢は回答欄へ入れるだけで、その場では送信しない。
          input.value = option.label;
        });
        form.append(button);
      }
    }
    if (prompt.kind === "mcp-form") {
      // サーバーで対応可能と確認されたMCPフォームを表示し、既定値による自動承認をしない。
      for (const [name, definition] of Object.entries(prompt.questions.properties)) {
        const label = element("label", definition.title || name);
        const choices = definition.type === "boolean" ? ["true", "false"] : definition.enum;
        const input = document.createElement(choices ? "select" : "input");
        if (choices) {
          const placeholder = element("option", "選択してください");
          placeholder.value = "";
          placeholder.disabled = true;
          placeholder.selected = true;
          input.append(placeholder);
          for (const value of choices) {
            const option = element("option", definition.type === "boolean" ? value === "true" ? "はい" : "いいえ" : value);
            option.value = value;
            input.append(option);
          }
        } else {
          input.type = "text";
          input.maxLength = 4000;
          input.autocomplete = "off";
        }
        input.required = (prompt.questions.required || []).includes(name);
        label.append(input);
        if (definition.description) label.append(element("small", definition.description));
        form.append(label);
        answers.set(name, input);
      }
    }
    if (prompt.kind !== "unsupported") {
      const accept = element("button", prompt.kind === "question" ? "回答する" : "今回のみ許可", "primary-button");
      accept.type = "submit";
      form.append(accept);
    }
    if (prompt.kind !== "question") {
      const decline = element("button", "拒否", "secondary-button");
      decline.type = "button";
      decline.addEventListener("click", () => {
        // この確認の拒否だけを送り、後続の確認をまとめて許可しない。
        operate("/answers", { identifier: prompt.identifier, action: "decline" });
      });
      form.append(decline);
    }
    form.addEventListener("submit", (event) => {
      // 質問の識別子と入力値だけを送り、RPC本文はブラウザから指定しない。
      event.preventDefault();
      operate("/answers", { identifier: prompt.identifier, action: "accept", answers: Object.fromEntries([...answers]
        .filter(([, input]) => prompt.kind !== "mcp-form" || input.value !== "" || input.required)
        .map(([identifier, input]) => [identifier, [input.value]])) });
    });
    promptList.append(form);
  }

  async function operate(path, body) {
    // 通信エラー時にも依頼を勝手に再送しない。
    if (busy) return false;
    busy = true;
    showError("");
    if (snapshot) render(snapshot);
    for (const button of promptList.querySelectorAll("button")) button.disabled = true;
    try {
      const state = await apiRequest(`/api/v1/assistant${path}`, { method: "POST", body: JSON.stringify(body) });
      render(state);
      showError(state.error);
      return true;
    } catch (error) {
      showError(error.message + (path === "/messages" ? " 自動再送はしません。履歴を確認してください。" : ""));
      return false;
    } finally {
      busy = false;
      if (snapshot) render(snapshot);
      for (const button of promptList.querySelectorAll("button")) button.disabled = false;
    }
  }

  async function poll() {
    // 再表示やAndroidの復帰時にも同じPC側会話へ接続する。
    if (polling || panel.classList.contains("hidden")) return;
    window.clearTimeout(pollTimer);
    polling = true;
    try {
      if (!busy) {
        const state = await apiRequest("/api/v1/assistant/state");
        render(state);
        if (state.error) showError(state.error);
      }
    } catch (error) {
      showError(error.message);
      document.getElementById("assistant-send").disabled = true;
    } finally {
      polling = false;
      if (!panel.classList.contains("hidden")) pollTimer = window.setTimeout(poll, 1200);
    }
  }

  window.unitodoAssistant = {
    async open() {
      // PCでは右パネル、狭い画面では全面パネルとして開く。
      const wasHidden = panel.classList.contains("hidden");
      panel.classList.remove("hidden");
      followLatest = true;
      updateViewport();
      if (wasHidden) await poll();
      if (panel.classList.contains("hidden")) return;
      textInput.focus({ preventScroll: true });
      messageList.scrollTop = messageList.scrollHeight;
      scheduleViewport();
    }
  };

  function closePanel() {
    // 表示だけを閉じてPC側の実行状態は維持する。
    panel.classList.add("hidden");
    window.clearTimeout(pollTimer);
  }

  document.getElementById("assistant-close").addEventListener("click", closePanel);
  document.addEventListener("click", (event) => {
    // PCの欄外クリックで閉じ、パネル内と開くボタンの操作はそのまま受け付ける。
    if (window.matchMedia("(max-width: 680px)").matches || panel.classList.contains("hidden")) return;
    if (panel.contains(event.target) || event.target instanceof Element && event.target.closest("[data-open-codex-task-thread]")) return;
    closePanel();
  }, { capture: true });
  document.getElementById("assistant-new").addEventListener("click", async () => {
    // 既存会話はCodex側に残し、利用する会話だけ切り替える。
    if (snapshot && window.confirm("新しい会話に切り替えますか？ 現在の履歴はCodex側に残ります。")) {
      if (await operate("/new", { conversation: snapshot.conversation })) pendingSubmission = null;
    }
  });
  document.getElementById("assistant-stop").addEventListener("click", () => {
    // 停止は既に反映されたタスク変更を戻さない。
    if (snapshot) operate("/interrupt", { conversation: snapshot.conversation });
  });
  document.getElementById("assistant-reconnect").addEventListener("click", () => {
    // 送信結果が不明でも既存会話の履歴だけを取得する。
    if (snapshot) operate("/reconnect", { conversation: snapshot.conversation });
    else poll();
  });
  document.getElementById("assistant-form").addEventListener("submit", async (event) => {
    // 同じ入力を再試行するときは同じ識別子を再利用する。
    event.preventDefault();
    const text = textInput.value.trim();
    if (!snapshot || !text || busy) return;
    if (!pendingSubmission || pendingSubmission.text !== text || pendingSubmission.conversation !== snapshot.conversation)
      pendingSubmission = { conversation: snapshot.conversation, requestIdentifier: crypto.randomUUID(), text };
    if (await operate("/messages", pendingSubmission)) {
      textInput.value = "";
      pendingSubmission = null;
    }
  });
})();
