// This module provides a compatibility host for the migrated Codex webview.
// It keeps the Electron message shape so the existing frontend can boot in Tauri.

use base64::engine::general_purpose::STANDARD as BASE64_STANDARD;
use base64::Engine;
use futures_util::{SinkExt, StreamExt};
use reqwest::header::{HeaderMap, HeaderName, HeaderValue};
use reqwest::Method;
use serde::{Deserialize, Serialize};
use serde_json::{json, Map, Value};
use std::collections::{HashMap, HashSet, VecDeque};
use std::env;
use std::fs;
use std::net::TcpListener;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::sync::Mutex;
use std::time::{Duration, SystemTime, UNIX_EPOCH};
use tauri::{Emitter, Manager, State, Window};
use tokio::sync::mpsc;
use tokio_tungstenite::connect_async;
use tokio_tungstenite::tungstenite::Message as WsMessage;
use url::Url;
use uuid::Uuid;

const CHANNEL_MESSAGE_FOR_VIEW: &str = "codex_desktop:message-for-view";
const VSCODE_FETCH_PREFIX: &str = "vscode://codex/";
const GLOBAL_KEY_ACTIVE_WORKSPACE_ROOTS: &str = "active-workspace-roots";
const GLOBAL_KEY_WORKSPACE_ROOT_OPTIONS: &str = "electron-saved-workspace-roots";
const GLOBAL_KEY_WORKSPACE_ROOT_LABELS: &str = "electron-workspace-root-labels";
const GLOBAL_KEY_PINNED_THREAD_IDS: &str = "pinned-thread-ids";
const DEFAULT_BRIDGE_HEALTH_PATH: &str = "api/v1/health";
const DEFAULT_BRIDGE_WS_PATH: &str = "ws";
const CONFIG_KEY_MODEL: &str = "model";
const CONFIG_KEY_MODEL_EFFORT: &str = "model_reasoning_effort";
const CONFIG_KEY_APPROVAL_POLICY: &str = "approval_policy";
const CONFIG_KEY_SANDBOX_MODE: &str = "sandbox_mode";
const DEFAULT_MODEL: &str = "gpt-5.2-codex";
const DEFAULT_REASONING_EFFORT: &str = "medium";
const DEFAULT_SANDBOX_MODE: &str = "workspace-write";
const DEFAULT_APPROVAL_POLICY: &str = "on-request";

#[derive(Clone, Serialize)]
#[serde(rename_all = "camelCase")]
struct BridgeMeta {
    build_flavor: String,
    app_version: String,
    build_number: Option<String>,
    codex_app_session_id: String,
}

#[derive(Clone, Default)]
struct WorkspaceState {
    roots: Vec<String>,
    labels: HashMap<String, String>,
    active_roots: Vec<String>,
}

#[derive(Clone)]
struct HostTurn {
    id: String,
    status: String,
    error: Option<Value>,
    items: Vec<Value>,
}

#[derive(Clone)]
struct HostThread {
    id: String,
    created_at: i64,
    updated_at: i64,
    preview: String,
    cwd: String,
    path: Option<String>,
    git_info: Option<Value>,
    source: Value,
    turns: Vec<HostTurn>,
    archived: bool,
}

impl HostThread {
    fn to_list_json(&self) -> Value {
        json!({
            "id": self.id,
            "createdAt": self.created_at,
            "updatedAt": self.updated_at,
            "preview": self.preview,
            "cwd": self.cwd,
            "path": self.path,
            "gitInfo": self.git_info,
            "source": self.source
        })
    }

    fn to_resume_json(&self) -> Value {
        let turns = self
            .turns
            .iter()
            .map(|turn| {
                json!({
                    "id": turn.id,
                    "status": turn.status,
                    "error": turn.error,
                    "items": turn.items
                })
            })
            .collect::<Vec<_>>();

        let mut thread_json = self.to_list_json();
        if let Value::Object(obj) = &mut thread_json {
            obj.insert("turns".to_string(), Value::Array(turns));
        }
        thread_json
    }
}

#[derive(Clone, Default)]
struct ThreadStore {
    threads: HashMap<String, HostThread>,
    order: Vec<String>,
}

#[derive(Default)]
struct BridgeRuntimeState {
    base_url: Option<String>,
    ws_url: Option<String>,
    ws_sender: Option<mpsc::UnboundedSender<String>>,
    process: Option<Child>,
    run_states: HashMap<String, RunBridgeState>,
    pending_turns: HashMap<String, VecDeque<String>>,
    approval_run_by_request: HashMap<String, String>,
}

#[derive(Default)]
struct RunBridgeState {
    thread_id: String,
    turn_id: String,
    assistant_item_id: Option<String>,
    started_items: HashSet<String>,
    completed_items: HashSet<String>,
    assistant_delta_seen: bool,
    item_payloads: HashMap<String, Value>,
}

#[derive(Clone, Default)]
struct CodexConfigSnapshot {
    model: Option<String>,
    model_reasoning_effort: Option<String>,
    approval_policy: Option<String>,
    sandbox_mode: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct BridgeSessionSummary {
    id: String,
    #[serde(default)]
    title: String,
    #[serde(default)]
    cwd: Option<String>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct BridgeSessionMessage {
    role: String,
    #[serde(default)]
    text: String,
    #[serde(default)]
    trace: Vec<BridgeSessionTraceEntry>,
}

#[derive(Debug, Deserialize)]
#[serde(rename_all = "camelCase")]
struct BridgeSessionTraceEntry {
    kind: String,
    #[serde(default)]
    title: Option<String>,
    #[serde(default)]
    text: Option<String>,
    #[serde(default)]
    command: Option<String>,
    #[serde(default)]
    status: Option<String>,
    #[serde(default)]
    exit_code: Option<i32>,
    #[serde(default)]
    output: Option<String>,
}

struct AppState {
    bridge_meta: BridgeMeta,
    persisted_atom_state: Mutex<Map<String, Value>>,
    shared_object_state: Mutex<Map<String, Value>>,
    shared_subscriptions: Mutex<HashMap<String, HashSet<String>>>,
    workspace_state: Mutex<WorkspaceState>,
    thread_store: Mutex<ThreadStore>,
    bridge_runtime: Mutex<BridgeRuntimeState>,
}

impl AppState {
    fn new() -> Self {
        let build_number = env::var("CODEX_BUILD_NUMBER")
            .ok()
            .map(|v| v.trim().to_owned())
            .filter(|v| !v.is_empty());

        Self {
            bridge_meta: BridgeMeta {
                build_flavor: "tauri".to_string(),
                app_version: env!("CARGO_PKG_VERSION").to_string(),
                build_number,
                codex_app_session_id: format!("tauri-{}", Uuid::new_v4()),
            },
            persisted_atom_state: Mutex::new(Map::new()),
            shared_object_state: Mutex::new(Map::new()),
            shared_subscriptions: Mutex::new(HashMap::new()),
            workspace_state: Mutex::new(WorkspaceState::default()),
            thread_store: Mutex::new(ThreadStore::default()),
            bridge_runtime: Mutex::new(BridgeRuntimeState::default()),
        }
    }
}

#[derive(Serialize)]
struct ContextMenuResult {
    id: Option<String>,
}

fn worker_channel(worker_id: &str) -> String {
    format!("codex_desktop:worker:{worker_id}:for-view")
}

fn message_type(message: &Value) -> Option<&str> {
    message.get("type").and_then(Value::as_str)
}

fn push_unique(values: &mut Vec<String>, candidate: String) {
    if !values.iter().any(|v| v == &candidate) {
        values.push(candidate);
    }
}

fn normalize_root_path(path: &Path) -> String {
    fs::canonicalize(path)
        .unwrap_or_else(|_| path.to_path_buf())
        .to_string_lossy()
        .to_string()
}

fn normalize_root_string(root: &str) -> String {
    normalize_root_path(Path::new(root))
}

fn derive_workspace_label(root: &str) -> String {
    Path::new(root)
        .file_name()
        .and_then(|name| name.to_str())
        .map(|name| name.trim().to_string())
        .filter(|name| !name.is_empty())
        .unwrap_or_else(|| root.to_string())
}

fn upsert_workspace_root(workspace: &mut WorkspaceState, root: String) -> String {
    push_unique(&mut workspace.roots, root.clone());
    let label = workspace
        .labels
        .entry(root.clone())
        .or_insert_with(|| derive_workspace_label(&root))
        .clone();
    label
}

fn emit_workspace_root_option_picked(
    window: &Window,
    root: &str,
    label: &str,
) -> Result<(), String> {
    emit_message_to_window(
        window,
        json!({
            "type": "workspace-root-option-picked",
            "root": root,
            "label": label
        }),
    )
}

fn emit_workspace_root_options_updated(
    app: &tauri::AppHandle,
    snapshot: &WorkspaceState,
) -> Result<(), String> {
    emit_message_to_app(
        app,
        json!({
            "type": "workspace-root-options-updated",
            "roots": snapshot.roots,
            "labels": snapshot.labels
        }),
    )
}

fn emit_active_workspace_roots_updated(
    app: &tauri::AppHandle,
    snapshot: &WorkspaceState,
) -> Result<(), String> {
    emit_message_to_app(
        app,
        json!({
            "type": "active-workspace-roots-updated",
            "roots": snapshot.active_roots
        }),
    )
}

fn emit_workspace_state_updates(
    app: &tauri::AppHandle,
    snapshot: &WorkspaceState,
) -> Result<(), String> {
    emit_workspace_root_options_updated(app, snapshot)?;
    emit_active_workspace_roots_updated(app, snapshot)
}

fn pick_workspace_root_folder() -> Option<PathBuf> {
    rfd::FileDialog::new().pick_folder()
}

fn resolve_workspace_root_from_message(message: &Value) -> Option<String> {
    if let Some(root) = message.get("root").and_then(Value::as_str) {
        if !root.trim().is_empty() {
            return Some(normalize_root_string(root));
        }
    }
    pick_workspace_root_folder()
        .as_deref()
        .map(normalize_root_path)
}

fn resolve_home_dir() -> Option<PathBuf> {
    env::var_os("USERPROFILE")
        .or_else(|| env::var_os("HOME"))
        .map(PathBuf::from)
}

fn create_default_workspace_root() -> Result<PathBuf, String> {
    let base = resolve_home_dir()
        .or_else(|| env::current_dir().ok())
        .ok_or_else(|| "Could not resolve a base directory for workspace creation".to_string())?;

    let projects_dir = base.join("Codex Projects");
    fs::create_dir_all(&projects_dir)
        .map_err(|e| format!("Failed to create workspace parent directory: {e}"))?;

    for idx in 1..=999 {
        let folder_name = if idx == 1 {
            "New Project".to_string()
        } else {
            format!("New Project {idx}")
        };
        let candidate = projects_dir.join(folder_name);
        if candidate.exists() {
            continue;
        }
        fs::create_dir_all(&candidate)
            .map_err(|e| format!("Failed to create workspace directory: {e}"))?;
        return Ok(candidate);
    }

    Err("Failed to allocate a unique default workspace directory".to_string())
}

fn parse_vscode_endpoint(url: &str) -> Option<String> {
    if !url.starts_with(VSCODE_FETCH_PREFIX) {
        return None;
    }

    let suffix = &url[VSCODE_FETCH_PREFIX.len()..];
    let endpoint = suffix
        .split(['?', '#'])
        .next()
        .unwrap_or_default()
        .trim_matches('/')
        .to_string();
    if endpoint.is_empty() {
        None
    } else {
        Some(endpoint)
    }
}

fn parse_json_body_from_message(message: &Value) -> Value {
    message
        .get("body")
        .and_then(Value::as_str)
        .and_then(|raw| serde_json::from_str::<Value>(raw).ok())
        .unwrap_or(Value::Null)
}

fn parse_string_array(value: Option<&Value>) -> Vec<String> {
    value
        .and_then(Value::as_array)
        .map(|items| {
            items
                .iter()
                .filter_map(Value::as_str)
                .map(normalize_root_string)
                .collect::<Vec<_>>()
        })
        .unwrap_or_default()
}

fn parse_pinned_thread_ids(value: Option<&Value>) -> Vec<String> {
    value
        .and_then(Value::as_array)
        .map(|items| {
            items
                .iter()
                .filter_map(Value::as_str)
                .map(|id| id.trim().to_string())
                .filter(|id| !id.is_empty())
                .collect::<Vec<_>>()
        })
        .unwrap_or_default()
}

fn default_codex_home_path() -> String {
    let home = resolve_home_dir()
        .or_else(|| env::current_dir().ok())
        .unwrap_or_else(|| PathBuf::from("."));
    normalize_root_path(&home.join(".codex"))
}

fn workspace_roots_response(snapshot: &WorkspaceState) -> Value {
    json!({
        "roots": snapshot.roots,
        "labels": snapshot.labels
    })
}

fn active_workspace_roots_response(snapshot: &WorkspaceState) -> Value {
    json!({
        "roots": snapshot.active_roots
    })
}

fn sync_workspace_persisted_state(
    state: &AppState,
    snapshot: &WorkspaceState,
) -> Result<(), String> {
    let mut guard = lock_or_err(&state.persisted_atom_state, "persisted_atom_state")?;
    guard.insert(
        GLOBAL_KEY_WORKSPACE_ROOT_OPTIONS.to_string(),
        Value::Array(
            snapshot
                .roots
                .iter()
                .map(|root| Value::String(root.clone()))
                .collect::<Vec<_>>(),
        ),
    );
    let labels = snapshot
        .labels
        .iter()
        .map(|(k, v)| (k.clone(), Value::String(v.clone())))
        .collect::<Map<String, Value>>();
    guard.insert(
        GLOBAL_KEY_WORKSPACE_ROOT_LABELS.to_string(),
        Value::Object(labels),
    );
    guard.insert(
        GLOBAL_KEY_ACTIVE_WORKSPACE_ROOTS.to_string(),
        Value::Array(
            snapshot
                .active_roots
                .iter()
                .map(|root| Value::String(root.clone()))
                .collect::<Vec<_>>(),
        ),
    );
    Ok(())
}

fn sync_workspace_from_global_state(
    state: &AppState,
    key: &str,
    value: &Value,
) -> Result<Option<WorkspaceState>, String> {
    let mut workspace = lock_or_err(&state.workspace_state, "workspace_state")?;
    let mut changed = false;

    match key {
        GLOBAL_KEY_WORKSPACE_ROOT_OPTIONS => {
            workspace.roots.clear();
            for root in parse_string_array(Some(value)) {
                push_unique(&mut workspace.roots, root);
            }
            let roots_snapshot = workspace.roots.clone();
            workspace
                .labels
                .retain(|root, _| roots_snapshot.iter().any(|candidate| candidate == root));
            for root in &roots_snapshot {
                workspace
                    .labels
                    .entry(root.clone())
                    .or_insert_with(|| derive_workspace_label(root));
            }
            workspace
                .active_roots
                .retain(|root| roots_snapshot.iter().any(|candidate| candidate == root));
            if workspace.active_roots.is_empty() {
                if let Some(first_root) = roots_snapshot.first().cloned() {
                    workspace.active_roots.push(first_root);
                }
            }
            changed = true;
        }
        GLOBAL_KEY_WORKSPACE_ROOT_LABELS => {
            if let Some(labels) = value.as_object() {
                for (root, label_value) in labels {
                    let Some(label) = label_value.as_str() else {
                        continue;
                    };
                    let root = normalize_root_string(root);
                    if workspace.roots.iter().any(|candidate| candidate == &root) {
                        workspace.labels.insert(root, label.to_string());
                        changed = true;
                    }
                }
            }
        }
        GLOBAL_KEY_ACTIVE_WORKSPACE_ROOTS => {
            let active_from_object: Vec<String> = value
                .as_object()
                .and_then(|obj| obj.get("roots"))
                .map(|roots| parse_string_array(Some(roots)))
                .unwrap_or_default();
            let next_active = if active_from_object.is_empty() {
                parse_string_array(Some(value))
            } else {
                active_from_object
            };

            workspace.active_roots.clear();
            for root in next_active {
                push_unique(&mut workspace.active_roots, root.clone());
                if !workspace.roots.iter().any(|candidate| candidate == &root) {
                    workspace.roots.push(root.clone());
                }
                workspace
                    .labels
                    .entry(root.clone())
                    .or_insert_with(|| derive_workspace_label(&root));
            }
            changed = true;
        }
        _ => {}
    }

    if changed {
        Ok(Some(workspace.clone()))
    } else {
        Ok(None)
    }
}

fn now_unix_seconds() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|d| d.as_secs() as i64)
        .unwrap_or(0)
}

fn default_thread_source() -> Value {
    json!({ "kind": "local" })
}

fn preferred_workspace_cwd(state: &AppState) -> String {
    if let Ok(workspace) = state.workspace_state.lock() {
        if let Some(root) = workspace.active_roots.first() {
            return root.clone();
        }
        if let Some(root) = workspace.roots.first() {
            return root.clone();
        }
    }
    "/".to_string()
}

fn first_text_from_input(input: &Value) -> String {
    input
        .as_array()
        .into_iter()
        .flatten()
        .find_map(|item| {
            if item.get("type").and_then(Value::as_str) != Some("text") {
                return None;
            }
            item.get("text")
                .and_then(Value::as_str)
                .map(|text| text.trim().to_string())
        })
        .filter(|text| !text.is_empty())
        .unwrap_or_default()
}

fn create_host_thread(id: Option<String>, cwd: String, preview: String) -> HostThread {
    let now = now_unix_seconds();
    HostThread {
        id: id.unwrap_or_else(|| Uuid::new_v4().to_string()),
        created_at: now,
        updated_at: now,
        preview,
        cwd,
        path: None,
        git_info: None,
        source: default_thread_source(),
        turns: Vec::new(),
        archived: false,
    }
}

fn promote_thread_in_order(store: &mut ThreadStore, thread_id: &str) {
    store.order.retain(|id| id != thread_id);
    store.order.insert(0, thread_id.to_string());
}

fn ensure_thread<'a>(
    store: &'a mut ThreadStore,
    thread_id: &str,
    fallback_cwd: &str,
) -> &'a mut HostThread {
    if !store.threads.contains_key(thread_id) {
        let thread = create_host_thread(
            Some(thread_id.to_string()),
            fallback_cwd.to_string(),
            String::new(),
        );
        store.threads.insert(thread_id.to_string(), thread);
    }
    promote_thread_in_order(store, thread_id);
    store
        .threads
        .get_mut(thread_id)
        .expect("thread must exist after ensure_thread")
}

fn emit_message_to_window(window: &Window, payload: Value) -> Result<(), String> {
    window
        .emit(CHANNEL_MESSAGE_FOR_VIEW, payload)
        .map_err(|e| format!("emit {CHANNEL_MESSAGE_FOR_VIEW} failed: {e}"))
}

fn emit_message_to_app(app: &tauri::AppHandle, payload: Value) -> Result<(), String> {
    app.emit(CHANNEL_MESSAGE_FOR_VIEW, payload)
        .map_err(|e| format!("broadcast {CHANNEL_MESSAGE_FOR_VIEW} failed: {e}"))
}

fn emit_worker_to_window(window: &Window, worker_id: &str, payload: Value) -> Result<(), String> {
    let channel = worker_channel(worker_id);
    window
        .emit(&channel, payload)
        .map_err(|e| format!("emit {channel} failed: {e}"))
}

fn resolve_api_base_url() -> String {
    if let Ok(value) = env::var("CODEX_API_BASE_URL") {
        let trimmed = value.trim();
        if !trimmed.is_empty() {
            return trimmed.trim_end_matches('/').to_string();
        }
    }

    let endpoint = env::var("CODEX_API_ENDPOINT")
        .ok()
        .unwrap_or_default()
        .to_lowercase();

    if endpoint == "localhost" {
        "http://localhost:8000/api".to_string()
    } else {
        "https://chatgpt.com/backend-api".to_string()
    }
}

fn ensure_absolute_url(url: &str) -> String {
    if url.starts_with("http://") || url.starts_with("https://") || url.starts_with("data:") {
        return url.to_string();
    }

    let base = resolve_api_base_url();
    format!("{}/{}", base, url.trim_start_matches('/'))
}

fn parse_headers(message: &Value) -> (HeaderMap, bool) {
    let mut headers = HeaderMap::new();
    let mut is_base64 = false;

    let Some(object) = message.get("headers").and_then(Value::as_object) else {
        return (headers, is_base64);
    };

    for (key, value) in object {
        if key.eq_ignore_ascii_case("x-codex-base64") {
            is_base64 = value.as_str() == Some("1");
            continue;
        }

        let Some(header_value) = value.as_str() else {
            continue;
        };

        let Ok(name) = HeaderName::from_bytes(key.as_bytes()) else {
            continue;
        };

        let Ok(parsed_value) = HeaderValue::from_str(header_value) else {
            continue;
        };

        headers.insert(name, parsed_value);
    }

    (headers, is_base64)
}

fn parse_request_body(message: &Value, base64_body: bool) -> Result<Option<Vec<u8>>, String> {
    let Some(body) = message.get("body") else {
        return Ok(None);
    };

    let Some(raw) = body.as_str() else {
        return Ok(None);
    };

    if base64_body {
        return BASE64_STANDARD
            .decode(raw)
            .map(Some)
            .map_err(|e| format!("decode base64 body failed: {e}"));
    }

    Ok(Some(raw.as_bytes().to_vec()))
}

fn headers_to_json(headers: &HeaderMap) -> Value {
    let mut map = Map::new();
    for (key, value) in headers {
        if let Ok(v) = value.to_str() {
            map.insert(key.to_string(), Value::String(v.to_string()));
        }
    }
    Value::Object(map)
}

fn json_fetch_error(request_id: &str, status: u16, error: impl Into<String>) -> Value {
    json!({
        "type": "fetch-response",
        "responseType": "error",
        "requestId": request_id,
        "status": status,
        "error": error.into(),
    })
}

fn json_fetch_success(
    request_id: &str,
    status: u16,
    headers: Value,
    body_json_string: String,
) -> Value {
    json!({
        "type": "fetch-response",
        "responseType": "success",
        "requestId": request_id,
        "status": status,
        "headers": headers,
        "bodyJsonString": body_json_string,
    })
}

fn is_http_url(url: &str) -> bool {
    Url::parse(url)
        .ok()
        .map(|u| u.scheme() == "http" || u.scheme() == "https")
        .unwrap_or(false)
}

fn mcp_error_payload(id: &str, message: impl Into<String>) -> Value {
    json!({
        "type": "mcp-response",
        "message": {
            "id": id,
            "error": {
                "message": message.into()
            }
        }
    })
}

fn mcp_result_payload(id: &str, result: Value) -> Value {
    json!({
        "type": "mcp-response",
        "message": {
            "id": id,
            "result": result
        }
    })
}

fn mcp_notification_payload(method: &str, params: Value) -> Value {
    json!({
        "type": "mcp-notification",
        "message": {
            "method": method,
            "params": params
        }
    })
}

fn mcp_request_payload(id: &str, method: &str, params: Value) -> Value {
    json!({
        "type": "mcp-request",
        "request": {
            "id": id,
            "method": method,
            "params": params
        }
    })
}

fn codex_config_path() -> Option<PathBuf> {
    resolve_home_dir().map(|home| home.join(".codex").join("config.toml"))
}

fn local_app_data_dir() -> Option<PathBuf> {
    env::var_os("LOCALAPPDATA")
        .map(PathBuf::from)
        .or_else(|| resolve_home_dir().map(|home| home.join("AppData").join("Local")))
}

fn bridge_base_url_from_env() -> Option<String> {
    let candidates = [
        "CODEX_BRIDGE_BASE_URL",
        "CODEX_BRIDGE_HTTP_URL",
        "CODEX_BRIDGE_URL",
    ];

    for key in candidates {
        let Ok(raw) = env::var(key) else {
            continue;
        };
        let trimmed = raw.trim();
        if trimmed.is_empty() {
            continue;
        }
        if let Some(normalized) = normalize_bridge_base_url(trimmed) {
            return Some(normalized);
        }
    }
    None
}

fn normalize_bridge_base_url(raw: &str) -> Option<String> {
    let trimmed = raw.trim().trim_end_matches('/');
    if trimmed.is_empty() {
        return None;
    }
    if let Some(rest) = trimmed.strip_prefix("ws://") {
        return Some(format!("http://{rest}"));
    }
    if let Some(rest) = trimmed.strip_prefix("wss://") {
        return Some(format!("https://{rest}"));
    }
    if trimmed.starts_with("http://") || trimmed.starts_with("https://") {
        return Some(trimmed.to_string());
    }
    Some(format!("http://{trimmed}"))
}

fn bridge_ws_url_from_base(base_url: &str) -> String {
    if let Some(rest) = base_url.strip_prefix("https://") {
        return format!("wss://{rest}/{}", DEFAULT_BRIDGE_WS_PATH);
    }
    if let Some(rest) = base_url.strip_prefix("http://") {
        return format!("ws://{rest}/{}", DEFAULT_BRIDGE_WS_PATH);
    }
    format!(
        "{}/{}",
        base_url.trim_end_matches('/'),
        DEFAULT_BRIDGE_WS_PATH
    )
}

fn read_cached_bridge_base_url() -> Option<String> {
    let path = local_app_data_dir()?
        .join("codex-relayouter")
        .join("connection_preferences.json");
    let content = fs::read_to_string(path).ok()?;
    let json: Value = serde_json::from_str(&content).ok()?;
    let port = json.get("port").and_then(Value::as_u64)?;
    if port == 0 || port > 65535 {
        return None;
    }
    Some(format!("http://127.0.0.1:{port}"))
}

fn pick_free_port() -> Result<u16, String> {
    let listener = TcpListener::bind("127.0.0.1:0")
        .map_err(|e| format!("Failed to reserve local port: {e}"))?;
    listener
        .local_addr()
        .map(|addr| addr.port())
        .map_err(|e| format!("Failed to read reserved local port: {e}"))
}

fn candidate_bridge_server_paths() -> Vec<PathBuf> {
    let mut candidates = Vec::new();

    if let Ok(path) = env::var("CODEX_BRIDGE_SERVER_EXE") {
        let candidate = PathBuf::from(path.trim());
        if !candidate.as_os_str().is_empty() {
            candidates.push(candidate);
        }
    }

    if let Ok(exe_path) = env::current_exe() {
        if let Some(base) = exe_path.parent() {
            candidates.push(
                base.join("bridge-server")
                    .join("codex-relayouter-server.exe"),
            );
            if let Some(parent) = base.parent() {
                candidates.push(
                    parent
                        .join("bridge-server")
                        .join("codex-relayouter-server.exe"),
                );
            }
        }
    }

    if let Ok(cwd) = env::current_dir() {
        let roots = [
            cwd.clone(),
            cwd.join(".."),
            cwd.join("..").join(".."),
            cwd.join("..").join("..").join(".."),
        ];
        for root in roots {
            candidates.push(
                root.join("bridge-server")
                    .join("codex-relayouter-server.exe"),
            );
            candidates.push(
                root.join("codex-relayouter-server")
                    .join("bin")
                    .join("Debug")
                    .join("net8.0")
                    .join("codex-relayouter-server.exe"),
            );
            candidates.push(
                root.join("codex-relayouter-server")
                    .join("bin")
                    .join("Release")
                    .join("net8.0")
                    .join("codex-relayouter-server.exe"),
            );
        }
    }

    candidates
}

fn find_bridge_server_executable() -> Option<PathBuf> {
    candidate_bridge_server_paths()
        .into_iter()
        .find(|path| path.exists())
}

async fn bridge_health_check(base_url: &str) -> bool {
    let health_url = format!(
        "{}/{}",
        base_url.trim_end_matches('/'),
        DEFAULT_BRIDGE_HEALTH_PATH
    );
    let client = reqwest::Client::new();
    match client.get(health_url).send().await {
        Ok(resp) => resp.status().is_success(),
        Err(_) => false,
    }
}

fn strip_toml_comment(line: &str) -> String {
    let mut in_double_quotes = false;
    let mut in_single_quotes = false;
    let mut escaped = false;
    let mut out = String::with_capacity(line.len());

    for ch in line.chars() {
        if escaped {
            escaped = false;
            out.push(ch);
            continue;
        }

        if in_double_quotes && ch == '\\' {
            escaped = true;
            out.push(ch);
            continue;
        }

        if !in_single_quotes && ch == '"' {
            in_double_quotes = !in_double_quotes;
            out.push(ch);
            continue;
        }

        if !in_double_quotes && ch == '\'' {
            in_single_quotes = !in_single_quotes;
            out.push(ch);
            continue;
        }

        if !in_double_quotes && !in_single_quotes && ch == '#' {
            break;
        }

        out.push(ch);
    }

    out
}

fn parse_toml_string(raw: &str) -> String {
    let raw = raw.trim();
    if raw.len() >= 2 && raw.starts_with('"') && raw.ends_with('"') {
        return raw[1..raw.len() - 1]
            .replace("\\n", "\n")
            .replace("\\r", "\r")
            .replace("\\t", "\t")
            .replace("\\\"", "\"")
            .replace("\\\\", "\\");
    }
    if raw.len() >= 2 && raw.starts_with('\'') && raw.ends_with('\'') {
        return raw[1..raw.len() - 1].to_string();
    }
    raw.to_string()
}

fn try_parse_root_toml_key(line: &str, key: &str) -> Option<String> {
    let trimmed = line.trim_start();
    if trimmed.starts_with('[') || trimmed.is_empty() || trimmed.starts_with('#') {
        return None;
    }
    if !trimmed.starts_with(key) {
        return None;
    }
    let after = trimmed[key.len()..].trim_start();
    if !after.starts_with('=') {
        return None;
    }
    let raw = strip_toml_comment(&after[1..]);
    let normalized = raw.trim();
    if normalized.is_empty() {
        return None;
    }
    Some(parse_toml_string(normalized))
}

fn read_codex_config_snapshot() -> CodexConfigSnapshot {
    let Some(path) = codex_config_path() else {
        return CodexConfigSnapshot::default();
    };
    let Ok(content) = fs::read_to_string(path) else {
        return CodexConfigSnapshot::default();
    };

    let mut snapshot = CodexConfigSnapshot::default();
    for line in content.lines() {
        let trimmed = line.trim_start();
        if trimmed.starts_with('[') {
            break;
        }
        if snapshot.model.is_none() {
            snapshot.model = try_parse_root_toml_key(line, CONFIG_KEY_MODEL);
        }
        if snapshot.model_reasoning_effort.is_none() {
            snapshot.model_reasoning_effort =
                try_parse_root_toml_key(line, CONFIG_KEY_MODEL_EFFORT);
        }
        if snapshot.approval_policy.is_none() {
            snapshot.approval_policy = try_parse_root_toml_key(line, CONFIG_KEY_APPROVAL_POLICY);
        }
        if snapshot.sandbox_mode.is_none() {
            snapshot.sandbox_mode = try_parse_root_toml_key(line, CONFIG_KEY_SANDBOX_MODE);
        }
    }
    snapshot
}

fn escape_toml_basic_string(text: &str) -> String {
    text.replace('\\', "\\\\").replace('"', "\\\"")
}

fn find_root_insert_index(lines: &[String]) -> usize {
    for (idx, line) in lines.iter().enumerate() {
        if line.trim_start().starts_with('[') {
            return idx;
        }
    }
    lines.len()
}

fn find_root_key_line_index(lines: &[String], key: &str) -> Option<usize> {
    for (idx, line) in lines.iter().enumerate() {
        let trimmed = line.trim_start();
        if trimmed.starts_with('[') {
            return None;
        }
        if trimmed.is_empty() || trimmed.starts_with('#') {
            continue;
        }
        if let Some(value) = trimmed.strip_prefix(key) {
            if value.trim_start().starts_with('=') {
                return Some(idx);
            }
        }
    }
    None
}

fn upsert_root_toml_key(
    lines: &mut Vec<String>,
    key: &str,
    value: Option<&str>,
    changed: &mut bool,
) {
    let existing_index = find_root_key_line_index(lines, key);

    match value {
        Some(text) => {
            let next_line = format!(r#"{key} = "{}""#, escape_toml_basic_string(text));
            if let Some(idx) = existing_index {
                if lines[idx] != next_line {
                    lines[idx] = next_line;
                    *changed = true;
                }
            } else {
                let insert_idx = find_root_insert_index(lines);
                lines.insert(insert_idx, next_line);
                *changed = true;
            }
        }
        None => {
            if let Some(idx) = existing_index {
                lines.remove(idx);
                *changed = true;
            }
        }
    }
}

fn extract_config_value(value: Option<&Value>) -> Option<Option<String>> {
    let value = value?;
    if value.is_null() {
        return Some(None);
    }
    let Some(text) = value.as_str() else {
        return None;
    };
    let normalized = text.trim();
    if normalized.is_empty() {
        return Some(None);
    }
    Some(Some(normalized.to_string()))
}

fn canonical_config_key(key: &str) -> Option<&'static str> {
    let normalized = key.trim();
    if normalized.is_empty() {
        return None;
    }
    let dot_suffix = normalized.rsplit('.').next().unwrap_or(normalized);
    match dot_suffix {
        CONFIG_KEY_MODEL => Some(CONFIG_KEY_MODEL),
        CONFIG_KEY_MODEL_EFFORT | "modelReasoningEffort" | "reasoningEffort" | "effort" => {
            Some(CONFIG_KEY_MODEL_EFFORT)
        }
        CONFIG_KEY_APPROVAL_POLICY | "approvalPolicy" => Some(CONFIG_KEY_APPROVAL_POLICY),
        CONFIG_KEY_SANDBOX_MODE | "sandboxMode" | "sandbox" => Some(CONFIG_KEY_SANDBOX_MODE),
        _ => None,
    }
}

fn collect_config_updates_from_object(
    object: &Map<String, Value>,
    updates: &mut HashMap<&'static str, Option<String>>,
) {
    for (key, value) in object {
        let Some(canonical) = canonical_config_key(key) else {
            continue;
        };
        if let Some(parsed) = extract_config_value(Some(value)) {
            updates.insert(canonical, parsed);
        }
    }
}

fn collect_config_updates(
    params: Option<&Map<String, Value>>,
) -> HashMap<&'static str, Option<String>> {
    let mut updates = HashMap::new();
    let Some(params) = params else {
        return updates;
    };

    collect_config_updates_from_object(params, &mut updates);

    if let Some(config) = params.get("config").and_then(Value::as_object) {
        collect_config_updates_from_object(config, &mut updates);
    }

    if let Some(writes) = params.get("writes").and_then(Value::as_array) {
        for entry in writes {
            let Some(obj) = entry.as_object() else {
                continue;
            };
            let key = obj
                .get("key")
                .or_else(|| obj.get("path"))
                .or_else(|| obj.get("name"))
                .and_then(Value::as_str)
                .unwrap_or_default();
            let Some(canonical) = canonical_config_key(key) else {
                continue;
            };
            let value = obj.get("value").or_else(|| obj.get("nextValue"));
            if let Some(parsed) = extract_config_value(value) {
                updates.insert(canonical, parsed);
            }
        }
    }

    updates
}

fn write_codex_config_updates(
    updates: &HashMap<&'static str, Option<String>>,
) -> Result<(), String> {
    if updates.is_empty() {
        return Ok(());
    }

    let Some(path) = codex_config_path() else {
        return Err("Could not resolve ~/.codex/config.toml".to_string());
    };
    if let Some(parent) = path.parent() {
        fs::create_dir_all(parent)
            .map_err(|e| format!("Failed to create config directory: {e}"))?;
    }

    let original = fs::read_to_string(&path).unwrap_or_default();
    let has_crlf = original.contains("\r\n");
    let newline = if has_crlf { "\r\n" } else { "\n" };
    let had_trailing_newline = original.ends_with('\n');
    let mut lines = if original.is_empty() {
        Vec::new()
    } else {
        original
            .lines()
            .map(ToString::to_string)
            .collect::<Vec<_>>()
    };

    let mut changed = false;
    let keys = [
        CONFIG_KEY_MODEL,
        CONFIG_KEY_MODEL_EFFORT,
        CONFIG_KEY_APPROVAL_POLICY,
        CONFIG_KEY_SANDBOX_MODE,
    ];

    for key in keys {
        if !updates.contains_key(key) {
            continue;
        }
        let value = updates.get(key).and_then(|v| v.clone());
        upsert_root_toml_key(&mut lines, key, value.as_deref(), &mut changed);
    }

    if !changed {
        return Ok(());
    }

    let mut next = lines.join(newline);
    if !next.is_empty() && had_trailing_newline {
        next.push_str(newline);
    }

    fs::write(&path, next).map_err(|e| format!("Failed to write config.toml: {e}"))
}

fn parse_first_string(value: Option<&Value>) -> Option<String> {
    let text = value?.as_str()?.trim();
    if text.is_empty() {
        None
    } else {
        Some(text.to_string())
    }
}

fn parse_optional_param(params: Option<&Map<String, Value>>, keys: &[&str]) -> Option<String> {
    let params = params?;
    for key in keys {
        if let Some(value) = parse_first_string(params.get(*key)) {
            return Some(value);
        }
    }
    None
}

fn extract_sandbox_from_params(params: Option<&Map<String, Value>>) -> Option<String> {
    if let Some(mode) =
        parse_optional_param(params, &[CONFIG_KEY_SANDBOX_MODE, "sandboxMode", "sandbox"])
    {
        return Some(mode);
    }
    if let Some(sandbox_policy) = params
        .and_then(|p| p.get("sandboxPolicy"))
        .and_then(Value::as_object)
    {
        if let Some(mode) = parse_first_string(sandbox_policy.get("mode")) {
            return Some(mode);
        }
    }
    None
}

fn extract_approval_policy_from_params(params: Option<&Map<String, Value>>) -> Option<String> {
    parse_optional_param(params, &[CONFIG_KEY_APPROVAL_POLICY, "approvalPolicy"])
}

fn extract_model_from_params(params: Option<&Map<String, Value>>) -> Option<String> {
    parse_optional_param(params, &[CONFIG_KEY_MODEL, "model"])
}

fn extract_effort_from_params(params: Option<&Map<String, Value>>) -> Option<String> {
    parse_optional_param(
        params,
        &[
            CONFIG_KEY_MODEL_EFFORT,
            "modelReasoningEffort",
            "reasoningEffort",
            "effort",
        ],
    )
}

fn extract_prompt_from_input(input: &Value) -> String {
    first_text_from_input(input)
}

fn parse_image_data_urls_from_input(input: &Value) -> Vec<String> {
    let mut urls = Vec::new();
    let Some(items) = input.as_array() else {
        return urls;
    };

    for entry in items {
        let Some(obj) = entry.as_object() else {
            continue;
        };
        let item_type = obj.get("type").and_then(Value::as_str).unwrap_or_default();
        if item_type != "image" && item_type != "input_image" {
            continue;
        }
        let Some(url) = obj
            .get("url")
            .or_else(|| obj.get("image_url"))
            .and_then(Value::as_str)
        else {
            continue;
        };
        let normalized = url.trim();
        if normalized.starts_with("data:image/") {
            urls.push(normalized.to_string());
        }
    }

    urls
}

async fn ensure_bridge_base_url(state: &AppState) -> Result<String, String> {
    let existing_base = {
        let runtime = lock_or_err(&state.bridge_runtime, "bridge_runtime")?;
        runtime.base_url.clone()
    };
    if let Some(base_url) = existing_base {
        if bridge_health_check(&base_url).await {
            return Ok(base_url);
        }
    }

    let mut candidates = Vec::new();
    if let Some(value) = bridge_base_url_from_env() {
        candidates.push(value);
    }
    if let Some(value) = read_cached_bridge_base_url() {
        candidates.push(value);
    }

    for candidate in candidates {
        if bridge_health_check(&candidate).await {
            let mut runtime = lock_or_err(&state.bridge_runtime, "bridge_runtime")?;
            runtime.base_url = Some(candidate.clone());
            runtime.ws_url = Some(bridge_ws_url_from_base(&candidate));
            return Ok(candidate);
        }
    }

    launch_bridge_server(state).await
}

async fn launch_bridge_server(state: &AppState) -> Result<String, String> {
    let executable = find_bridge_server_executable().ok_or_else(|| {
        "Could not find bridge-server executable. Set CODEX_BRIDGE_SERVER_EXE or build codex-relayouter-server.".to_string()
    })?;
    let port = pick_free_port()?;
    let base_url = format!("http://127.0.0.1:{port}");

    let mut command = Command::new(&executable);
    command
        .arg("--urls")
        .arg(format!("http://127.0.0.1:{port}"))
        .arg("--Bridge:Security:RemoteEnabled=false")
        .env("ASPNETCORE_ENVIRONMENT", "Production")
        .stdout(Stdio::null())
        .stderr(Stdio::null());

    if let Some(parent) = executable.parent() {
        command.current_dir(parent);
    }

    let child = command
        .spawn()
        .map_err(|e| format!("Failed to launch bridge-server from {:?}: {e}", executable))?;

    {
        let mut runtime = lock_or_err(&state.bridge_runtime, "bridge_runtime")?;
        if let Some(mut existing) = runtime.process.take() {
            let _ = existing.kill();
        }
        runtime.process = Some(child);
        runtime.base_url = Some(base_url.clone());
        runtime.ws_url = Some(bridge_ws_url_from_base(&base_url));
    }

    let deadline = SystemTime::now() + Duration::from_secs(12);
    while SystemTime::now() < deadline {
        if bridge_health_check(&base_url).await {
            return Ok(base_url);
        }
        tokio::time::sleep(Duration::from_millis(250)).await;
    }

    {
        let mut runtime = lock_or_err(&state.bridge_runtime, "bridge_runtime")?;
        if let Some(mut process) = runtime.process.take() {
            let _ = process.kill();
        }
        runtime.ws_sender = None;
    }

    Err("Timed out waiting for bridge-server health check".to_string())
}

fn bridge_endpoint(base_url: &str, path: &str) -> String {
    format!(
        "{}/{}",
        base_url.trim_end_matches('/'),
        path.trim_start_matches('/')
    )
}

async fn bridge_get_json(state: &AppState, path: &str) -> Result<Value, String> {
    let base_url = ensure_bridge_base_url(state).await?;
    let url = bridge_endpoint(&base_url, path);
    let response = reqwest::Client::new()
        .get(url)
        .send()
        .await
        .map_err(|e| format!("Bridge GET failed: {e}"))?;

    if !response.status().is_success() {
        return Err(format!(
            "Bridge GET failed with status {}",
            response.status()
        ));
    }

    response
        .json::<Value>()
        .await
        .map_err(|e| format!("Bridge GET JSON parse failed: {e}"))
}

async fn bridge_post_json(state: &AppState, path: &str, body: Value) -> Result<Value, String> {
    let base_url = ensure_bridge_base_url(state).await?;
    let url = bridge_endpoint(&base_url, path);
    let response = reqwest::Client::new()
        .post(url)
        .json(&body)
        .send()
        .await
        .map_err(|e| format!("Bridge POST failed: {e}"))?;

    if !response.status().is_success() {
        let status = response.status();
        let text = response.text().await.unwrap_or_default();
        return Err(format!("Bridge POST failed with status {status}: {text}"));
    }

    response
        .json::<Value>()
        .await
        .map_err(|e| format!("Bridge POST JSON parse failed: {e}"))
}

async fn ensure_bridge_ws_connected(
    app: &tauri::AppHandle,
    state: &AppState,
) -> Result<(), String> {
    {
        let runtime = lock_or_err(&state.bridge_runtime, "bridge_runtime")?;
        if runtime.ws_sender.is_some() {
            return Ok(());
        }
    }

    let base_url = ensure_bridge_base_url(state).await?;
    let ws_url = bridge_ws_url_from_base(&base_url);
    let (stream, _) = connect_async(&ws_url)
        .await
        .map_err(|e| format!("Failed to connect bridge websocket {ws_url}: {e}"))?;
    let (mut sink, mut stream) = stream.split();

    let (tx, mut rx) = mpsc::unbounded_channel::<String>();
    let writer_state = app.clone();
    tokio::spawn(async move {
        while let Some(text) = rx.recv().await {
            if sink.send(WsMessage::Text(text)).await.is_err() {
                break;
            }
        }

        if let Ok(mut runtime) = writer_state.state::<AppState>().bridge_runtime.lock() {
            runtime.ws_sender = None;
        }
    });

    let reader_state = app.clone();
    tokio::spawn(async move {
        while let Some(next) = stream.next().await {
            let Ok(message) = next else {
                break;
            };
            let text = match message {
                WsMessage::Text(text) => text,
                WsMessage::Binary(bytes) => match String::from_utf8(bytes) {
                    Ok(text) => text,
                    Err(_) => continue,
                },
                WsMessage::Close(_) => break,
                _ => continue,
            };

            let Ok(envelope) = serde_json::from_str::<Value>(&text) else {
                continue;
            };
            if let Err(error) = handle_bridge_envelope(&reader_state, envelope).await {
                eprintln!("[tauri-host] bridge event handling failed: {error}");
            }
        }

        if let Ok(mut runtime) = reader_state.state::<AppState>().bridge_runtime.lock() {
            runtime.ws_sender = None;
            runtime.run_states.clear();
            runtime.approval_run_by_request.clear();
        }
    });

    let mut runtime = lock_or_err(&state.bridge_runtime, "bridge_runtime")?;
    runtime.ws_sender = Some(tx);
    runtime.ws_url = Some(ws_url);
    Ok(())
}

async fn send_bridge_command(
    app: &tauri::AppHandle,
    state: &AppState,
    name: &str,
    data: Value,
) -> Result<(), String> {
    ensure_bridge_ws_connected(app, state).await?;
    let sender = {
        let runtime = lock_or_err(&state.bridge_runtime, "bridge_runtime")?;
        runtime
            .ws_sender
            .clone()
            .ok_or_else(|| "Bridge websocket sender is unavailable".to_string())?
    };

    let envelope = json!({
        "protocolVersion": 1,
        "type": "command",
        "name": name,
        "id": Uuid::new_v4().to_string(),
        "data": data
    });
    let payload = serde_json::to_string(&envelope)
        .map_err(|e| format!("Serialize bridge command failed: {e}"))?;
    sender
        .send(payload)
        .map_err(|_| "Bridge websocket channel is closed".to_string())
}

async fn bridge_list_sessions(
    state: &AppState,
    limit: usize,
) -> Result<Vec<BridgeSessionSummary>, String> {
    let clamped = limit.clamp(1, 200);
    let value = bridge_get_json(state, &format!("api/v1/sessions?limit={clamped}")).await?;
    serde_json::from_value::<Vec<BridgeSessionSummary>>(value)
        .map_err(|e| format!("Parse sessions list failed: {e}"))
}

fn bridge_summary_to_thread_list_json(summary: &BridgeSessionSummary) -> Value {
    let now = now_unix_seconds();
    let preview = if summary.title.trim().is_empty() {
        summary.id.clone()
    } else {
        summary.title.clone()
    };
    json!({
        "id": summary.id,
        "createdAt": now,
        "updatedAt": now,
        "preview": preview,
        "cwd": summary.cwd.clone().unwrap_or_else(|| "/".to_string()),
        "path": Value::Null,
        "gitInfo": Value::Null,
        "source": default_thread_source()
    })
}

fn trace_entry_to_item(entry: &BridgeSessionTraceEntry) -> Option<Value> {
    if entry.kind.eq_ignore_ascii_case("reasoning") {
        let text = entry
            .text
            .as_ref()
            .map(|s| s.trim().to_string())
            .filter(|s| !s.is_empty())?;
        let summary_text = if let Some(title) = entry
            .title
            .as_ref()
            .map(|s| s.trim().to_string())
            .filter(|s| !s.is_empty())
        {
            format!("{title}: {text}")
        } else {
            text
        };
        return Some(json!({
            "id": format!("reasoning-{}", Uuid::new_v4()),
            "type": "reasoning",
            "summary": [summary_text]
        }));
    }

    if entry.kind.eq_ignore_ascii_case("command") {
        return Some(json!({
            "id": format!("command-{}", Uuid::new_v4()),
            "type": "commandExecution",
            "command": entry.command.clone().unwrap_or_default(),
            "status": entry.status.clone().unwrap_or_else(|| "completed".to_string()),
            "exitCode": entry.exit_code,
            "aggregatedOutput": entry.output
        }));
    }

    None
}

fn push_history_turn(turns: &mut Vec<Value>, items: &mut Vec<Value>) {
    if items.is_empty() {
        return;
    }
    turns.push(json!({
        "id": Uuid::new_v4().to_string(),
        "status": "completed",
        "error": Value::Null,
        "items": items.clone()
    }));
    items.clear();
}

async fn bridge_thread_turns(state: &AppState, thread_id: &str) -> Result<Vec<Value>, String> {
    let value = bridge_get_json(
        state,
        &format!("api/v1/sessions/{thread_id}/messages?limit=200"),
    )
    .await?;
    let messages = serde_json::from_value::<Vec<BridgeSessionMessage>>(value)
        .map_err(|e| format!("Parse session messages failed: {e}"))?;

    let mut turns = Vec::new();
    let mut current_items = Vec::new();

    for message in messages {
        let role = message.role.trim().to_lowercase();
        match role.as_str() {
            "user" => {
                push_history_turn(&mut turns, &mut current_items);
                current_items.push(json!({
                    "id": format!("user-{}", Uuid::new_v4()),
                    "type": "userMessage",
                    "content": [
                        { "type": "text", "text": message.text }
                    ]
                }));
            }
            "assistant" => {
                for trace in &message.trace {
                    if let Some(item) = trace_entry_to_item(trace) {
                        current_items.push(item);
                    }
                }
                current_items.push(json!({
                    "id": format!("assistant-{}", Uuid::new_v4()),
                    "type": "agentMessage",
                    "text": message.text
                }));
                push_history_turn(&mut turns, &mut current_items);
            }
            _ => {}
        }
    }

    push_history_turn(&mut turns, &mut current_items);
    Ok(turns)
}

async fn bridge_session_settings(
    state: &AppState,
    thread_id: &str,
) -> (Option<String>, Option<String>) {
    let Ok(value) = bridge_get_json(state, &format!("api/v1/sessions/{thread_id}/settings")).await
    else {
        let config = read_codex_config_snapshot();
        return (config.approval_policy, config.sandbox_mode);
    };

    let approval_policy = value
        .get("approvalPolicy")
        .or_else(|| value.get(CONFIG_KEY_APPROVAL_POLICY))
        .and_then(Value::as_str)
        .map(|v| v.trim().to_string())
        .filter(|v| !v.is_empty());
    let sandbox_mode = value
        .get("sandbox")
        .or_else(|| value.get("sandboxMode"))
        .or_else(|| value.get(CONFIG_KEY_SANDBOX_MODE))
        .and_then(Value::as_str)
        .map(|v| v.trim().to_string())
        .filter(|v| !v.is_empty());

    (approval_policy, sandbox_mode)
}

async fn bridge_read_thread(
    state: &AppState,
    thread_id: &str,
    include_turns: bool,
    requested_cwd: Option<String>,
) -> Result<Value, String> {
    let summaries = bridge_list_sessions(state, 200).await.unwrap_or_default();
    let summary = summaries.into_iter().find(|s| s.id == thread_id);
    let cwd = requested_cwd
        .or_else(|| summary.as_ref().and_then(|s| s.cwd.clone()))
        .unwrap_or_else(|| preferred_workspace_cwd(state));
    let preview = summary
        .as_ref()
        .map(|s| s.title.clone())
        .filter(|t| !t.trim().is_empty())
        .unwrap_or_else(|| thread_id.to_string());

    let turns = if include_turns {
        bridge_thread_turns(state, thread_id).await?
    } else {
        Vec::new()
    };

    Ok(json!({
        "id": thread_id,
        "createdAt": now_unix_seconds(),
        "updatedAt": now_unix_seconds(),
        "preview": preview,
        "cwd": cwd,
        "path": Value::Null,
        "gitInfo": Value::Null,
        "source": default_thread_source(),
        "turns": turns
    }))
}

fn queue_pending_turn(state: &AppState, thread_id: &str, turn_id: &str) -> Result<(), String> {
    let mut runtime = lock_or_err(&state.bridge_runtime, "bridge_runtime")?;
    runtime
        .pending_turns
        .entry(thread_id.to_string())
        .or_default()
        .push_back(turn_id.to_string());
    Ok(())
}

fn build_turn_started_notification(thread_id: &str, turn_id: &str) -> Value {
    mcp_notification_payload(
        "turn/started",
        json!({
            "threadId": thread_id,
            "turn": {
                "id": turn_id,
                "status": "inProgress",
                "error": Value::Null
            }
        }),
    )
}

fn run_context(
    state: &AppState,
    run_id: &str,
    thread_hint: Option<&str>,
) -> Result<Option<(String, String)>, String> {
    let mut runtime = lock_or_err(&state.bridge_runtime, "bridge_runtime")?;
    let run = runtime.run_states.entry(run_id.to_string()).or_default();
    if run.thread_id.is_empty() {
        if let Some(thread_id) = thread_hint {
            let thread_id = thread_id.trim();
            if !thread_id.is_empty() {
                run.thread_id = thread_id.to_string();
            }
        }
    }
    if run.turn_id.is_empty() {
        run.turn_id = Uuid::new_v4().to_string();
    }

    if run.thread_id.is_empty() {
        return Ok(None);
    }

    Ok(Some((run.thread_id.clone(), run.turn_id.clone())))
}

fn mark_item_started(
    state: &AppState,
    run_id: &str,
    item_id: &str,
    payload: Value,
) -> Result<bool, String> {
    let mut runtime = lock_or_err(&state.bridge_runtime, "bridge_runtime")?;
    let run = runtime.run_states.entry(run_id.to_string()).or_default();
    if run.started_items.contains(item_id) {
        return Ok(false);
    }
    run.started_items.insert(item_id.to_string());
    run.item_payloads.insert(item_id.to_string(), payload);
    Ok(true)
}

fn mark_item_completed(
    state: &AppState,
    run_id: &str,
    item_id: &str,
    payload: Value,
) -> Result<bool, String> {
    let mut runtime = lock_or_err(&state.bridge_runtime, "bridge_runtime")?;
    let run = runtime.run_states.entry(run_id.to_string()).or_default();
    if run.completed_items.contains(item_id) {
        return Ok(false);
    }
    run.completed_items.insert(item_id.to_string());
    run.item_payloads.insert(item_id.to_string(), payload);
    Ok(true)
}

async fn handle_bridge_envelope(app: &tauri::AppHandle, envelope: Value) -> Result<(), String> {
    if envelope.get("type").and_then(Value::as_str) != Some("event") {
        return Ok(());
    }

    let Some(event_name) = envelope.get("name").and_then(Value::as_str) else {
        return Ok(());
    };
    let data = envelope.get("data").cloned().unwrap_or(Value::Null);
    let state_handle = app.state::<AppState>();
    let state = state_handle.inner();

    match event_name {
        "bridge.connected" => {}
        "session.created" => {
            let run_id = data
                .get("runId")
                .and_then(Value::as_str)
                .unwrap_or_default();
            let session_id = data
                .get("sessionId")
                .and_then(Value::as_str)
                .unwrap_or_default()
                .trim();
            if run_id.is_empty() || session_id.is_empty() {
                return Ok(());
            }

            {
                let mut runtime = lock_or_err(&state.bridge_runtime, "bridge_runtime")?;
                let run = runtime.run_states.entry(run_id.to_string()).or_default();
                run.thread_id = session_id.to_string();
            }

            let thread_payload = json!({
                "thread": {
                    "id": session_id,
                    "createdAt": now_unix_seconds(),
                    "updatedAt": now_unix_seconds(),
                    "preview": session_id,
                    "cwd": preferred_workspace_cwd(state),
                    "path": Value::Null,
                    "gitInfo": Value::Null,
                    "source": default_thread_source()
                }
            });
            let _ = emit_message_to_app(
                app,
                mcp_notification_payload("thread/started", thread_payload),
            );
        }
        "run.started" => {
            let run_id = data
                .get("runId")
                .and_then(Value::as_str)
                .unwrap_or_default();
            let thread_id = data
                .get("sessionId")
                .or_else(|| data.get("threadId"))
                .and_then(Value::as_str)
                .unwrap_or_default()
                .trim();
            if run_id.is_empty() || thread_id.is_empty() {
                return Ok(());
            }

            let turn_id = {
                let mut runtime = lock_or_err(&state.bridge_runtime, "bridge_runtime")?;
                let next_turn = runtime
                    .pending_turns
                    .entry(thread_id.to_string())
                    .or_default()
                    .pop_front()
                    .unwrap_or_else(|| Uuid::new_v4().to_string());
                let run = runtime.run_states.entry(run_id.to_string()).or_default();
                run.thread_id = thread_id.to_string();
                run.turn_id = next_turn.clone();
                next_turn
            };

            let _ = emit_message_to_app(app, build_turn_started_notification(thread_id, &turn_id));
        }
        "turn.started" => {
            let run_id = data
                .get("runId")
                .and_then(Value::as_str)
                .unwrap_or_default();
            let thread_id = data
                .get("threadId")
                .and_then(Value::as_str)
                .unwrap_or_default()
                .trim();
            let turn_id = data
                .get("turnId")
                .and_then(Value::as_str)
                .unwrap_or_default()
                .trim();
            if run_id.is_empty() || thread_id.is_empty() || turn_id.is_empty() {
                return Ok(());
            }

            let should_emit = {
                let mut runtime = lock_or_err(&state.bridge_runtime, "bridge_runtime")?;
                let run = runtime.run_states.entry(run_id.to_string()).or_default();
                let was_empty = run.turn_id.is_empty();
                run.thread_id = thread_id.to_string();
                if run.turn_id.is_empty() {
                    run.turn_id = turn_id.to_string();
                }
                was_empty
            };

            if should_emit {
                let _ =
                    emit_message_to_app(app, build_turn_started_notification(thread_id, turn_id));
            }
        }
        "chat.message.delta" => {
            let run_id = data
                .get("runId")
                .and_then(Value::as_str)
                .unwrap_or_default();
            let item_id = data
                .get("itemId")
                .and_then(Value::as_str)
                .unwrap_or_default();
            let delta = data
                .get("delta")
                .and_then(Value::as_str)
                .unwrap_or_default();
            if run_id.is_empty() || item_id.is_empty() || delta.is_empty() {
                return Ok(());
            }

            let thread_hint = data
                .get("sessionId")
                .or_else(|| data.get("threadId"))
                .and_then(Value::as_str);
            let Some((thread_id, turn_id)) = run_context(state, run_id, thread_hint)? else {
                return Ok(());
            };

            let item_payload = json!({
                "id": item_id,
                "type": "agentMessage",
                "text": Value::Null
            });
            if mark_item_started(state, run_id, item_id, item_payload.clone())? {
                let _ = emit_message_to_app(
                    app,
                    mcp_notification_payload(
                        "item/started",
                        json!({
                            "threadId": thread_id,
                            "turnId": turn_id,
                            "item": item_payload
                        }),
                    ),
                );
            }

            {
                let mut runtime = lock_or_err(&state.bridge_runtime, "bridge_runtime")?;
                let run = runtime.run_states.entry(run_id.to_string()).or_default();
                run.assistant_item_id = Some(item_id.to_string());
                run.assistant_delta_seen = true;
            }

            let _ = emit_message_to_app(
                app,
                mcp_notification_payload(
                    "item/agentMessage/delta",
                    json!({
                        "threadId": thread_id,
                        "turnId": turn_id,
                        "itemId": item_id,
                        "delta": delta
                    }),
                ),
            );
        }
        "chat.message" => {
            let run_id = data
                .get("runId")
                .and_then(Value::as_str)
                .unwrap_or_default();
            let role = data.get("role").and_then(Value::as_str).unwrap_or_default();
            if run_id.is_empty() || !role.eq_ignore_ascii_case("assistant") {
                return Ok(());
            }

            let text = data.get("text").and_then(Value::as_str).unwrap_or_default();
            if text.is_empty() {
                return Ok(());
            }

            let thread_hint = data
                .get("sessionId")
                .or_else(|| data.get("threadId"))
                .and_then(Value::as_str);
            let Some((thread_id, turn_id)) = run_context(state, run_id, thread_hint)? else {
                return Ok(());
            };

            let item_id = {
                let mut runtime = lock_or_err(&state.bridge_runtime, "bridge_runtime")?;
                let run = runtime.run_states.entry(run_id.to_string()).or_default();
                run.assistant_item_id
                    .clone()
                    .unwrap_or_else(|| format!("assistant-{run_id}"))
            };

            let item_payload = json!({
                "id": item_id,
                "type": "agentMessage",
                "text": Value::Null
            });
            if mark_item_started(state, run_id, &item_id, item_payload.clone())? {
                let _ = emit_message_to_app(
                    app,
                    mcp_notification_payload(
                        "item/started",
                        json!({
                            "threadId": thread_id,
                            "turnId": turn_id,
                            "item": item_payload
                        }),
                    ),
                );
            }

            let delta_needed = {
                let runtime = lock_or_err(&state.bridge_runtime, "bridge_runtime")?;
                runtime
                    .run_states
                    .get(run_id)
                    .map(|run| !run.assistant_delta_seen)
                    .unwrap_or(true)
            };

            if delta_needed {
                let _ = emit_message_to_app(
                    app,
                    mcp_notification_payload(
                        "item/agentMessage/delta",
                        json!({
                            "threadId": thread_id,
                            "turnId": turn_id,
                            "itemId": item_id,
                            "delta": text
                        }),
                    ),
                );
            }
        }
        "run.reasoning.delta" => {
            let run_id = data
                .get("runId")
                .and_then(Value::as_str)
                .unwrap_or_default();
            let item_id_raw = data
                .get("itemId")
                .and_then(Value::as_str)
                .unwrap_or_default();
            let delta = data
                .get("textDelta")
                .or_else(|| data.get("delta"))
                .and_then(Value::as_str)
                .unwrap_or_default();
            if run_id.is_empty() || item_id_raw.is_empty() || delta.is_empty() {
                return Ok(());
            }

            let (item_id, summary_index) =
                if let Some((base, idx)) = item_id_raw.rsplit_once("_summary_") {
                    let parsed = idx.parse::<i64>().unwrap_or(0);
                    (base.to_string(), parsed)
                } else {
                    (item_id_raw.to_string(), 0)
                };

            let thread_hint = data
                .get("sessionId")
                .or_else(|| data.get("threadId"))
                .and_then(Value::as_str);
            let Some((thread_id, turn_id)) = run_context(state, run_id, thread_hint)? else {
                return Ok(());
            };

            let item_payload = json!({
                "id": item_id,
                "type": "reasoning",
                "summary": []
            });
            if mark_item_started(state, run_id, &item_id, item_payload.clone())? {
                let _ = emit_message_to_app(
                    app,
                    mcp_notification_payload(
                        "item/started",
                        json!({
                            "threadId": thread_id,
                            "turnId": turn_id,
                            "item": item_payload
                        }),
                    ),
                );
            }

            let _ = emit_message_to_app(
                app,
                mcp_notification_payload(
                    "item/reasoning/summaryTextDelta",
                    json!({
                        "threadId": thread_id,
                        "turnId": turn_id,
                        "itemId": item_id,
                        "summaryIndex": summary_index,
                        "delta": delta
                    }),
                ),
            );
        }
        "run.reasoning" => {
            let run_id = data
                .get("runId")
                .and_then(Value::as_str)
                .unwrap_or_default();
            let item_id_raw = data
                .get("itemId")
                .and_then(Value::as_str)
                .unwrap_or_default();
            let text = data.get("text").and_then(Value::as_str).unwrap_or_default();
            if run_id.is_empty() || item_id_raw.is_empty() || text.is_empty() {
                return Ok(());
            }
            let (item_id, summary_index) =
                if let Some((base, idx)) = item_id_raw.rsplit_once("_summary_") {
                    let parsed = idx.parse::<i64>().unwrap_or(0);
                    (base.to_string(), parsed)
                } else {
                    (item_id_raw.to_string(), 0)
                };

            let thread_hint = data
                .get("sessionId")
                .or_else(|| data.get("threadId"))
                .and_then(Value::as_str);
            let Some((thread_id, turn_id)) = run_context(state, run_id, thread_hint)? else {
                return Ok(());
            };

            let item_payload = json!({
                "id": item_id,
                "type": "reasoning",
                "summary": []
            });
            if mark_item_started(state, run_id, &item_id, item_payload.clone())? {
                let _ = emit_message_to_app(
                    app,
                    mcp_notification_payload(
                        "item/started",
                        json!({
                            "threadId": thread_id,
                            "turnId": turn_id,
                            "item": item_payload
                        }),
                    ),
                );
            }

            let _ = emit_message_to_app(
                app,
                mcp_notification_payload(
                    "item/reasoning/summaryTextDelta",
                    json!({
                        "threadId": thread_id,
                        "turnId": turn_id,
                        "itemId": item_id,
                        "summaryIndex": summary_index,
                        "delta": text
                    }),
                ),
            );
        }
        "run.command" => {
            let run_id = data
                .get("runId")
                .and_then(Value::as_str)
                .unwrap_or_default();
            let item_id = data
                .get("itemId")
                .and_then(Value::as_str)
                .unwrap_or_default();
            let command = data
                .get("command")
                .and_then(Value::as_str)
                .unwrap_or_default();
            if run_id.is_empty() || item_id.is_empty() || command.is_empty() {
                return Ok(());
            }

            let status = data
                .get("status")
                .and_then(Value::as_str)
                .unwrap_or("inProgress")
                .to_string();
            let thread_hint = data
                .get("sessionId")
                .or_else(|| data.get("threadId"))
                .and_then(Value::as_str);
            let Some((thread_id, turn_id)) = run_context(state, run_id, thread_hint)? else {
                return Ok(());
            };

            let item_payload = json!({
                "id": item_id,
                "type": "commandExecution",
                "command": command,
                "status": status,
                "exitCode": data.get("exitCode"),
                "aggregatedOutput": data.get("output")
            });
            if mark_item_started(state, run_id, item_id, item_payload.clone())? {
                let _ = emit_message_to_app(
                    app,
                    mcp_notification_payload(
                        "item/started",
                        json!({
                            "threadId": thread_id,
                            "turnId": turn_id,
                            "item": item_payload.clone()
                        }),
                    ),
                );
            }

            if let Some(output) = data.get("output").and_then(Value::as_str) {
                if !output.is_empty() {
                    let _ = emit_message_to_app(
                        app,
                        mcp_notification_payload(
                            "item/commandExecution/outputDelta",
                            json!({
                                "threadId": thread_id,
                                "turnId": turn_id,
                                "itemId": item_id,
                                "delta": output
                            }),
                        ),
                    );
                }
            }

            let terminal = matches!(
                status.as_str(),
                "completed" | "failed" | "declined" | "interrupted" | "canceled" | "cancelled"
            );
            if terminal && mark_item_completed(state, run_id, item_id, item_payload.clone())? {
                let _ = emit_message_to_app(
                    app,
                    mcp_notification_payload(
                        "item/completed",
                        json!({
                            "threadId": thread_id,
                            "turnId": turn_id,
                            "item": item_payload
                        }),
                    ),
                );
            }
        }
        "run.command.outputDelta" => {
            let run_id = data
                .get("runId")
                .and_then(Value::as_str)
                .unwrap_or_default();
            let item_id = data
                .get("itemId")
                .and_then(Value::as_str)
                .unwrap_or_default();
            let delta = data
                .get("delta")
                .and_then(Value::as_str)
                .unwrap_or_default();
            if run_id.is_empty() || item_id.is_empty() || delta.is_empty() {
                return Ok(());
            }

            let thread_hint = data
                .get("sessionId")
                .or_else(|| data.get("threadId"))
                .and_then(Value::as_str);
            let Some((thread_id, turn_id)) = run_context(state, run_id, thread_hint)? else {
                return Ok(());
            };

            let item_payload = json!({
                "id": item_id,
                "type": "commandExecution",
                "command": Value::Null,
                "status": "inProgress"
            });
            if mark_item_started(state, run_id, item_id, item_payload.clone())? {
                let _ = emit_message_to_app(
                    app,
                    mcp_notification_payload(
                        "item/started",
                        json!({
                            "threadId": thread_id,
                            "turnId": turn_id,
                            "item": item_payload
                        }),
                    ),
                );
            }

            let _ = emit_message_to_app(
                app,
                mcp_notification_payload(
                    "item/commandExecution/outputDelta",
                    json!({
                        "threadId": thread_id,
                        "turnId": turn_id,
                        "itemId": item_id,
                        "delta": delta
                    }),
                ),
            );
        }
        "approval.requested" => {
            let request_id = data
                .get("requestId")
                .and_then(Value::as_str)
                .unwrap_or_default()
                .trim()
                .to_string();
            let run_id = data
                .get("runId")
                .and_then(Value::as_str)
                .unwrap_or_default()
                .trim()
                .to_string();
            let kind = data.get("kind").and_then(Value::as_str).unwrap_or_default();
            if request_id.is_empty() {
                return Ok(());
            }
            if !run_id.is_empty() {
                let mut runtime = lock_or_err(&state.bridge_runtime, "bridge_runtime")?;
                runtime
                    .approval_run_by_request
                    .insert(request_id.clone(), run_id);
            }

            let method = if kind.eq_ignore_ascii_case("fileChange") {
                "item/fileChange/requestApproval"
            } else {
                "item/commandExecution/requestApproval"
            };
            let _ = emit_message_to_app(app, mcp_request_payload(&request_id, method, data));
        }
        "run.completed" | "run.failed" | "run.canceled" => {
            let run_id = data
                .get("runId")
                .and_then(Value::as_str)
                .unwrap_or_default();
            if run_id.is_empty() {
                return Ok(());
            }

            let (thread_id, turn_id, pending_items, turn_status, turn_error) = {
                let mut runtime = lock_or_err(&state.bridge_runtime, "bridge_runtime")?;
                let Some(run) = runtime.run_states.remove(run_id) else {
                    return Ok(());
                };

                let pending = run
                    .started_items
                    .iter()
                    .filter(|item_id| !run.completed_items.contains(*item_id))
                    .filter_map(|item_id| run.item_payloads.get(item_id).cloned())
                    .collect::<Vec<_>>();

                let turn_status = match event_name {
                    "run.completed" => "completed",
                    "run.canceled" => "interrupted",
                    _ => "failed",
                };
                let turn_error = if event_name == "run.failed" {
                    data.get("message")
                        .and_then(Value::as_str)
                        .map(|message| json!({ "message": message }))
                } else {
                    None
                };

                (
                    run.thread_id,
                    run.turn_id,
                    pending,
                    turn_status.to_string(),
                    turn_error,
                )
            };

            if thread_id.is_empty() || turn_id.is_empty() {
                return Ok(());
            }

            for item in pending_items {
                let _ = emit_message_to_app(
                    app,
                    mcp_notification_payload(
                        "item/completed",
                        json!({
                            "threadId": thread_id,
                            "turnId": turn_id,
                            "item": item
                        }),
                    ),
                );
            }

            let _ = emit_message_to_app(
                app,
                mcp_notification_payload(
                    "turn/completed",
                    json!({
                        "threadId": thread_id,
                        "turn": {
                            "id": turn_id,
                            "status": turn_status,
                            "error": turn_error
                        }
                    }),
                ),
            );
        }
        "run.rejected" => {
            let thread_id = data
                .get("sessionId")
                .or_else(|| data.get("threadId"))
                .and_then(Value::as_str)
                .unwrap_or_default()
                .trim()
                .to_string();
            if thread_id.is_empty() {
                return Ok(());
            }

            let turn_id = {
                let mut runtime = lock_or_err(&state.bridge_runtime, "bridge_runtime")?;
                runtime
                    .pending_turns
                    .entry(thread_id.clone())
                    .or_default()
                    .pop_front()
                    .unwrap_or_else(|| Uuid::new_v4().to_string())
            };

            let error_message = data
                .get("reason")
                .and_then(Value::as_str)
                .unwrap_or("Run rejected")
                .to_string();

            let _ = emit_message_to_app(
                app,
                mcp_notification_payload(
                    "turn/completed",
                    json!({
                        "threadId": thread_id,
                        "turn": {
                            "id": turn_id,
                            "status": "failed",
                            "error": { "message": error_message }
                        }
                    }),
                ),
            );
        }
        _ => {}
    }

    Ok(())
}

async fn handle_mcp_response(
    app: &tauri::AppHandle,
    state: &AppState,
    message: &Value,
) -> Result<(), String> {
    let response = message
        .get("response")
        .or_else(|| message.get("message"))
        .and_then(Value::as_object);
    let Some(response) = response else {
        return Ok(());
    };

    let request_id = response
        .get("id")
        .and_then(|value| {
            value
                .as_str()
                .map(|s| s.to_string())
                .or_else(|| value.as_i64().map(|v| v.to_string()))
        })
        .unwrap_or_default();
    if request_id.trim().is_empty() {
        return Ok(());
    }

    let result = response.get("result").and_then(Value::as_object);
    let decision = result
        .and_then(|obj| obj.get("decision"))
        .and_then(Value::as_str)
        .map(|v| v.trim().to_string())
        .filter(|v| !v.is_empty())
        .unwrap_or_else(|| "decline".to_string());

    let run_id = {
        let mut runtime = lock_or_err(&state.bridge_runtime, "bridge_runtime")?;
        runtime.approval_run_by_request.remove(&request_id)
    }
    .or_else(|| {
        result
            .and_then(|obj| obj.get("runId"))
            .and_then(Value::as_str)
            .map(|v| v.trim().to_string())
            .filter(|v| !v.is_empty())
    });

    let Some(run_id) = run_id else {
        return Ok(());
    };

    send_bridge_command(
        app,
        state,
        "approval.respond",
        json!({
            "runId": run_id,
            "requestId": request_id,
            "decision": decision
        }),
    )
    .await
}

async fn handle_mcp_request(
    app: &tauri::AppHandle,
    window: &Window,
    state: &AppState,
    message: &Value,
) -> Result<(), String> {
    let Some(request) = message.get("request").and_then(Value::as_object) else {
        return Ok(());
    };

    let id = request
        .get("id")
        .and_then(Value::as_str)
        .unwrap_or("unknown-mcp-id");
    let method = request
        .get("method")
        .and_then(Value::as_str)
        .unwrap_or("unknown-method");
    let params = request.get("params").and_then(Value::as_object);

    let payload = match method {
        "thread/list" => {
            let archived = params
                .and_then(|p| p.get("archived"))
                .and_then(Value::as_bool)
                .unwrap_or(false);
            let limit = params
                .and_then(|p| p.get("limit"))
                .and_then(Value::as_u64)
                .map(|v| v as usize)
                .unwrap_or(100)
                .clamp(1, 200);
            let offset = params
                .and_then(|p| p.get("cursor"))
                .and_then(Value::as_str)
                .and_then(|v| v.parse::<usize>().ok())
                .unwrap_or(0);

            if archived {
                mcp_result_payload(id, json!({ "data": [], "nextCursor": Value::Null }))
            } else {
                match bridge_list_sessions(state, (limit + offset).max(limit)).await {
                    Ok(summaries) => {
                        let total = summaries.len();
                        let data = summaries
                            .into_iter()
                            .skip(offset)
                            .take(limit)
                            .map(|summary| bridge_summary_to_thread_list_json(&summary))
                            .collect::<Vec<_>>();
                        let next_cursor = if offset + data.len() < total {
                            Value::String((offset + data.len()).to_string())
                        } else {
                            Value::Null
                        };
                        mcp_result_payload(id, json!({ "data": data, "nextCursor": next_cursor }))
                    }
                    Err(error) => mcp_error_payload(id, format!("thread/list failed: {error}")),
                }
            }
        }
        "thread/read" => {
            let thread_id = params
                .and_then(|p| p.get("threadId"))
                .and_then(Value::as_str)
                .unwrap_or_default()
                .trim();
            if thread_id.is_empty() {
                mcp_error_payload(id, "thread/read requires threadId")
            } else {
                let include_turns = params
                    .and_then(|p| p.get("includeTurns"))
                    .and_then(Value::as_bool)
                    .unwrap_or(true);
                let requested_cwd = params
                    .and_then(|p| p.get("cwd"))
                    .and_then(Value::as_str)
                    .map(|v| v.trim().to_string())
                    .filter(|v| !v.is_empty())
                    .map(|v| normalize_root_string(&v));

                match bridge_read_thread(state, thread_id, include_turns, requested_cwd).await {
                    Ok(thread) => mcp_result_payload(id, json!({ "thread": thread })),
                    Err(error) => mcp_error_payload(id, format!("thread/read failed: {error}")),
                }
            }
        }
        "thread/start" => {
            let cwd = params
                .and_then(|p| p.get("cwd"))
                .and_then(Value::as_str)
                .map(|v| v.trim().to_string())
                .filter(|v| !v.is_empty())
                .map(|v| normalize_root_string(&v))
                .unwrap_or_else(|| preferred_workspace_cwd(state));
            let config = read_codex_config_snapshot();
            let model = extract_model_from_params(params)
                .or_else(|| config.model.clone())
                .unwrap_or_else(|| DEFAULT_MODEL.to_string());
            let reasoning_effort = extract_effort_from_params(params)
                .or_else(|| config.model_reasoning_effort.clone())
                .unwrap_or_else(|| DEFAULT_REASONING_EFFORT.to_string());
            let approval_policy = extract_approval_policy_from_params(params)
                .or_else(|| config.approval_policy.clone())
                .unwrap_or_else(|| DEFAULT_APPROVAL_POLICY.to_string());
            let sandbox_mode = extract_sandbox_from_params(params)
                .or_else(|| config.sandbox_mode.clone())
                .unwrap_or_else(|| DEFAULT_SANDBOX_MODE.to_string());

            match bridge_post_json(state, "api/v1/sessions", json!({ "cwd": cwd })).await {
                Ok(created) => {
                    let thread_id = created
                        .get("id")
                        .and_then(Value::as_str)
                        .unwrap_or_default()
                        .trim()
                        .to_string();
                    if thread_id.is_empty() {
                        mcp_error_payload(id, "thread/start failed: missing session id")
                    } else {
                        match bridge_read_thread(state, &thread_id, true, Some(cwd.clone())).await {
                            Ok(thread_json) => mcp_result_payload(
                                id,
                                json!({
                                    "thread": thread_json,
                                    "model": model,
                                    "reasoningEffort": reasoning_effort,
                                    "cwd": cwd,
                                    "sessionMeta": {
                                        "cwd": cwd,
                                        "approvalPolicy": approval_policy,
                                        "sandboxMode": sandbox_mode
                                    }
                                }),
                            ),
                            Err(error) => {
                                mcp_error_payload(id, format!("thread/start read failed: {error}"))
                            }
                        }
                    }
                }
                Err(error) => mcp_error_payload(id, format!("thread/start failed: {error}")),
            }
        }
        "thread/resume" => {
            let thread_id = params
                .and_then(|p| p.get("threadId"))
                .and_then(Value::as_str)
                .unwrap_or_default()
                .trim();
            if thread_id.is_empty() {
                mcp_error_payload(id, "thread/resume requires threadId")
            } else {
                let config = read_codex_config_snapshot();
                let model = extract_model_from_params(params)
                    .or_else(|| config.model.clone())
                    .unwrap_or_else(|| DEFAULT_MODEL.to_string());
                let reasoning_effort = extract_effort_from_params(params)
                    .or_else(|| config.model_reasoning_effort.clone())
                    .unwrap_or_else(|| DEFAULT_REASONING_EFFORT.to_string());
                let (session_approval, session_sandbox) =
                    bridge_session_settings(state, thread_id).await;
                let requested_cwd = params
                    .and_then(|p| p.get("cwd"))
                    .and_then(Value::as_str)
                    .map(|v| v.trim().to_string())
                    .filter(|v| !v.is_empty())
                    .map(|v| normalize_root_string(&v));

                match bridge_read_thread(state, thread_id, true, requested_cwd).await {
                    Ok(thread_json) => {
                        let cwd = thread_json
                            .get("cwd")
                            .and_then(Value::as_str)
                            .unwrap_or("/")
                            .to_string();
                        mcp_result_payload(
                            id,
                            json!({
                                "thread": thread_json,
                                "model": model,
                                "reasoningEffort": reasoning_effort,
                                "cwd": cwd,
                                "sessionMeta": {
                                    "cwd": cwd,
                                    "approvalPolicy": session_approval.unwrap_or_else(|| DEFAULT_APPROVAL_POLICY.to_string()),
                                    "sandboxMode": session_sandbox.unwrap_or_else(|| DEFAULT_SANDBOX_MODE.to_string())
                                }
                            }),
                        )
                    }
                    Err(error) => mcp_error_payload(id, format!("thread/resume failed: {error}")),
                }
            }
        }
        "turn/start" => {
            let thread_id = params
                .and_then(|p| p.get("threadId"))
                .and_then(Value::as_str)
                .unwrap_or_default()
                .trim()
                .to_string();
            if thread_id.is_empty() {
                mcp_error_payload(id, "turn/start requires threadId")
            } else {
                let input = params
                    .and_then(|p| p.get("input"))
                    .cloned()
                    .unwrap_or_else(|| json!([]));
                let prompt = extract_prompt_from_input(&input);
                let images = parse_image_data_urls_from_input(&input);
                if prompt.is_empty() && images.is_empty() {
                    mcp_error_payload(id, "turn/start requires non-empty input")
                } else {
                    let cwd = params
                        .and_then(|p| p.get("cwd"))
                        .and_then(Value::as_str)
                        .map(|v| v.trim().to_string())
                        .filter(|v| !v.is_empty())
                        .map(|v| normalize_root_string(&v))
                        .unwrap_or_else(|| preferred_workspace_cwd(state));
                    let config = read_codex_config_snapshot();
                    let model = extract_model_from_params(params)
                        .or_else(|| config.model.clone())
                        .unwrap_or_else(|| DEFAULT_MODEL.to_string());
                    let effort = extract_effort_from_params(params)
                        .or_else(|| config.model_reasoning_effort.clone())
                        .unwrap_or_else(|| DEFAULT_REASONING_EFFORT.to_string());
                    let (session_approval, session_sandbox) =
                        bridge_session_settings(state, &thread_id).await;
                    let approval_policy = extract_approval_policy_from_params(params)
                        .or(session_approval)
                        .or_else(|| config.approval_policy.clone())
                        .unwrap_or_else(|| DEFAULT_APPROVAL_POLICY.to_string());
                    let sandbox_mode = extract_sandbox_from_params(params)
                        .or(session_sandbox)
                        .or_else(|| config.sandbox_mode.clone())
                        .unwrap_or_else(|| DEFAULT_SANDBOX_MODE.to_string());
                    let turn_id = Uuid::new_v4().to_string();

                    if let Err(error) = queue_pending_turn(state, &thread_id, &turn_id) {
                        mcp_error_payload(id, format!("turn/start queue failed: {error}"))
                    } else {
                        let mut command = json!({
                            "prompt": prompt,
                            "sessionId": thread_id,
                            "workingDirectory": cwd,
                            "model": model,
                            "sandbox": sandbox_mode,
                            "approvalPolicy": approval_policy,
                            "effort": effort,
                            "skipGitRepoCheck": true
                        });
                        if !images.is_empty() {
                            if let Value::Object(obj) = &mut command {
                                obj.insert(
                                    "images".to_string(),
                                    Value::Array(
                                        images.into_iter().map(Value::String).collect::<Vec<_>>(),
                                    ),
                                );
                            }
                        }

                        match send_bridge_command(app, state, "chat.send", command).await {
                            Ok(_) => mcp_result_payload(
                                id,
                                json!({
                                    "threadId": thread_id,
                                    "turn": {
                                        "id": turn_id,
                                        "status": "inProgress",
                                        "error": Value::Null
                                    }
                                }),
                            ),
                            Err(error) => {
                                mcp_error_payload(id, format!("turn/start failed: {error}"))
                            }
                        }
                    }
                }
            }
        }
        "turn/interrupt" => {
            let run_id = params.and_then(|p| p.get("runId")).and_then(Value::as_str);
            let thread_id = params
                .and_then(|p| p.get("threadId"))
                .or_else(|| params.and_then(|p| p.get("sessionId")))
                .and_then(Value::as_str);
            let mut data = Map::new();
            if let Some(run_id) = run_id {
                if !run_id.trim().is_empty() {
                    data.insert(
                        "runId".to_string(),
                        Value::String(run_id.trim().to_string()),
                    );
                }
            }
            if let Some(thread_id) = thread_id {
                if !thread_id.trim().is_empty() {
                    data.insert(
                        "sessionId".to_string(),
                        Value::String(thread_id.trim().to_string()),
                    );
                }
            }
            match send_bridge_command(app, state, "run.cancel", Value::Object(data)).await {
                Ok(_) => mcp_result_payload(id, json!({ "success": true })),
                Err(error) => mcp_error_payload(id, format!("turn/interrupt failed: {error}")),
            }
        }
        "model/list" => {
            let config = read_codex_config_snapshot();
            let default_model = config.model.unwrap_or_else(|| DEFAULT_MODEL.to_string());
            let default_effort = config
                .model_reasoning_effort
                .unwrap_or_else(|| DEFAULT_REASONING_EFFORT.to_string());
            mcp_result_payload(
                id,
                json!({
                    "data": [
                        {
                            "model": default_model,
                            "isDefault": true,
                            "defaultReasoningEffort": default_effort,
                            "supportedReasoningEfforts": [
                                { "reasoningEffort": "minimal", "description": "Minimal effort" },
                                { "reasoningEffort": "low", "description": "Low effort" },
                                { "reasoningEffort": "medium", "description": "Medium effort" },
                                { "reasoningEffort": "high", "description": "High effort" }
                            ]
                        }
                    ],
                    "nextCursor": Value::Null
                }),
            )
        }
        "config/read" => {
            let snapshot = read_codex_config_snapshot();
            let model = snapshot.model.unwrap_or_else(|| DEFAULT_MODEL.to_string());
            let effort = snapshot
                .model_reasoning_effort
                .unwrap_or_else(|| DEFAULT_REASONING_EFFORT.to_string());
            let approval_policy = snapshot
                .approval_policy
                .unwrap_or_else(|| DEFAULT_APPROVAL_POLICY.to_string());
            let sandbox_mode = snapshot
                .sandbox_mode
                .unwrap_or_else(|| DEFAULT_SANDBOX_MODE.to_string());
            mcp_result_payload(
                id,
                json!({
                    "config": {
                        "model": model,
                        "model_reasoning_effort": effort,
                        "approval_policy": approval_policy,
                        "sandbox_mode": sandbox_mode,
                        "modelReasoningEffort": effort,
                        "approvalPolicy": approval_policy,
                        "sandboxMode": sandbox_mode
                    },
                    "layers": []
                }),
            )
        }
        "config/batchWrite" => {
            let updates = collect_config_updates(params);
            match write_codex_config_updates(&updates) {
                Ok(_) => {
                    mcp_result_payload(id, json!({ "version": now_unix_seconds().to_string() }))
                }
                Err(error) => mcp_error_payload(id, format!("config/batchWrite failed: {error}")),
            }
        }
        _ => {
            return handle_mcp_request_legacy(app, window, state, message);
        }
    };

    emit_message_to_window(window, payload)
}

fn handle_mcp_request_legacy(
    _app: &tauri::AppHandle,
    window: &Window,
    state: &AppState,
    message: &Value,
) -> Result<(), String> {
    let Some(request) = message.get("request").and_then(Value::as_object) else {
        return Ok(());
    };

    let id = request
        .get("id")
        .and_then(Value::as_str)
        .unwrap_or("unknown-mcp-id");
    let method = request
        .get("method")
        .and_then(Value::as_str)
        .unwrap_or("unknown-method");
    let params = request.get("params").and_then(Value::as_object);

    let payload = match method {
        // Frontend startup depends on this request to resolve auth loading state.
        "account/read" => mcp_result_payload(
            id,
            json!({
                "account": {
                    "type": "apiKey"
                },
                "requiresOpenaiAuth": false
            }),
        ),
        "account/logout" => mcp_result_payload(id, json!({})),
        // The home screen and sidebar expect recent threads with pagination fields.
        "thread/list" => {
            let archived = params
                .and_then(|p| p.get("archived"))
                .and_then(Value::as_bool)
                .unwrap_or(false);
            let limit = params
                .and_then(|p| p.get("limit"))
                .and_then(Value::as_u64)
                .map(|v| v as usize)
                .unwrap_or(200);
            let offset = params
                .and_then(|p| p.get("cursor"))
                .and_then(Value::as_str)
                .and_then(|v| v.parse::<usize>().ok())
                .unwrap_or(0);

            let (data, next_cursor) = {
                let store = lock_or_err(&state.thread_store, "thread_store")?;
                let ordered_ids = store
                    .order
                    .iter()
                    .filter_map(|thread_id| {
                        let thread = store.threads.get(thread_id)?;
                        if thread.archived == archived {
                            Some(thread_id.clone())
                        } else {
                            None
                        }
                    })
                    .collect::<Vec<_>>();

                let data = ordered_ids
                    .iter()
                    .skip(offset)
                    .take(limit)
                    .filter_map(|thread_id| store.threads.get(thread_id))
                    .map(HostThread::to_list_json)
                    .collect::<Vec<_>>();

                let next = if offset + data.len() < ordered_ids.len() {
                    Value::String((offset + data.len()).to_string())
                } else {
                    Value::Null
                };
                (data, next)
            };

            mcp_result_payload(
                id,
                json!({
                    "data": data,
                    "nextCursor": next_cursor
                }),
            )
        }
        "thread/read" => {
            let thread_id = params
                .and_then(|p| p.get("threadId"))
                .and_then(Value::as_str)
                .unwrap_or_default();
            if thread_id.is_empty() {
                mcp_error_payload(id, "thread/read requires threadId")
            } else {
                let include_turns = params
                    .and_then(|p| p.get("includeTurns"))
                    .and_then(Value::as_bool)
                    .unwrap_or(true);
                let fallback_cwd = preferred_workspace_cwd(state);
                let thread_json = {
                    let mut store = lock_or_err(&state.thread_store, "thread_store")?;
                    let thread = ensure_thread(&mut store, thread_id, &fallback_cwd);
                    thread.archived = false;
                    thread.updated_at = now_unix_seconds();
                    if include_turns {
                        thread.to_resume_json()
                    } else {
                        thread.to_list_json()
                    }
                };

                mcp_result_payload(id, json!({ "thread": thread_json }))
            }
        }
        "thread/start" => {
            let cwd = params
                .and_then(|p| p.get("cwd"))
                .and_then(Value::as_str)
                .filter(|v| !v.trim().is_empty())
                .map(normalize_root_string)
                .unwrap_or_else(|| preferred_workspace_cwd(state));
            let model = params
                .and_then(|p| p.get("model"))
                .and_then(Value::as_str)
                .unwrap_or("gpt-5.2-codex");
            let reasoning_effort = params
                .and_then(|p| p.get("effort"))
                .and_then(Value::as_str)
                .unwrap_or("medium");
            let preview = params
                .and_then(|p| p.get("input"))
                .map(first_text_from_input)
                .unwrap_or_default();

            let thread_json = {
                let mut store = lock_or_err(&state.thread_store, "thread_store")?;
                let thread = create_host_thread(None, cwd.clone(), preview);
                let thread_id = thread.id.clone();
                let response = thread.to_resume_json();
                store.threads.insert(thread_id.clone(), thread);
                promote_thread_in_order(&mut store, &thread_id);
                response
            };

            mcp_result_payload(
                id,
                json!({
                    "thread": thread_json,
                    "model": model,
                    "reasoningEffort": reasoning_effort,
                    "cwd": cwd,
                    "sessionMeta": {
                        "cwd": cwd
                    }
                }),
            )
        }
        "thread/resume" => {
            let thread_id = params
                .and_then(|p| p.get("threadId"))
                .and_then(Value::as_str)
                .unwrap_or_default();
            if thread_id.is_empty() {
                mcp_error_payload(id, "thread/resume requires threadId")
            } else {
                let requested_cwd = params
                    .and_then(|p| p.get("cwd"))
                    .and_then(Value::as_str)
                    .filter(|v| !v.trim().is_empty())
                    .map(normalize_root_string);
                let fallback_cwd = requested_cwd
                    .clone()
                    .unwrap_or_else(|| preferred_workspace_cwd(state));
                let model = params
                    .and_then(|p| p.get("model"))
                    .and_then(Value::as_str)
                    .unwrap_or("gpt-5.2-codex");
                let reasoning_effort = params
                    .and_then(|p| p.get("effort"))
                    .and_then(Value::as_str)
                    .unwrap_or("medium");

                let (thread_json, cwd) = {
                    let mut store = lock_or_err(&state.thread_store, "thread_store")?;
                    let thread = ensure_thread(&mut store, thread_id, &fallback_cwd);
                    if let Some(cwd) = requested_cwd {
                        thread.cwd = cwd;
                    }
                    thread.updated_at = now_unix_seconds();
                    thread.archived = false;
                    (thread.to_resume_json(), thread.cwd.clone())
                };

                mcp_result_payload(
                    id,
                    json!({
                        "thread": thread_json,
                        "model": model,
                        "reasoningEffort": reasoning_effort,
                        "cwd": cwd,
                        "sessionMeta": {
                            "cwd": cwd
                        }
                    }),
                )
            }
        }
        "turn/start" => {
            let thread_id = params
                .and_then(|p| p.get("threadId"))
                .and_then(Value::as_str)
                .unwrap_or_default();
            if thread_id.is_empty() {
                mcp_error_payload(id, "turn/start requires threadId")
            } else {
                let input = params
                    .and_then(|p| p.get("input"))
                    .cloned()
                    .unwrap_or_else(|| json!([]));
                let cwd = params
                    .and_then(|p| p.get("cwd"))
                    .and_then(Value::as_str)
                    .filter(|v| !v.trim().is_empty())
                    .map(normalize_root_string)
                    .unwrap_or_else(|| preferred_workspace_cwd(state));
                let preview = first_text_from_input(&input);
                let turn_id = Uuid::new_v4().to_string();

                {
                    let mut store = lock_or_err(&state.thread_store, "thread_store")?;
                    let thread = ensure_thread(&mut store, thread_id, &cwd);
                    thread.cwd = cwd.clone();
                    thread.updated_at = now_unix_seconds();
                    thread.archived = false;
                    if !preview.is_empty() {
                        thread.preview = preview;
                    }

                    thread.turns.push(HostTurn {
                        id: turn_id.clone(),
                        status: "completed".to_string(),
                        error: None,
                        items: vec![
                            json!({
                                "id": format!("user-{}", Uuid::new_v4()),
                                "type": "userMessage",
                                "content": input
                            }),
                            json!({
                                "id": format!("assistant-{}", Uuid::new_v4()),
                                "type": "agentMessage",
                                "text": "Tauri 兼容层已接收这条消息。"
                            }),
                        ],
                    });
                }

                mcp_result_payload(
                    id,
                    json!({
                        "threadId": thread_id,
                        "turn": {
                            "id": turn_id,
                            "status": "completed",
                            "error": null
                        }
                    }),
                )
            }
        }
        "turn/interrupt" => mcp_result_payload(id, json!({ "success": true })),
        "thread/archive" => {
            if let Some(thread_id) = params
                .and_then(|p| p.get("threadId"))
                .and_then(Value::as_str)
            {
                let mut store = lock_or_err(&state.thread_store, "thread_store")?;
                if let Some(thread) = store.threads.get_mut(thread_id) {
                    thread.archived = true;
                    thread.updated_at = now_unix_seconds();
                }
            }
            mcp_result_payload(id, json!({}))
        }
        "thread/unarchive" => {
            if let Some(thread_id) = params
                .and_then(|p| p.get("threadId"))
                .and_then(Value::as_str)
            {
                let mut store = lock_or_err(&state.thread_store, "thread_store")?;
                if let Some(thread) = store.threads.get_mut(thread_id) {
                    thread.archived = false;
                    thread.updated_at = now_unix_seconds();
                    promote_thread_in_order(&mut store, thread_id);
                }
            }
            mcp_result_payload(id, json!({}))
        }
        "model/list" => mcp_result_payload(
            id,
            json!({
                "data": [
                    {
                        "model": "gpt-5.2-codex",
                        "isDefault": true,
                        "defaultReasoningEffort": "medium",
                        "supportedReasoningEfforts": [
                            { "reasoningEffort": "minimal", "description": "Minimal effort" },
                            { "reasoningEffort": "low", "description": "Low effort" },
                            { "reasoningEffort": "medium", "description": "Medium effort" },
                            { "reasoningEffort": "high", "description": "High effort" }
                        ]
                    }
                ],
                "nextCursor": null
            }),
        ),
        "skills/list" => mcp_result_payload(id, json!({ "data": [] })),
        "mcpServerStatus/list" => mcp_result_payload(id, json!({ "data": [] })),
        "collaborationMode/list" => mcp_result_payload(id, json!({ "data": [] })),
        "config/batchWrite" => mcp_result_payload(
            id,
            json!({
                "version": now_unix_seconds().to_string()
            }),
        ),
        "feedback/upload" => mcp_result_payload(
            id,
            json!({
                "threadId": Uuid::new_v4().to_string()
            }),
        ),
        // The app picker expects the same list+cursor envelope.
        "app/list" => mcp_result_payload(
            id,
            json!({
                "data": [],
                "nextCursor": null
            }),
        ),
        // Config bootstrap paths require a config object; layers are optional.
        "config/read" => mcp_result_payload(
            id,
            json!({
                "config": {},
                "layers": []
            }),
        ),
        "configRequirements/read" => mcp_result_payload(
            id,
            json!({
                "requirements": []
            }),
        ),
        _ => {
            println!("[tauri-host] unimplemented mcp method: {method}; returning empty result");
            mcp_result_payload(id, json!({}))
        }
    };

    emit_message_to_window(window, payload)
}

fn emit_fetch_json_success(window: &Window, request_id: &str, body: Value) -> Result<(), String> {
    let body_json_string = serde_json::to_string(&body).unwrap_or_else(|_| "null".to_string());
    emit_message_to_window(
        window,
        json_fetch_success(request_id, 200, json!({}), body_json_string),
    )
}

fn handle_vscode_fetch(
    app: &tauri::AppHandle,
    window: &Window,
    state: &AppState,
    request_id: &str,
    endpoint: &str,
    message: &Value,
) -> Result<(), String> {
    let body = parse_json_body_from_message(message);
    let params = body.as_object();

    let response = match endpoint {
        "workspace-root-options" => {
            let snapshot = {
                let workspace = lock_or_err(&state.workspace_state, "workspace_state")?;
                workspace.clone()
            };
            workspace_roots_response(&snapshot)
        }
        "active-workspace-roots" => {
            let snapshot = {
                let workspace = lock_or_err(&state.workspace_state, "workspace_state")?;
                workspace.clone()
            };
            active_workspace_roots_response(&snapshot)
        }
        "add-workspace-root-option" => {
            let root = params
                .and_then(|p| p.get("root"))
                .and_then(Value::as_str)
                .map(normalize_root_string)
                .or_else(|| {
                    pick_workspace_root_folder()
                        .as_deref()
                        .map(normalize_root_path)
                });

            let mut response = json!({
                "success": false
            });

            if let Some(root) = root {
                let set_active = params
                    .and_then(|p| p.get("setActive"))
                    .and_then(Value::as_bool)
                    .unwrap_or(false);
                let custom_label = params
                    .and_then(|p| p.get("label"))
                    .and_then(Value::as_str)
                    .map(|label| label.trim().to_string())
                    .filter(|label| !label.is_empty());

                let snapshot = {
                    let mut workspace = lock_or_err(&state.workspace_state, "workspace_state")?;
                    upsert_workspace_root(&mut workspace, root.clone());
                    if let Some(label) = custom_label {
                        workspace.labels.insert(root.clone(), label);
                    }
                    if set_active {
                        workspace.active_roots = vec![root.clone()];
                    } else if workspace.active_roots.is_empty() {
                        workspace.active_roots = vec![root.clone()];
                    }
                    workspace.clone()
                };
                sync_workspace_persisted_state(state, &snapshot)?;
                emit_workspace_state_updates(app, &snapshot)?;

                response = json!({
                    "success": true,
                    "root": root,
                    "workspaceRootOptions": workspace_roots_response(&snapshot),
                    "activeWorkspaceRoots": active_workspace_roots_response(&snapshot),
                });
            }
            response
        }
        "get-global-state" => {
            let key = params
                .and_then(|p| p.get("key"))
                .and_then(Value::as_str)
                .unwrap_or_default();
            let value = {
                let guard = lock_or_err(&state.persisted_atom_state, "persisted_atom_state")?;
                guard.get(key).cloned().unwrap_or(Value::Null)
            };
            json!({ "value": value })
        }
        "set-global-state" => {
            let key = params
                .and_then(|p| p.get("key"))
                .and_then(Value::as_str)
                .unwrap_or_default()
                .to_string();
            let value = params
                .and_then(|p| p.get("value"))
                .cloned()
                .unwrap_or(Value::Null);

            if !key.is_empty() {
                {
                    let mut guard =
                        lock_or_err(&state.persisted_atom_state, "persisted_atom_state")?;
                    guard.insert(key.clone(), value.clone());
                }
                if let Some(snapshot) = sync_workspace_from_global_state(state, &key, &value)? {
                    sync_workspace_persisted_state(state, &snapshot)?;
                    emit_workspace_state_updates(app, &snapshot)?;
                }
            }

            json!({ "success": true })
        }
        "codex-home" => json!({
            "codexHome": default_codex_home_path()
        }),
        "git-origins" => json!({
            "origins": []
        }),
        "paths-exist" => {
            let candidate_paths = params
                .and_then(|p| p.get("paths").or_else(|| p.get("dirs")))
                .and_then(Value::as_array)
                .map(|items| {
                    items
                        .iter()
                        .filter_map(Value::as_str)
                        .filter(|path| Path::new(path).exists())
                        .map(|path| path.to_string())
                        .collect::<Vec<_>>()
                })
                .unwrap_or_default();
            json!({
                "existingPaths": candidate_paths
            })
        }
        "list-pinned-threads" => {
            let thread_ids = {
                let guard = lock_or_err(&state.persisted_atom_state, "persisted_atom_state")?;
                parse_pinned_thread_ids(guard.get(GLOBAL_KEY_PINNED_THREAD_IDS))
            };
            json!({ "threadIds": thread_ids })
        }
        "list-pending-automation-run-threads" => json!({ "threadIds": [] }),
        "inbox-items" => json!({
            "items": [],
            "nextCursor": null
        }),
        "pending-automation-runs" => json!({ "runs": [] }),
        "list-automations" => json!({
            "automations": [],
            "nextCursor": null
        }),
        "recommended-skills" => json!({ "skills": [] }),
        "install-recommended-skill" | "remove-skill" => json!({ "success": true }),
        "set-pinned-threads-order" => {
            let next_ids = params
                .and_then(|p| p.get("threadIds"))
                .map(|value| parse_pinned_thread_ids(Some(value)))
                .unwrap_or_default();
            {
                let mut guard = lock_or_err(&state.persisted_atom_state, "persisted_atom_state")?;
                guard.insert(
                    GLOBAL_KEY_PINNED_THREAD_IDS.to_string(),
                    Value::Array(
                        next_ids
                            .iter()
                            .map(|id| Value::String(id.clone()))
                            .collect(),
                    ),
                );
            }
            json!({ "threadIds": next_ids })
        }
        "set-thread-pinned" => {
            let thread_id = params
                .and_then(|p| p.get("threadId"))
                .and_then(Value::as_str)
                .unwrap_or_default()
                .trim()
                .to_string();
            let pinned = params
                .and_then(|p| p.get("pinned"))
                .and_then(Value::as_bool)
                .unwrap_or(false);

            let next_ids = {
                let mut guard = lock_or_err(&state.persisted_atom_state, "persisted_atom_state")?;
                let mut thread_ids =
                    parse_pinned_thread_ids(guard.get(GLOBAL_KEY_PINNED_THREAD_IDS));
                thread_ids.retain(|id| id != &thread_id);
                if pinned && !thread_id.is_empty() {
                    thread_ids.insert(0, thread_id.clone());
                }
                guard.insert(
                    GLOBAL_KEY_PINNED_THREAD_IDS.to_string(),
                    Value::Array(
                        thread_ids
                            .iter()
                            .map(|id| Value::String(id.clone()))
                            .collect(),
                    ),
                );
                thread_ids
            };
            json!({ "threadIds": next_ids })
        }
        "is-copilot-api-available" => json!({ "available": false }),
        "os-info" => json!({
            "platform": std::env::consts::OS,
            "arch": std::env::consts::ARCH,
            "isWindows": cfg!(target_os = "windows")
        }),
        "locale-info" => {
            let locale = env::var("LC_ALL")
                .ok()
                .filter(|value| !value.trim().is_empty())
                .or_else(|| env::var("LANG").ok())
                .unwrap_or_else(|| "en-US".to_string());
            json!({
                "locale": locale,
                "language": locale
            })
        }
        "account-info" => json!({
            "plan": null,
            "email": null,
            "accountId": null
        }),
        "open-in-targets" => json!({ "targets": [] }),
        "extension-info" => json!({
            "windowType": "electron",
            "host": "tauri"
        }),
        "open-file" => json!({ "success": true }),
        "pick-files" => json!({ "paths": [] }),
        "find-files" => json!({ "files": [] }),
        "set-preferred-app" => json!({ "success": true }),
        "get-configuration" => json!({}),
        "set-configuration" => json!({ "success": true }),
        other => {
            println!("[tauri-host] unimplemented vscode endpoint: {other}; returning empty body");
            json!({})
        }
    };

    emit_fetch_json_success(window, request_id, response)
}

async fn handle_fetch(
    app: &tauri::AppHandle,
    window: &Window,
    state: &AppState,
    message: &Value,
) -> Result<(), String> {
    let request_id = message
        .get("requestId")
        .and_then(Value::as_str)
        .unwrap_or("unknown-request")
        .to_string();

    let method = message
        .get("method")
        .and_then(Value::as_str)
        .unwrap_or("GET")
        .to_uppercase();

    let Some(url) = message.get("url").and_then(Value::as_str) else {
        return emit_message_to_window(
            &window,
            json_fetch_error(&request_id, 400, "Missing fetch url"),
        );
    };

    if let Some(endpoint) = parse_vscode_endpoint(url) {
        return handle_vscode_fetch(app, window, state, &request_id, &endpoint, message);
    }

    let absolute_url = ensure_absolute_url(url);
    if absolute_url.starts_with("data:") {
        return emit_message_to_window(
            &window,
            json_fetch_error(
                &request_id,
                400,
                "Data URL fetch is not supported in Tauri host",
            ),
        );
    }

    let method = Method::from_bytes(method.as_bytes()).unwrap_or(Method::GET);
    let (headers, is_base64_body) = parse_headers(message);
    let body = parse_request_body(message, is_base64_body)?;

    let client = reqwest::Client::new();
    let mut request_builder = client.request(method, &absolute_url).headers(headers);
    if let Some(bytes) = body {
        request_builder = request_builder.body(bytes);
    }

    let response = match request_builder.send().await {
        Ok(resp) => resp,
        Err(e) => {
            return emit_message_to_window(
                &window,
                json_fetch_error(&request_id, 500, format!("Fetch failed: {e}")),
            )
        }
    };

    let status = response.status().as_u16();
    let headers_json = headers_to_json(response.headers());

    if !response.status().is_success() {
        let message = response
            .text()
            .await
            .ok()
            .filter(|t| !t.trim().is_empty())
            .unwrap_or_else(|| format!("Request failed with status {status}"));
        return emit_message_to_window(&window, json_fetch_error(&request_id, status, message));
    }

    let content_type = response
        .headers()
        .get(reqwest::header::CONTENT_TYPE)
        .and_then(|v| v.to_str().ok())
        .unwrap_or("")
        .to_lowercase();

    if status == 204 {
        return emit_message_to_window(
            &window,
            json_fetch_success(&request_id, status, headers_json, "null".to_string()),
        );
    }

    if content_type.contains("application/json") {
        let text = match response.text().await {
            Ok(v) => v,
            Err(e) => {
                return emit_message_to_window(
                    &window,
                    json_fetch_error(&request_id, 500, format!("Read JSON response failed: {e}")),
                )
            }
        };

        let body_json_string = match serde_json::from_str::<Value>(&text) {
            Ok(v) => serde_json::to_string(&v).unwrap_or_else(|_| "null".to_string()),
            Err(_) => serde_json::to_string(&text).unwrap_or_else(|_| "\"\"".to_string()),
        };

        return emit_message_to_window(
            &window,
            json_fetch_success(&request_id, status, headers_json, body_json_string),
        );
    }

    let bytes = match response.bytes().await {
        Ok(v) => v,
        Err(e) => {
            return emit_message_to_window(
                &window,
                json_fetch_error(&request_id, 500, format!("Read response bytes failed: {e}")),
            )
        }
    };

    let body_json_string = serde_json::to_string(&json!({
        "base64": BASE64_STANDARD.encode(bytes),
        "contentType": content_type,
    }))
    .unwrap_or_else(|_| "{}".to_string());

    emit_message_to_window(
        &window,
        json_fetch_success(&request_id, status, headers_json, body_json_string),
    )
}

fn handle_open_in_browser(message: &Value) -> Result<(), String> {
    let Some(url) = message.get("url").and_then(Value::as_str) else {
        return Err("open-in-browser missing url".to_string());
    };
    if !is_http_url(url) {
        return Err("open-in-browser only supports http/https".to_string());
    }
    open::that(url).map_err(|e| format!("open-in-browser failed: {e}"))?;
    Ok(())
}

fn lock_or_err<'a, T>(
    mutex: &'a Mutex<T>,
    name: &str,
) -> Result<std::sync::MutexGuard<'a, T>, String> {
    mutex.lock().map_err(|_| format!("Lock poisoned: {name}"))
}

async fn handle_message(
    app: &tauri::AppHandle,
    window: &Window,
    state: &AppState,
    message: Value,
) -> Result<(), String> {
    match message_type(&message).unwrap_or_default() {
        "ready" => {
            let persisted = {
                let guard = lock_or_err(&state.persisted_atom_state, "persisted_atom_state")?;
                Value::Object(guard.clone())
            };
            emit_message_to_window(
                window,
                json!({ "type": "persisted-atom-sync", "state": persisted }),
            )?;
            emit_message_to_window(
                window,
                json!({ "type": "app-update-ready-changed", "isUpdateReady": false }),
            )?;
        }
        "persisted-atom-sync-request" => {
            let persisted = {
                let guard = lock_or_err(&state.persisted_atom_state, "persisted_atom_state")?;
                Value::Object(guard.clone())
            };
            emit_message_to_window(
                window,
                json!({ "type": "persisted-atom-sync", "state": persisted }),
            )?;
        }
        "persisted-atom-update" => {
            let Some(key) = message.get("key").and_then(Value::as_str) else {
                return Ok(());
            };
            let deleted = message
                .get("deleted")
                .and_then(Value::as_bool)
                .unwrap_or(false);
            let next_value = if deleted {
                Value::Null
            } else {
                message.get("value").cloned().unwrap_or(Value::Null)
            };

            {
                let mut guard = lock_or_err(&state.persisted_atom_state, "persisted_atom_state")?;
                if deleted {
                    guard.remove(key);
                } else {
                    guard.insert(key.to_string(), next_value.clone());
                }
            }

            emit_message_to_app(
                app,
                json!({
                    "type": "persisted-atom-updated",
                    "key": key,
                    "value": next_value,
                    "deleted": deleted
                }),
            )?;
        }
        "persisted-atom-reset" => {
            {
                let mut guard = lock_or_err(&state.persisted_atom_state, "persisted_atom_state")?;
                guard.clear();
            }
            emit_message_to_app(app, json!({ "type": "persisted-atom-sync", "state": {} }))?;
        }
        "shared-object-subscribe" => {
            let Some(key) = message.get("key").and_then(Value::as_str) else {
                return Ok(());
            };
            let label = window.label().to_string();
            {
                let mut guard = lock_or_err(&state.shared_subscriptions, "shared_subscriptions")?;
                guard.entry(key.to_string()).or_default().insert(label);
            }
            let value = {
                let guard = lock_or_err(&state.shared_object_state, "shared_object_state")?;
                guard.get(key).cloned().unwrap_or(Value::Null)
            };
            emit_message_to_window(
                window,
                json!({
                    "type": "shared-object-updated",
                    "key": key,
                    "value": value
                }),
            )?;
        }
        "shared-object-unsubscribe" => {
            let Some(key) = message.get("key").and_then(Value::as_str) else {
                return Ok(());
            };
            let label = window.label().to_string();
            let mut guard = lock_or_err(&state.shared_subscriptions, "shared_subscriptions")?;
            if let Some(subscribers) = guard.get_mut(key) {
                subscribers.remove(&label);
                if subscribers.is_empty() {
                    guard.remove(key);
                }
            }
        }
        "shared-object-set" => {
            let Some(key) = message.get("key").and_then(Value::as_str) else {
                return Ok(());
            };
            let value = message.get("value").cloned().unwrap_or(Value::Null);
            let subscribers = {
                {
                    let mut guard = lock_or_err(&state.shared_object_state, "shared_object_state")?;
                    guard.insert(key.to_string(), value.clone());
                }
                let guard = lock_or_err(&state.shared_subscriptions, "shared_subscriptions")?;
                guard.get(key).cloned().unwrap_or_default()
            };

            for label in subscribers {
                if let Some(target) = app.get_webview_window(&label) {
                    let _ = target.emit(
                        CHANNEL_MESSAGE_FOR_VIEW,
                        json!({
                            "type": "shared-object-updated",
                            "key": key,
                            "value": value
                        }),
                    );
                }
            }
        }
        "fetch" => {
            handle_fetch(app, window, state, &message).await?;
        }
        "fetch-stream" => {
            let request_id = message
                .get("requestId")
                .and_then(Value::as_str)
                .unwrap_or("unknown-request");
            emit_message_to_window(
                window,
                json!({
                    "type": "fetch-stream-error",
                    "requestId": request_id,
                    "error": "Streaming fetch is not implemented in the Tauri shell."
                }),
            )?;
        }
        "cancel-fetch" | "cancel-fetch-stream" => {}
        "mcp-request" => {
            handle_mcp_request(app, window, state, &message).await?;
        }
        "mcp-response" => {
            handle_mcp_response(app, state, &message).await?;
        }
        "open-in-browser" => {
            if let Err(e) = handle_open_in_browser(&message) {
                eprintln!("[tauri-host] open-in-browser error: {e}");
            }
        }
        "electron-pick-workspace-root-option" => {
            if let Some(root) = resolve_workspace_root_from_message(&message) {
                let (label, snapshot) = {
                    let mut workspace = lock_or_err(&state.workspace_state, "workspace_state")?;
                    let label = upsert_workspace_root(&mut workspace, root.clone());
                    (label, workspace.clone())
                };
                emit_workspace_root_option_picked(window, &root, &label)?;
                emit_workspace_state_updates(app, &snapshot)?;
            }
        }
        "electron-add-new-workspace-root-option" => {
            if let Some(root) = resolve_workspace_root_from_message(&message) {
                let (label, snapshot) = {
                    let mut workspace = lock_or_err(&state.workspace_state, "workspace_state")?;
                    let label = upsert_workspace_root(&mut workspace, root.clone());
                    workspace.active_roots = vec![root.clone()];
                    (label, workspace.clone())
                };
                emit_workspace_root_option_picked(window, &root, &label)?;
                emit_workspace_state_updates(app, &snapshot)?;
            }
        }
        "electron-update-workspace-root-options" => {
            let roots = message
                .get("roots")
                .and_then(Value::as_array)
                .map(|array| {
                    array
                        .iter()
                        .filter_map(Value::as_str)
                        .map(normalize_root_string)
                        .collect::<Vec<_>>()
                })
                .unwrap_or_default();

            let snapshot = {
                let mut workspace = lock_or_err(&state.workspace_state, "workspace_state")?;
                workspace.roots.clear();
                for root in roots {
                    push_unique(&mut workspace.roots, root);
                }

                let roots_snapshot = workspace.roots.clone();

                workspace
                    .labels
                    .retain(|root, _| roots_snapshot.iter().any(|item| item == root));
                for root in &roots_snapshot {
                    workspace
                        .labels
                        .entry(root.clone())
                        .or_insert_with(|| derive_workspace_label(root));
                }

                workspace
                    .active_roots
                    .retain(|root| roots_snapshot.iter().any(|item| item == root));
                if workspace.active_roots.is_empty() {
                    if let Some(first_root) = roots_snapshot.first().cloned() {
                        workspace.active_roots.push(first_root);
                    }
                }

                workspace.clone()
            };

            emit_workspace_state_updates(app, &snapshot)?;
        }
        "electron-set-active-workspace-root" => {
            if let Some(root) = message.get("root").and_then(Value::as_str) {
                let root = normalize_root_string(root);
                let snapshot = {
                    let mut workspace = lock_or_err(&state.workspace_state, "workspace_state")?;
                    upsert_workspace_root(&mut workspace, root.clone());
                    workspace.active_roots = vec![root];
                    workspace.clone()
                };
                emit_workspace_state_updates(app, &snapshot)?;
            }
        }
        "electron-onboarding-skip-workspace" => {
            let result = match create_default_workspace_root() {
                Ok(path) => {
                    let root = normalize_root_path(&path);
                    let (label, snapshot) = {
                        let mut workspace = lock_or_err(&state.workspace_state, "workspace_state")?;
                        let label = upsert_workspace_root(&mut workspace, root.clone());
                        workspace.active_roots = vec![root.clone()];
                        (label, workspace.clone())
                    };
                    emit_workspace_root_option_picked(window, &root, &label)?;
                    emit_workspace_state_updates(app, &snapshot)?;
                    json!({
                        "type": "electron-onboarding-skip-workspace-result",
                        "success": true
                    })
                }
                Err(error) => json!({
                    "type": "electron-onboarding-skip-workspace-result",
                    "success": false,
                    "error": error
                }),
            };
            emit_message_to_window(window, result)?;
        }
        // Electron-specific side effects are safe to ignore in the compatibility host.
        "electron-set-badge-count"
        | "power-save-blocker-set"
        | "view-focused"
        | "set-telemetry-user"
        | "electron-set-window-mode" => {}
        "electron-window-focus-request" => {
            let is_focused = window.is_focused().unwrap_or(false);
            emit_message_to_window(
                window,
                json!({
                    "type": "electron-window-focus-changed",
                    "isFocused": is_focused
                }),
            )?;
        }
        "log-message" => {
            let level = message
                .get("level")
                .and_then(Value::as_str)
                .unwrap_or("info");
            let text = message
                .get("message")
                .and_then(Value::as_str)
                .unwrap_or_default();
            println!("[frontend:{level}] {text}");
        }
        other => {
            println!("[tauri-host] unhandled message type: {other}");
        }
    }

    Ok(())
}

#[tauri::command]
async fn send_message_from_view(
    app: tauri::AppHandle,
    window: Window,
    state: State<'_, AppState>,
    message: Value,
) -> Result<(), String> {
    handle_message(&app, &window, state.inner(), message).await
}

#[tauri::command]
async fn send_worker_message_from_view(
    worker_id: String,
    message: Value,
    window: Window,
) -> Result<(), String> {
    match message_type(&message).unwrap_or_default() {
        "worker-request" => {
            let request = message.get("request").cloned().unwrap_or_else(|| json!({}));
            let id = request.get("id").cloned().unwrap_or(Value::Null);
            let method = request
                .get("method")
                .cloned()
                .unwrap_or_else(|| Value::String("unknown".to_string()));

            emit_worker_to_window(
                &window,
                &worker_id,
                json!({
                    "type": "worker-response",
                    "workerId": worker_id,
                    "response": {
                        "id": id,
                        "method": method,
                        "result": {
                            "type": "error",
                            "error": {
                                "message": "Tauri worker bridge is not implemented for this worker yet."
                            }
                        }
                    }
                }),
            )?;
        }
        "worker-request-cancel" => {}
        other => {
            println!("[tauri-worker] unhandled worker message: {other}");
        }
    }

    Ok(())
}

#[tauri::command]
async fn show_context_menu(_items: Value) -> Result<ContextMenuResult, String> {
    Ok(ContextMenuResult { id: None })
}

#[tauri::command]
async fn trigger_sentry_test_error() -> Result<(), String> {
    eprintln!("[tauri-host] trigger_sentry_test_error invoked");
    Ok(())
}

#[tauri::command]
fn get_bridge_meta(state: State<'_, AppState>) -> BridgeMeta {
    state.bridge_meta.clone()
}

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .manage(AppState::new())
        .invoke_handler(tauri::generate_handler![
            send_message_from_view,
            send_worker_message_from_view,
            show_context_menu,
            trigger_sentry_test_error,
            get_bridge_meta
        ])
        .run(tauri::generate_context!())
        .expect("error while running tauri application");
}
