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
  // 通知の自動非表示予約と、ブラウザで送信結果を確認できなかった状態を保持する。
  let noticeTimer = null;
  let submissionUncertain = false;
  // 履歴照合後に利用者の確認が必要な注意を保持する。
  let noticeRequiresReview = false;
  // キーボード開閉前の末尾追従状態とViewport更新の予約を保持する。
  let followLatest = true;
  let viewportFrame = null;
  // 閉じるボタンによる履歴移動の完了待ちと、その解除処理を保持する。
  let closingNavigation = null;
  let finishClosingNavigation = null;

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
    window.clearTimeout(noticeTimer);
    noticeTimer = null;
    noticeRequiresReview = false;
    const element = document.getElementById("assistant-error");
    element.textContent = message || "";
    element.classList.toggle("hidden", !message);
  }

  function dismissNoticeLater() {
    // 正常復帰後の通知を3秒で閉じ、定期取得では期限を延長しない。
    if (noticeRequiresReview || noticeTimer !== null || document.getElementById("assistant-error").classList.contains("hidden")) return;
    noticeTimer = window.setTimeout(() => {
      // 新しいエラーがない場合にだけ、予約された通知を閉じる。
      showError("");
    }, 3000);
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
    const wasUncertain = submissionUncertain || snapshot?.status === "uncertain";
    busy = true;
    showError("");
    if (snapshot) render(snapshot);
    for (const button of promptList.querySelectorAll("button")) button.disabled = true;
    try {
      const state = await apiRequest(`/api/v1/assistant${path}`, { method: "POST", body: JSON.stringify(body) });
      render(state);
      showError(state.error);
      if (path === "/reconnect" && !state.error && ["idle", "running"].includes(state.status)) {
        // 結果不明だった依頼の照合時だけ注意を添え、通常更新は短い通知にする。
        showError("履歴を更新しました" + (wasUncertain ? "。結果不明だった依頼の反映状況を確認してください。" : ""));
        submissionUncertain = false;
        noticeRequiresReview = wasUncertain;
        if (!wasUncertain) dismissNoticeLater();
      }
      if (path === "/messages" && state.status !== "uncertain") submissionUncertain = false;
      return true;
    } catch (error) {
      // 応答を受け取れなかった送信は、次の履歴取得まで結果不明として扱う。
      if (path === "/messages") submissionUncertain = true;
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
        else if (state.status === "idle" && !submissionUncertain) dismissNoticeLater();
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
      if (closingNavigation) await closingNavigation;
      const wasHidden = panel.classList.contains("hidden");
      if (wasHidden && window.matchMedia("(max-width: 680px)").matches && !window.history.state?.unitodoAssistant) {
        // 同じページ内に会話表示の履歴を積み、Androidの戻る操作を受け止める。
        window.history.pushState({ ...window.history.state, unitodoAssistant: true }, "");
      }
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
    if (panel.contains(document.activeElement)) document.activeElement.blur();
    window.clearTimeout(pollTimer);
    if (window.history.state?.unitodoAssistant && !closingNavigation) {
      // ボタンで閉じた場合も会話分の履歴を戻し、再開を繰り返しても履歴を増やさない。
      closingNavigation = new Promise(resolve => {
        // 非同期の履歴移動後に再表示を許可する。
        finishClosingNavigation = resolve;
      });
      window.history.back();
    }
  }

  window.addEventListener("popstate", () => {
    // 戻る操作では会話だけを閉じ、進む操作では同じ会話を再表示する。
    const finish = finishClosingNavigation;
    closingNavigation = null;
    finishClosingNavigation = null;
    if (window.history.state?.unitodoAssistant) window.unitodoAssistant.open();
    else closePanel();
    finish?.();
  });

  if (window.history.state?.unitodoAssistant) {
    // 会話表示中の再読み込みでも履歴とパネル表示を一致させる。
    window.unitodoAssistant.open();
  }

  document.getElementById("assistant-close").addEventListener("click", closePanel);
  document.addEventListener("pointerdown", (event) => {
    // PCで押し始めた位置が欄外のときだけ閉じ、パネル内からのドラッグは維持する。
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
  const assistantForm = document.getElementById("assistant-form");
  const sendButton = document.getElementById("assistant-send");
  textInput.addEventListener("keydown", (event) => {
    // IME変換中を除くCtrl+Enterで、送信ボタンと同じフォーム送信を実行する。
    if (event.key !== "Enter" || !event.ctrlKey || event.isComposing) return;
    event.preventDefault();
    if (!sendButton.disabled) assistantForm.requestSubmit(sendButton);
  });
  assistantForm.addEventListener("submit", async (event) => {
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
