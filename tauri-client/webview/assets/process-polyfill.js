// Process polyfill for browser/Windows compatibility
(function() {
  if (typeof window.process === "object" && typeof window.process.cwd === "function") return;
  function detectPlatform() {
    var ua = navigator.userAgent.toLowerCase();
    if (ua.indexOf("win") !== -1) return "win32";
    if (ua.indexOf("mac") !== -1) return "darwin";
    if (ua.indexOf("linux") !== -1) return "linux";
    return "browser";
  }
  window.process = window.process || {
    cwd: function() { return "/"; },
    env: {},
    platform: detectPlatform(),
    version: "",
    versions: {},
    nextTick: function(fn) { setTimeout(fn, 0); }
  };
})();
