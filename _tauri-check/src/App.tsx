import { FormEvent, useCallback, useEffect, useMemo, useRef, useState } from "react";
import "./App.css";

type SideNavItem = {
  id: string;
  icon: string;
  label: string;
};

type SessionSummary = {
  id: string;
  title: string;
  createdAt?: string;
  cwd?: string;
};

type SessionTrace = {
  kind: string;
  title?: string;
  text?: string;
  command?: string;
  status?: string;
  exitCode?: number;
  output?: string;
};

type SessionMessage = {
  id: string;
  role: string;
  text: string;
  trace: SessionTrace[];
};

type TimelineItem = {
  id: string;
  kind: "command" | "note" | "divider";
  text: string;
};

const sideNav: SideNavItem[] = [
  { id: "automation", icon: "◌", label: "自动化" },
  { id: "skills", icon: "◇", label: "技能" },
  { id: "connections", icon: "◎", label: "连接设备" },
];

const DEFAULT_BACKEND_URL = "http://127.0.0.1:5000";
const BACKEND_URL_STORAGE_KEY = "relayouter.backendUrl";
const DEVICE_TOKEN_STORAGE_KEY = "relayouter.deviceToken";

function makeId(prefix: string): string {
  if (typeof crypto !== "undefined" && typeof crypto.randomUUID === "function") {
    return `${prefix}-${crypto.randomUUID()}`;
  }

  return `${prefix}-${Date.now()}-${Math.random().toString(16).slice(2, 10)}`;
}

function asRecord(value: unknown): Record<string, unknown> | null {
  if (value !== null && typeof value === "object" && !Array.isArray(value)) {
    return value as Record<string, unknown>;
  }

  return null;
}

function readString(record: Record<string, unknown>, key: string): string | undefined {
  const value = record[key];
  return typeof value === "string" ? value : undefined;
}

function readNumber(record: Record<string, unknown>, key: string): number | undefined {
  const value = record[key];
  return typeof value === "number" ? value : undefined;
}

function normalizeBaseUrl(raw: string): string {
  let value = raw.trim();
  if (value.length === 0) {
    return "";
  }

  if (!value.startsWith("http://") && !value.startsWith("https://")) {
    value = `http://${value}`;
  }

  return value.replace(/\/+$/, "");
}

function toWsUrl(baseUrl: string): string {
  if (baseUrl.startsWith("https://")) {
    return `wss://${baseUrl.slice("https://".length)}/ws`;
  }

  return `ws://${baseUrl.replace("http://", "")}/ws`;
}

function compactText(value: string, maxLength = 180): string {
  const normalized = value.replace(/\s+/g, " ").trim();
  if (normalized.length <= maxLength) {
    return normalized;
  }

  return `${normalized.slice(0, maxLength)}...`;
}

function formatAge(createdAt?: string): string {
  if (!createdAt) {
    return "未知";
  }

  const created = new Date(createdAt);
  if (Number.isNaN(created.getTime())) {
    return "未知";
  }

  const minutes = Math.max(1, Math.floor((Date.now() - created.getTime()) / 60000));
  if (minutes < 60) {
    return `${minutes} 分钟`;
  }

  const hours = Math.floor(minutes / 60);
  if (hours < 24) {
    return `${hours} 小时`;
  }

  const days = Math.floor(hours / 24);
  if (days < 7) {
    return `${days} 天`;
  }

  const weeks = Math.floor(days / 7);
  return `${weeks} 周`;
}

function parseErrorMessage(error: unknown): string {
  if (error instanceof Error) {
    return error.message;
  }

  return "未知错误";
}

function parseSessions(payload: unknown): SessionSummary[] {
  if (!Array.isArray(payload)) {
    return [];
  }

  const sessions: SessionSummary[] = [];
  for (const item of payload) {
    const record = asRecord(item);
    if (!record) {
      continue;
    }

    const id = readString(record, "id");
    const title = readString(record, "title");
    if (!id || !title) {
      continue;
    }

    sessions.push({
      id,
      title,
      createdAt: readString(record, "createdAt"),
      cwd: readString(record, "cwd"),
    });
  }

  return sessions;
}

function parseTrace(payload: unknown): SessionTrace[] {
  if (!Array.isArray(payload)) {
    return [];
  }

  const traces: SessionTrace[] = [];
  for (const entry of payload) {
    const record = asRecord(entry);
    if (!record) {
      continue;
    }

    const kind = readString(record, "kind");
    if (!kind) {
      continue;
    }

    traces.push({
      kind,
      title: readString(record, "title"),
      text: readString(record, "text"),
      command: readString(record, "command"),
      status: readString(record, "status"),
      exitCode: readNumber(record, "exitCode"),
      output: readString(record, "output"),
    });
  }

  return traces;
}

function parseMessages(payload: unknown): SessionMessage[] {
  if (!Array.isArray(payload)) {
    return [];
  }

  const messages: SessionMessage[] = [];
  payload.forEach((item, index) => {
    const record = asRecord(item);
    if (!record) {
      return;
    }

    const role = readString(record, "role");
    const text = readString(record, "text");
    if (!role || !text) {
      return;
    }

    messages.push({
      id: `${role}-${index}-${makeId("msg")}`,
      role,
      text,
      trace: parseTrace(record.trace),
    });
  });

  return messages;
}

function App() {
  const [backendUrlDraft, setBackendUrlDraft] = useState(
    () => localStorage.getItem(BACKEND_URL_STORAGE_KEY) ?? DEFAULT_BACKEND_URL,
  );
  const [tokenDraft, setTokenDraft] = useState(() => localStorage.getItem(DEVICE_TOKEN_STORAGE_KEY) ?? "");
  const [backendUrl, setBackendUrl] = useState(() => normalizeBaseUrl(localStorage.getItem(BACKEND_URL_STORAGE_KEY) ?? DEFAULT_BACKEND_URL));
  const [deviceToken, setDeviceToken] = useState(() => localStorage.getItem(DEVICE_TOKEN_STORAGE_KEY) ?? "");
  const [sessions, setSessions] = useState<SessionSummary[]>([]);
  const [selectedSessionId, setSelectedSessionId] = useState<string | null>(null);
  const [messages, setMessages] = useState<SessionMessage[]>([]);
  const [timeline, setTimeline] = useState<TimelineItem[]>([
    { id: makeId("timeline"), kind: "note", text: "等待连接后端..." },
  ]);
  const [composerText, setComposerText] = useState("");
  const [statusText, setStatusText] = useState("未连接");
  const [socketState, setSocketState] = useState<"connecting" | "connected" | "closed">("closed");
  const [isLoadingSessions, setIsLoadingSessions] = useState(false);
  const [isLoadingMessages, setIsLoadingMessages] = useState(false);
  const [isSending, setIsSending] = useState(false);
  const [socketEpoch, setSocketEpoch] = useState(0);

  const selectedSessionRef = useRef<string | null>(null);
  const socketRef = useRef<WebSocket | null>(null);

  useEffect(() => {
    selectedSessionRef.current = selectedSessionId;
  }, [selectedSessionId]);

  const pushTimeline = useCallback((item: Omit<TimelineItem, "id">) => {
    setTimeline((previous) => {
      const next = [...previous, { id: makeId("timeline"), ...item }];
      return next.length > 120 ? next.slice(next.length - 120) : next;
    });
  }, []);

  const getAuthHeaders = useCallback((): HeadersInit => {
    if (!deviceToken.trim()) {
      return {};
    }

    return {
      Authorization: `Bearer ${deviceToken.trim()}`,
    };
  }, [deviceToken]);

  const loadSessions = useCallback(async () => {
    if (!backendUrl) {
      return;
    }

    setIsLoadingSessions(true);
    try {
      const response = await fetch(`${backendUrl}/api/v1/sessions?limit=40`, {
        method: "GET",
        headers: getAuthHeaders(),
      });

      if (!response.ok) {
        throw new Error(`会话请求失败: HTTP ${response.status}`);
      }

      const payload = await response.json();
      const nextSessions = parseSessions(payload);
      setSessions(nextSessions);
      setSelectedSessionId((current) => {
        if (current && nextSessions.some((session) => session.id === current)) {
          return current;
        }

        return nextSessions[0]?.id ?? null;
      });
    } catch (error) {
      const message = parseErrorMessage(error);
      setStatusText(`读取会话失败：${message}`);
      pushTimeline({ kind: "note", text: `读取会话失败：${message}` });
    } finally {
      setIsLoadingSessions(false);
    }
  }, [backendUrl, getAuthHeaders, pushTimeline]);

  const loadMessages = useCallback(
    async (sessionId: string) => {
      if (!backendUrl || !sessionId.trim()) {
        return;
      }

      setIsLoadingMessages(true);
      try {
        const response = await fetch(`${backendUrl}/api/v1/sessions/${encodeURIComponent(sessionId)}/messages?limit=200`, {
          method: "GET",
          headers: getAuthHeaders(),
        });

        if (!response.ok) {
          throw new Error(`历史消息请求失败: HTTP ${response.status}`);
        }

        const payload = await response.json();
        setMessages(parseMessages(payload));
      } catch (error) {
        const message = parseErrorMessage(error);
        setStatusText(`读取历史消息失败：${message}`);
        pushTimeline({ kind: "note", text: `读取历史消息失败：${message}` });
      } finally {
        setIsLoadingMessages(false);
      }
    },
    [backendUrl, getAuthHeaders, pushTimeline],
  );

  const handleEnvelope = useCallback(
    (rawEnvelope: unknown) => {
      const envelope = asRecord(rawEnvelope);
      if (!envelope) {
        return;
      }

      const name = readString(envelope, "name");
      const data = asRecord(envelope.data) ?? {};
      if (!name) {
        return;
      }

      if (name === "chat.message") {
        const text = readString(data, "text");
        if (!text) {
          return;
        }

        const role = readString(data, "role") ?? "assistant";
        const sessionId = readString(data, "sessionId") ?? readString(data, "threadId");
        const selectedId = selectedSessionRef.current;

        if (sessionId && !selectedId) {
          setSelectedSessionId(sessionId);
        }

        if (!sessionId || !selectedId || sessionId === selectedId) {
          setMessages((previous) => [
            ...previous,
            {
              id: makeId("ws-message"),
              role,
              text,
              trace: [],
            },
          ]);
        }

        if (role === "assistant") {
          pushTimeline({ kind: "note", text: `助手回复：${compactText(text, 120)}` });
        }

        return;
      }

      if (name === "session.created") {
        const sessionId = readString(data, "sessionId");
        if (sessionId) {
          setSelectedSessionId(sessionId);
          void loadSessions();
          void loadMessages(sessionId);
          pushTimeline({ kind: "note", text: `创建新会话：${sessionId}` });
        }

        return;
      }

      if (name === "run.command") {
        const command = readString(data, "command");
        const status = readString(data, "status");
        const output = readString(data, "output");
        if (command) {
          pushTimeline({ kind: "command", text: status ? `${command} (${status})` : command });
        }

        if (output) {
          pushTimeline({ kind: "note", text: `输出：${compactText(output, 140)}` });
        }

        return;
      }

      if (name === "run.reasoning") {
        const text = readString(data, "text");
        if (text) {
          pushTimeline({ kind: "note", text: `思考：${compactText(text, 140)}` });
        }

        return;
      }

      if (name === "run.started") {
        pushTimeline({ kind: "divider", text: "运行开始" });
        return;
      }

      if (name === "run.completed") {
        pushTimeline({ kind: "divider", text: "运行完成" });
        return;
      }

      if (name === "run.canceled") {
        pushTimeline({ kind: "divider", text: "运行已取消" });
        return;
      }

      if (name === "run.failed" || name === "run.rejected" || name === "bridge.error") {
        const message = readString(data, "message") ?? readString(data, "reason") ?? "请求失败";
        pushTimeline({ kind: "note", text: `错误：${message}` });
      }
    },
    [loadMessages, loadSessions, pushTimeline],
  );

  useEffect(() => {
    if (!backendUrl) {
      return;
    }

    let disposed = false;
    setSocketState("connecting");
    setStatusText("正在连接实时通道...");

    const socket = new WebSocket(toWsUrl(backendUrl));
    socketRef.current = socket;

    socket.onopen = () => {
      if (disposed) {
        return;
      }

      setSocketState("connected");
      setStatusText("实时通道已连接");
      pushTimeline({ kind: "note", text: "实时通道已连接" });
    };

    socket.onclose = () => {
      if (disposed) {
        return;
      }

      setSocketState("closed");
      setStatusText("实时通道已断开");
      pushTimeline({ kind: "note", text: "实时通道已断开" });
    };

    socket.onerror = () => {
      if (disposed) {
        return;
      }

      setSocketState("closed");
      setStatusText("实时通道连接失败");
      pushTimeline({ kind: "note", text: "实时通道连接失败" });
    };

    socket.onmessage = (event) => {
      if (disposed) {
        return;
      }

      try {
        const envelope = JSON.parse(String(event.data)) as unknown;
        handleEnvelope(envelope);
      } catch {
        pushTimeline({ kind: "note", text: "收到无法解析的实时消息" });
      }
    };

    return () => {
      disposed = true;
      if (socketRef.current === socket) {
        socketRef.current = null;
      }

      socket.close();
    };
  }, [backendUrl, handleEnvelope, pushTimeline, socketEpoch]);

  useEffect(() => {
    void loadSessions();
  }, [loadSessions]);

  useEffect(() => {
    if (!selectedSessionId) {
      setMessages([]);
      return;
    }

    void loadMessages(selectedSessionId);
  }, [loadMessages, selectedSessionId]);

  const sessionItems = useMemo(
    () =>
      sessions.map((session) => ({
        id: session.id,
        title: session.title,
        preview: session.cwd ?? "工作目录未记录",
        age: formatAge(session.createdAt),
        isActive: session.id === selectedSessionId,
      })),
    [selectedSessionId, sessions],
  );

  const selectedThread = sessionItems.find((item) => item.isActive) ?? sessionItems[0];

  const reconnectSocket = () => {
    setSocketEpoch((value) => value + 1);
  };

  const applyConnectionSettings = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();

    const normalizedUrl = normalizeBaseUrl(backendUrlDraft);
    setBackendUrl(normalizedUrl);
    setDeviceToken(tokenDraft.trim());
    localStorage.setItem(BACKEND_URL_STORAGE_KEY, normalizedUrl);
    localStorage.setItem(DEVICE_TOKEN_STORAGE_KEY, tokenDraft.trim());
    setStatusText("连接配置已更新");
    pushTimeline({ kind: "note", text: `连接配置已更新：${normalizedUrl || "未设置地址"}` });
    setSocketEpoch((value) => value + 1);
  };

  const handleSend = (event: FormEvent<HTMLFormElement>) => {
    event.preventDefault();
    const prompt = composerText.trim();
    if (!prompt) {
      return;
    }

    const socket = socketRef.current;
    if (!socket || socket.readyState !== WebSocket.OPEN) {
      setStatusText("实时通道未连接，无法发送");
      pushTimeline({ kind: "note", text: "发送失败：实时通道未连接" });
      return;
    }

    setIsSending(true);
    try {
      const payload: Record<string, unknown> = { prompt };
      if (selectedSessionRef.current) {
        payload.sessionId = selectedSessionRef.current;
      }

      socket.send(
        JSON.stringify({
          protocolVersion: 1,
          type: "command",
          name: "chat.send",
          id: makeId("chat-send").replace(/[^a-zA-Z0-9]/g, ""),
          data: payload,
        }),
      );

      pushTimeline({ kind: "note", text: "已发送请求，等待模型返回..." });
      setComposerText("");
    } catch (error) {
      const message = parseErrorMessage(error);
      setStatusText(`发送失败：${message}`);
      pushTimeline({ kind: "note", text: `发送失败：${message}` });
    } finally {
      setIsSending(false);
    }
  };

  return (
    <div className="app-frame" data-theme="dark">
      <div className="shell">
        <aside className="sidebar">
          <div className="brand">
            <span className="brand-mark" aria-hidden="true">
              <svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round"><path d="M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z" /></svg>
            </span>
            <div>
              <p className="brand-overline">Codex Relayouter</p>
              <h2>桌面工作台</h2>
            </div>
          </div>

          <button className="create-thread" type="button">
            <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><line x1="12" y1="5" x2="12" y2="19" /><line x1="5" y1="12" x2="19" y2="12" /></svg>
            新线程
          </button>

          <nav className="side-nav" aria-label="快捷入口">
            {sideNav.map((item) => (
              <button key={item.id} type="button">
                <span aria-hidden="true">{item.icon}</span>
                {item.label}
              </button>
            ))}
          </nav>

          <section className="thread-panel">
            <header className="thread-panel-head">
              <h3>线程</h3>
              <button className="mini-ghost" type="button" aria-label="筛选" title="筛选">
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round"><polygon points="22 3 2 3 10 12.46 10 19 14 21 14 12.46 22 3" /></svg>
              </button>
            </header>

            <ul className="thread-list">
              {sessionItems.length === 0 ? (
                <li className="thread-empty">{isLoadingSessions ? "正在加载会话..." : "暂无会话，先发送一条消息试试"}</li>
              ) : (
                sessionItems.map((thread) => (
                  <li key={thread.id}>
                    <button
                      className={thread.isActive ? "thread-item active" : "thread-item"}
                      type="button"
                      onClick={() => setSelectedSessionId(thread.id)}
                    >
                      <div className="thread-main">
                        <span className="thread-title" title={thread.title}>
                          {thread.title}
                        </span>
                        <span className="thread-preview">{thread.preview}</span>
                      </div>
                      <span className="thread-meta">
                        <span className="age">{thread.age}</span>
                      </span>
                    </button>
                  </li>
                ))
              )}
            </ul>

            <button className="expand-more" type="button" onClick={() => void loadSessions()}>
              展开显示
            </button>
          </section>

          <div className="sidebar-footer">
            <span className={socketState === "connected" ? "status-dot" : "status-dot offline"} />
            {socketState === "connected" ? "已登录" : "未连接"}
          </div>
        </aside>

        <section className="workspace">
          <header className="workspace-head">
            <div className="workspace-title">
              <p className="workspace-path">Session / LastChat</p>
              <h1>{selectedThread?.title ?? "未选择会话"}</h1>
            </div>

            <div className="head-actions">
              <button type="button" onClick={() => void loadSessions()}>
                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" style={{ marginRight: 4, verticalAlign: 'middle' }}><polyline points="1 4 1 10 7 10" /><polyline points="23 20 23 14 17 14" /><path d="M20.49 9A9 9 0 0 0 5.64 5.64L1 10m22 4l-4.64 4.36A9 9 0 0 1 3.51 15" /></svg>
                刷新
              </button>
              <button type="button" onClick={reconnectSocket}>
                打开
              </button>
              <button className="stats" type="button">
                <span className="plus">提交</span>
                <span style={{ fontSize: 11, color: 'var(--green)', fontWeight: 600 }}>+{messages.length}</span>
                <span style={{ fontSize: 11, color: 'var(--red)', fontWeight: 600 }}>-{sessions.length}</span>
              </button>
            </div>
          </header>

          <div className="workspace-meta">
            <span className="meta-pill">后端：{backendUrl || "未设置"}</span>
            <span className="meta-pill">会话：{sessions.length}</span>
            <span className="meta-pill muted">{statusText}</span>
          </div>

          <form className="connection-form" onSubmit={applyConnectionSettings}>
            <label className="connection-field">
              后端地址
              <input
                type="text"
                value={backendUrlDraft}
                onChange={(event) => setBackendUrlDraft(event.currentTarget.value)}
                placeholder="http://127.0.0.1:5000"
              />
            </label>

            <label className="connection-field">
              设备令牌（可选）
              <input
                type="password"
                value={tokenDraft}
                onChange={(event) => setTokenDraft(event.currentTarget.value)}
                placeholder="remote 模式时填写"
              />
            </label>

            <button className="apply-button" type="submit">
              应用连接
            </button>
          </form>

          <main className="chat-scroll">
            <article className="assistant-card">
              <section className="message-stream">
                <h3 className="timeline-section">会话内容</h3>
                {isLoadingMessages ? <p className="message-hint">正在加载历史消息…</p> : null}
                {!isLoadingMessages && messages.length === 0 ? <p className="message-hint">暂无消息，发送一条试试。</p> : null}
                {messages.map((message) => (
                  <article
                    key={message.id}
                    className={message.role === "user" ? "message-bubble user" : "message-bubble assistant"}
                  >
                    <header className="message-header">
                      <span className="role-tag">{message.role === "user" ? "你" : "助手"}</span>
                    </header>
                    <p>{message.text}</p>
                    {message.trace.length > 0 ? (
                      <details className="trace-box">
                        <summary>展开运行细节（{message.trace.length}）</summary>
                        <ul>
                          {message.trace.map((trace, index) => (
                            <li key={`${message.id}-trace-${index}`}>
                              <span>{trace.kind}</span>
                              {trace.command ? <code>{trace.command}</code> : null}
                              {trace.text ? <span>{compactText(trace.text, 90)}</span> : null}
                            </li>
                          ))}
                        </ul>
                      </details>
                    ) : null}
                  </article>
                ))}
              </section>

              <section className="timeline-panel">
                <h3 className="timeline-section">运行状态</h3>
                {timeline.map((item, index) => {
                  const animationStyle = { animationDelay: `${index * 35}ms` };
                  if (item.kind === "divider") {
                    return (
                      <div key={item.id} className="timeline-divider" style={animationStyle}>
                        <span>{item.text}</span>
                      </div>
                    );
                  }

                  return (
                    <div key={item.id} className={`timeline-row ${item.kind === "command" ? "command" : "note"}`} style={animationStyle}>
                      <span className="timeline-dot" />
                      <p>{item.kind === "command" ? <code>{item.text}</code> : item.text}</p>
                    </div>
                  );
                })}
              </section>
            </article>
          </main>

          <footer className="composer">
            <form onSubmit={handleSend}>
              <textarea
                value={composerText}
                onChange={(event) => setComposerText(event.currentTarget.value)}
                placeholder="输入提示词并回车发送…"
                rows={3}
              />
              <div className="composer-row">
                <div className="composer-chips">
                  <button type="button">+ GPT-S3-Codex ▾</button>
                  <button type="button">超高 ▾</button>
                  <button type="button"><span className="chip-dot" /> full access ▾</button>
                  <button type="button">◎ 本地 ▾</button>
                  <button type="button">P feature ▾</button>
                </div>

                <button className="send-button" type="submit" disabled={isSending} aria-label="发送">
                  <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2.5" strokeLinecap="round" strokeLinejoin="round"><line x1="22" y1="2" x2="11" y2="13" /><polygon points="22 2 15 22 11 13 2 9 22 2" /></svg>
                </button>
              </div>
            </form>
          </footer>
        </section>
      </div>
    </div>
  );
}

export default App;
