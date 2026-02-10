(function () {
  const tauri = window.__TAURI__;
  const invoke = tauri?.core?.invoke;
  const listen = tauri?.event?.listen;
  const messageChannel = "codex_desktop:message-for-view";

  async function bridgeLog(level, message) {
    const text = `[tauri-bridge] ${message}`;
    if (!invoke) {
      if (level === "error") {
        console.error(text);
      } else if (level === "warn") {
        console.warn(text);
      } else {
        console.log(text);
      }
      return;
    }

    try {
      await invoke("send_message_from_view", {
        message: {
          type: "log-message",
          level,
          message: text,
        },
      });
    } catch {
      // swallow bridge logging failures to avoid blocking app startup
    }
  }

  const defaultMeta = {
    buildFlavor: "tauri",
    appVersion: "0.1.0",
    buildNumber: null,
    codexAppSessionId: "tauri-bootstrap",
  };
  let bridgeMeta = { ...defaultMeta };

  const workerCallbacks = new Map();
  const workerUnlisten = new Map();

  function workerChannel(workerId) {
    return `codex_desktop:worker:${workerId}:for-view`;
  }

  function dispatchHostMessage(payload) {
    window.dispatchEvent(
      new MessageEvent("message", {
        data: payload,
      }),
    );
  }

  async function loadBridgeMeta() {
    if (!invoke) {
      return;
    }

    try {
      const meta = await invoke("get_bridge_meta");
      if (meta && typeof meta === "object") {
        bridgeMeta = { ...bridgeMeta, ...meta };
      }
    } catch (error) {
      console.warn("[tauri-bridge] get_bridge_meta failed", error);
    }
  }

  async function ensureWorkerListener(workerId) {
    if (!listen || workerUnlisten.has(workerId)) {
      return;
    }

    const unlisten = await listen(workerChannel(workerId), (event) => {
      const callbacks = workerCallbacks.get(workerId);
      if (!callbacks || callbacks.size === 0) {
        return;
      }
      callbacks.forEach((callback) => {
        try {
          callback(event.payload);
        } catch (error) {
          console.warn("[tauri-bridge] worker callback failed", error);
        }
      });
    });

    workerUnlisten.set(workerId, unlisten);
  }

  function getPathForFile(file) {
    if (file && typeof file.path === "string") {
      return file.path;
    }
    return null;
  }

  if (listen) {
    listen(messageChannel, (event) => {
      dispatchHostMessage(event.payload);
    }).catch((error) => {
      console.warn("[tauri-bridge] listen message channel failed", error);
      bridgeLog("error", `listen message channel failed: ${String(error?.message ?? error)}`);
    });
  } else {
    bridgeLog("warn", "event.listen is unavailable in this runtime");
  }

  loadBridgeMeta();

  bridgeLog(
    "info",
    `bootstrap pathname=${window.location.pathname} search=${window.location.search}`,
  );

  window.addEventListener("error", (event) => {
    const detail = `${event.message ?? "unknown"} @ ${event.filename ?? "unknown"}:${event.lineno ?? 0}:${event.colno ?? 0}`;
    bridgeLog("error", `window.error ${detail}`);
  });

  window.addEventListener("unhandledrejection", (event) => {
    const reason = event.reason;
    const detail =
      reason && typeof reason === "object"
        ? reason.stack || reason.message || JSON.stringify(reason)
        : String(reason);
    bridgeLog("error", `window.unhandledrejection ${detail}`);
  });

  window.codexWindowType = "electron";
  document.documentElement.dataset.codexWindowType = "electron";
  document.documentElement.dataset.windowType = "electron";

  window.electronBridge = {
    windowType: "electron",
    async sendMessageFromView(message) {
      if (!invoke) {
        throw new Error("Tauri invoke is unavailable");
      }
      await invoke("send_message_from_view", { message });
    },
    getPathForFile,
    async sendWorkerMessageFromView(workerId, message) {
      if (!invoke) {
        throw new Error("Tauri invoke is unavailable");
      }
      await invoke("send_worker_message_from_view", {
        workerId,
        worker_id: workerId,
        message,
      });
    },
    subscribeToWorkerMessages(workerId, callback) {
      const callbacks = workerCallbacks.get(workerId) ?? new Set();
      callbacks.add(callback);
      workerCallbacks.set(workerId, callbacks);
      ensureWorkerListener(workerId);

      return () => {
        const set = workerCallbacks.get(workerId);
        if (!set) {
          return;
        }
        set.delete(callback);
        if (set.size > 0) {
          return;
        }
        workerCallbacks.delete(workerId);
        const unlisten = workerUnlisten.get(workerId);
        if (unlisten) {
          unlisten();
          workerUnlisten.delete(workerId);
        }
      };
    },
    async showContextMenu(items) {
      if (!invoke) {
        return { id: null };
      }
      return invoke("show_context_menu", { items });
    },
    async triggerSentryTestError() {
      if (!invoke) {
        return;
      }
      await invoke("trigger_sentry_test_error");
    },
    getSentryInitOptions() {
      return {
        buildFlavor: bridgeMeta.buildFlavor,
        appVersion: bridgeMeta.appVersion,
        buildNumber: bridgeMeta.buildNumber,
        codexAppSessionId: bridgeMeta.codexAppSessionId,
      };
    },
    getAppSessionId() {
      return bridgeMeta.codexAppSessionId;
    },
    getBuildFlavor() {
      return bridgeMeta.buildFlavor;
    },
  };
})();
