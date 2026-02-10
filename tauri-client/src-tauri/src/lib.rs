// This module provides a compatibility host for the migrated Codex webview.
// It keeps the Electron message shape so the existing frontend can boot in Tauri.

use base64::engine::general_purpose::STANDARD as BASE64_STANDARD;
use base64::Engine;
use reqwest::header::{HeaderMap, HeaderName, HeaderValue};
use reqwest::Method;
use serde::Serialize;
use serde_json::{json, Map, Value};
use std::collections::{HashMap, HashSet};
use std::env;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::Mutex;
use std::time::{SystemTime, UNIX_EPOCH};
use tauri::{Emitter, Manager, State, Window};
use url::Url;
use uuid::Uuid;

const CHANNEL_MESSAGE_FOR_VIEW: &str = "codex_desktop:message-for-view";
const VSCODE_FETCH_PREFIX: &str = "vscode://codex/";
const GLOBAL_KEY_ACTIVE_WORKSPACE_ROOTS: &str = "active-workspace-roots";
const GLOBAL_KEY_WORKSPACE_ROOT_OPTIONS: &str = "electron-saved-workspace-roots";
const GLOBAL_KEY_WORKSPACE_ROOT_LABELS: &str = "electron-workspace-root-labels";
const GLOBAL_KEY_PINNED_THREAD_IDS: &str = "pinned-thread-ids";

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

struct AppState {
    bridge_meta: BridgeMeta,
    persisted_atom_state: Mutex<Map<String, Value>>,
    shared_object_state: Mutex<Map<String, Value>>,
    shared_subscriptions: Mutex<HashMap<String, HashSet<String>>>,
    workspace_state: Mutex<WorkspaceState>,
    thread_store: Mutex<ThreadStore>,
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

fn handle_mcp_request(
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
            handle_mcp_request(app, window, state, &message)?;
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
