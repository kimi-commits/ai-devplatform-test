// AI-DOS Chat 面板前端邏輯。原本內嵌在 chatPanel.ts 的 <script> 字串裡,拆成獨立檔案的原因
// 見同目錄 chatPanel.css 開頭註解(webview.html 整包字串超過約 6KB 觸發 document.write bug)。
(function () {
  const vscode = acquireVsCodeApi();
  const transcript = document.getElementById("transcript");
  const statusBar = document.getElementById("statusBar");
  const input = document.getElementById("messageInput");
  const workflowSelect = document.getElementById("workflowSelect");
  const prdSelect = document.getElementById("prdSelect");
  const reportSelect = document.getElementById("reportSelect");
  const sendButton = document.getElementById("sendButton");
  const reviseButton = document.getElementById("reviseButton");
  const acceptButton = document.getElementById("acceptButton");

  function appendLine(text, cssClass) {
    const div = document.createElement("div");
    div.className = "msg " + cssClass;
    div.textContent = text;
    transcript.appendChild(div);
    transcript.scrollTop = transcript.scrollHeight;
  }

  // Stage C:選了「Project Manager」模式時,把自由輸入框換成 PRD 下拉選單(使用者要選一份
  // 已經產生好的 PRD,不是打字),並跟 extension host 要一份最新的 PRD 清單。其他模式維持
  // 原本的文字輸入框。
  function isPmDispatchMode() {
    return workflowSelect.value === "pm-dispatch";
  }

  // Stage E:選了「Product Manager(驗收)」模式時,把自由輸入框換成測試報告下拉選單,
  // 旁邊的送出按鈕換成「修改規格」/「完成驗收」兩顆按鈕(見下面 updateInputMode)。
  function isAcceptanceMode() {
    return workflowSelect.value === "acceptance";
  }

  function updateInputMode() {
    const pmDispatch = isPmDispatchMode();
    const acceptance = isAcceptanceMode();

    input.style.display = pmDispatch || acceptance ? "none" : "";
    prdSelect.style.display = pmDispatch ? "" : "none";
    reportSelect.style.display = acceptance ? "" : "none";
    sendButton.style.display = acceptance ? "none" : "";
    reviseButton.style.display = acceptance ? "" : "none";
    acceptButton.style.display = acceptance ? "" : "none";

    if (pmDispatch) {
      vscode.postMessage({ command: "requestPrdList" });
    } else if (acceptance) {
      vscode.postMessage({ command: "requestReportList" });
    }
  }

  workflowSelect.addEventListener("change", updateInputMode);
  updateInputMode();

  function send() {
    if (isPmDispatchMode()) {
      const prdId = prdSelect.value;
      if (!prdId) {
        return;
      }
      vscode.postMessage({ command: "dispatchPm", prdId });
      return;
    }

    const text = input.value;
    if (!text.trim()) {
      return;
    }
    vscode.postMessage({ command: "send", text, workflow: workflowSelect.value });
    input.value = "";
    input.style.height = "auto";
  }

  // 輸入框是 textarea,支援多行/貼上長文字;純 Enter 一律當作換行(textarea 預設行為,
  // 不攔截),Ctrl+Enter(Mac 是 Cmd+Enter)才送出,避免打到一半按 Enter 換行就被誤送出。
  input.addEventListener("input", () => {
    input.style.height = "auto";
    input.style.height = Math.min(input.scrollHeight, 192) + "px";
  });

  // 送出訊息(一般需求或 PM 對話)時 AI 回覆前不能重複送出,避免按太快同時打好幾個請求;
  // Workflow 執行中的狀態列只是提示,不鎖輸入,因為使用者可能想開另一場對話或新的 Workflow。
  function setStatus(text, lockInput) {
    statusBar.textContent = text || "";
    const disabled = Boolean(lockInput && text);
    input.disabled = disabled;
    prdSelect.disabled = disabled;
    reportSelect.disabled = disabled;
    sendButton.disabled = disabled;
    reviseButton.disabled = disabled;
    acceptButton.disabled = disabled;
  }

  /** 收到 extension host 回傳的 PRD 清單(見 ChatPanel.loadPrdList),重新填 prdSelect 的選項。 */
  function renderPrdList(prds) {
    prdSelect.innerHTML = "";
    if (!prds || prds.length === 0) {
      const option = document.createElement("option");
      option.value = "";
      option.textContent = "(目前沒有任何 PRD,請先用「PM 規劃討論」產生一份)";
      prdSelect.appendChild(option);
      return;
    }

    prds.forEach((prd) => {
      const option = document.createElement("option");
      option.value = prd.id;
      const createdAt = new Date(prd.createdAt).toLocaleString();
      option.textContent = prd.title + "(" + createdAt + ")";
      prdSelect.appendChild(option);
    });
  }

  /** Stage E:收到測試報告清單(見 ChatPanel.loadReportList),重新填 reportSelect 的選項。 */
  function renderReportList(reports) {
    reportSelect.innerHTML = "";
    if (!reports || reports.length === 0) {
      const option = document.createElement("option");
      option.value = "";
      option.textContent = "(目前沒有任何測試報告,請先用「Project Manager」模式跑完一次分派)";
      reportSelect.appendChild(option);
      return;
    }

    reports.forEach((report) => {
      const option = document.createElement("option");
      option.value = report.id;
      const createdAt = new Date(report.createdAt).toLocaleString();
      const verdict = report.passed ? "✅" : "⚠️";
      option.textContent = verdict + " " + report.title + "(" + createdAt + ")";
      reportSelect.appendChild(option);
    });
  }

  sendButton.addEventListener("click", send);
  reviseButton.addEventListener("click", () => {
    vscode.postMessage({ command: "reviseReport", reportId: reportSelect.value });
  });
  acceptButton.addEventListener("click", () => {
    vscode.postMessage({ command: "acceptReport", reportId: reportSelect.value });
  });
  input.addEventListener("keydown", (e) => {
    if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) {
      e.preventDefault();
      send();
    }
  });

  window.addEventListener("message", (event) => {
    const message = event.data;
    if (message.command === "setStatus") {
      setStatus(message.text, Boolean(message.lockInput));
    } else if (message.command === "prdList") {
      renderPrdList(message.prds);
    } else if (message.command === "reportList") {
      renderReportList(message.reports);
    } else if (message.command === "switchMode") {
      // Stage F:「修改規格」流程開場後,把下拉選單切回「PM 規劃討論」模式,讓文字輸入框
      // 重新出現,使用者才能繼續跟 PM 來回討論(見 chatPanel.ts 的 reviseReport)。
      workflowSelect.value = message.workflow;
      updateInputMode();
    } else if (message.command === "appendUser") {
      appendLine("你: " + message.text, "user");
    } else if (message.command === "appendSystem") {
      appendLine(message.text, "system");
    } else if (message.command === "appendPm") {
      const div = document.createElement("div");
      div.className = "msg pm";
      div.textContent = "🧑‍💼 PM: " + message.text;
      transcript.appendChild(div);

      const btn = document.createElement("button");
      btn.className = "finalizeButton";
      btn.textContent = "📋 確認規格,產生 PRD";
      btn.addEventListener("click", () => {
        vscode.postMessage({ command: "finalizePlanning" });
      });
      transcript.appendChild(btn);
      transcript.scrollTop = transcript.scrollHeight;
    } else if (message.command === "appendSpecReady") {
      const div = document.createElement("div");
      div.className = "msg system";
      div.textContent = "📋 PRD 已產生:\n\n" + message.text;
      transcript.appendChild(div);

      // Stage F:如果這份新 PRD 是從「修改規格」按鈕討論出來的,不能顯示「開始開發」按鈕——
      // 那顆按鈕接的是 default/parallel pipeline,不是「Project Manager」動態分派流程,接下去
      // 應該回「Project Manager」模式手動選這份 PRD(見 PlanningSession.Origin 類別註解)。
      if (message.origin === "revise") {
        const hint = document.createElement("div");
        hint.className = "msg system";
        hint.textContent =
          "(這是修改規格後的新版本;請切到「Project Manager」模式的下拉選單選這份 PRD," +
          "重新分派給 CoderA/B/C。)";
        transcript.appendChild(hint);
      } else {
        const btn = document.createElement("button");
        btn.className = "finalizeButton";
        btn.textContent = "🚀 開始開發";
        btn.addEventListener("click", () => {
          vscode.postMessage({ command: "startDevelopment" });
        });
        transcript.appendChild(btn);
      }
      transcript.scrollTop = transcript.scrollHeight;
    } else if (message.command === "appendStepSucceeded") {
      const div = document.createElement("div");
      div.className = "msg system";
      div.textContent = "✅ Step '" + message.stepId + "' 完成";
      (message.artifactIds || []).forEach((id) => {
        const btn = document.createElement("button");
        btn.className = "diffButton";
        btn.textContent = "顯示內容/Diff (" + id.slice(0, 8) + ")";
        btn.addEventListener("click", () => {
          vscode.postMessage({ command: "showDiff", artifactId: id });
        });
        div.appendChild(btn);
      });
      transcript.appendChild(div);
      transcript.scrollTop = transcript.scrollHeight;
    }
  });
})();
